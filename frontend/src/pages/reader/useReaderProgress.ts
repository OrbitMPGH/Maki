import { useEffect, useRef } from 'react'
import { flushProgress, saveProgress } from '../../api/reader'
import type { UnlockedAchievement } from '../../api/reader'
import type { ReadingClock } from './useReadingClock'

const DEBOUNCE_MS = 1500

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
 */
export function useReaderProgress(
  chapterId: number | undefined,
  page: number,
  enabled: boolean,
  clock: ReadingClock,
  onUnlocked?: (unlocked: UnlockedAchievement[]) => void,
) {
  const latest = useRef({ chapterId, page })
  const pending = useRef(false)

  // Held in a ref so a caller passing an inline arrow does not restart the debounce and the
  // heartbeat on every render, which would mean the timers never actually fire.
  const unlockHandler = useRef(onUnlocked)
  unlockHandler.current = onUnlocked

  latest.current = { chapterId, page }

  const report = (unlocked: UnlockedAchievement[]) => {
    if (unlocked.length > 0) unlockHandler.current?.(unlocked)
  }

  useEffect(() => {
    if (!enabled || !chapterId) return

    pending.current = true
    const timer = setTimeout(() => {
      pending.current = false
      void saveProgress(chapterId, page, undefined, clock.take()).then(report).catch(() => {})
    }, DEBOUNCE_MS)

    return () => clearTimeout(timer)
  }, [chapterId, page, enabled, clock])

  useEffect(() => {
    if (!enabled) return

    const timer = setInterval(() => {
      const { chapterId: id, page: at } = latest.current
      if (!id || clock.pending() === 0) return
      void saveProgress(id, at, undefined, clock.take()).then(report).catch(() => {})
    }, HEARTBEAT_MS)

    return () => clearInterval(timer)
  }, [enabled, clock])

  useEffect(() => {
    if (!enabled) return

    const flush = () => {
      const { chapterId: id, page: at } = latest.current
      // Banked seconds are worth a write on their own: this is the last chance to report the
      // stretch since the previous one, and a hidden tab may never come back.
      if (!id || (!pending.current && clock.pending() === 0)) return
      pending.current = false
      void flushProgress(id, at, undefined, clock.take()).catch(() => {})
    }

    const onVisibility = () => {
      if (document.visibilityState === 'hidden') flush()
    }

    document.addEventListener('visibilitychange', onVisibility)
    window.addEventListener('pagehide', flush)
    return () => {
      document.removeEventListener('visibilitychange', onVisibility)
      window.removeEventListener('pagehide', flush)
      flush()
    }
  }, [enabled, clock])
}
