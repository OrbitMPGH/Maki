import { useCallback, useEffect, useRef } from 'react'
import { flushProgress, saveProgress } from '../../api/reader'
import type { ReadingClock } from './useReadingClock'

const DEBOUNCE_MS = 1500

/** How long a final flush waits for the save in flight before sending anyway. */
const FLUSH_CHAIN_WAIT_MS = 2000

/**
 * How often banked reading time is reported when nothing else is writing. Page turns carry it
 * along for free, so this only fires on a chapter being read without them: a long continuous
 * strip, or a page somebody is staring at.
 */
const HEARTBEAT_MS = 60_000

/**
 * Reports the reader's position and its reading time, debounced, and flushes on tab hide /
 * unmount so closing the browser mid-chapter still resumes in the right place. Positions are
 * absolute page indices; time is a delta of seconds since the last report, consumed from the
 * clock only when a request is actually being sent, so a swallowed failure loses it and nothing
 * double-counts it. Failures stay silent: losing a position must never interrupt reading.
 *
 * `complete` rides along on every write once set, so a completion never depends on the server
 * deriving it from the page index. The returned `settle` hands off to a caller about to write
 * the same chapter itself: it drops the pending write and waits out the one in flight, so two
 * completing writes never race each other into a double completion.
 */
export function useReaderProgress(
  chapterId: number | undefined,
  page: number,
  complete: boolean,
  enabled: boolean,
  clock: ReadingClock,
  onFlushed?: () => void,
) {
  const latest = useRef({ chapterId, page, complete })
  const pending = useRef(false)
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined)
  const inflight = useRef<Promise<void>>(Promise.resolve())
  // Where the chapter was opened, and whether anything has been written since. Opening is not a
  // reason to write: the position on record already is this one.
  const opened = useRef<{ chapterId: number; page: number; written: boolean } | undefined>(undefined)

  // Held in a ref so a caller passing an inline arrow does not restart the debounce and the
  // heartbeat on every render, which would mean the timers never actually fire.
  const flushedHandler = useRef(onFlushed)
  flushedHandler.current = onFlushed

  latest.current = { chapterId, page, complete }

  if (!enabled || !chapterId) {
    opened.current = undefined
  } else if (opened.current?.chapterId !== chapterId) {
    opened.current = { chapterId, page, written: false }
  }

  // Chained onto the previous send rather than tracked standalone: heartbeat and debounce saves
  // overlap, and tracking only the last one let an older, slower request land after a newer save
  // or after the final flush and regress the persisted position. Chaining serializes them, so
  // `settle()` draining `inflight.current` waits for every send still queued, in order.
  const send = useCallback(
    (id: number, at: number, done: boolean) => {
      // Seconds are pulled from the clock now, at the moment this send is decided, not once its
      // turn in the chain comes up: waiting would let time banked while queued behind an earlier
      // request bleed into this send instead of a later one.
      const seconds = clock.take()
      if (opened.current?.chapterId === id) opened.current.written = true
      inflight.current = inflight.current
        .then(() => saveProgress(id, at, done || undefined, seconds))
        .catch(() => {})
    },
    [clock],
  )

  useEffect(() => {
    if (!enabled || !chapterId) return

    const start = opened.current
    if (start && !start.written && start.page === page && !complete) {
      pending.current = false
      return
    }

    pending.current = true
    timer.current = setTimeout(() => {
      if (!pending.current) return
      pending.current = false
      send(chapterId, page, complete)
    }, DEBOUNCE_MS)

    return () => clearTimeout(timer.current)
  }, [chapterId, page, complete, enabled, send])

  useEffect(() => {
    if (!enabled) return

    const timer = setInterval(() => {
      const { chapterId: id, page: at, complete: done } = latest.current
      if (!id || clock.pending() === 0) return
      send(id, at, done)
    }, HEARTBEAT_MS)

    return () => clearInterval(timer)
  }, [enabled, clock, send])

  useEffect(() => {
    if (!enabled) return

    const flush = (direct: boolean) => {
      const { chapterId: id, page: at, complete: done } = latest.current
      // Banked seconds are worth a write on their own: this is the last chance to report the
      // stretch since the previous one, and a hidden tab may never come back.
      if (!id || (!pending.current && clock.pending() === 0)) return
      // The flush carries the latest position, so a debounce still armed would only repeat it.
      clearTimeout(timer.current)
      pending.current = false
      const seconds = clock.take()
      if (opened.current?.chapterId === id) opened.current.written = true
      const run = () =>
        flushProgress(id, at, done || undefined, seconds)
          .then(() => {
            // Only now has the write committed, so a refetch started any earlier could read the old state.
            flushedHandler.current?.()
          })
          .catch(() => {})
      // A hidden or closing page may be frozen before anything queued runs, so those send at once
      // (keepalive). Only the unmount flush goes after the save already in flight, so that one
      // cannot land later and put the older position back; a hung save is only waited on briefly.
      if (direct) {
        void run()
        return
      }
      inflight.current = Promise.race([
        inflight.current,
        new Promise<void>((resolve) => setTimeout(resolve, FLUSH_CHAIN_WAIT_MS)),
      ]).then(run)
    }

    const onVisibility = () => {
      if (document.visibilityState === 'hidden') flush(true)
    }

    document.addEventListener('visibilitychange', onVisibility)
    const onPageHide = () => flush(true)
    window.addEventListener('pagehide', onPageHide)
    return () => {
      document.removeEventListener('visibilitychange', onVisibility)
      window.removeEventListener('pagehide', onPageHide)
      flush(false)
    }
  }, [enabled, clock])

  const settle = useCallback(() => {
    clearTimeout(timer.current)
    pending.current = false
    return inflight.current
  }, [])

  return { settle }
}
