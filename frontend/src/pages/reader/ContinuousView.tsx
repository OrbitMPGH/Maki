import { useEffect, useRef, useState } from 'react'
import type { ReaderFit } from './prefs'

const FIT_CLASS: Record<ReaderFit, string> = {
  width: 'reader-fit-width',
  height: 'reader-fit-height',
  screen: 'reader-fit-width',
  original: 'reader-fit-original',
}

// Pixels of downward scroll (wheel delta / touch drag) needed at the bottom to trigger the next
// chapter. Sized for a couple of scroll-wheel notches, not a long hold.
const PAST_END_THRESHOLD = 1000
// How close to the true bottom counts as "at the bottom" — scrollHeight/clientHeight are
// fractional in some browsers, so an exact `=== ` check misses by sub-pixel amounts.
const BOTTOM_EPSILON = 2

/**
 * The webtoon strip: every page stacked, scrolled continuously. The current page is whichever
 * one owns the middle of the viewport, tracked with an IntersectionObserver rather than a scroll
 * handler so long chapters don't run layout maths on every frame.
 */
export default function ContinuousView({
  urls,
  page,
  onPageChange,
  onPastEnd,
  hasNext,
  fit,
  gap,
  label,
}: {
  urls: string[]
  page: number
  onPageChange: (page: number) => void
  /** Fired once the bottom-of-strip progress bar fills — the strip's analogue of turning past the
   *  last page in paged mode. */
  onPastEnd: () => void
  /** Gates the "scroll for next chapter" prompt — there's nothing to scroll into on the last
   *  chapter, so the strip just ends. */
  hasNext: boolean
  fit: ReaderFit
  gap: number
  label: string
}) {
  const container = useRef<HTMLDivElement>(null)
  const pages = useRef<(HTMLImageElement | null)[]>([])
  const sentinel = useRef<HTMLDivElement>(null)
  // Only the *first* render of a chapter jumps to the saved position; afterwards the scroll
  // position is the source of truth and re-scrolling would fight the user.
  const jumped = useRef(false)
  // Mirrors `pastEndProgress` state without the render lag, so consecutive wheel/touch events in
  // the same frame accumulate correctly instead of each reading a stale 0.
  const progress = useRef(0)
  const [pastEndProgress, setPastEndProgress] = useState(0)

  useEffect(() => {
    jumped.current = false
    progress.current = 0
    setPastEndProgress(0)
  }, [urls])

  useEffect(() => {
    if (jumped.current || urls.length === 0 || page === 0) {
      jumped.current = true
      return
    }
    pages.current[page]?.scrollIntoView({ block: 'start' })
    jumped.current = true
  }, [urls, page])

  useEffect(() => {
    if (urls.length === 0) return

    // Track the whole intersecting set and take the topmost, rather than whichever entry the
    // callback happened to see last. While images are still loading they have no height yet, so
    // several stack inside the band at once and "last wins" reports a page further down than the
    // one actually on screen.
    const intersecting = new Set<number>()
    const observer = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          const index = Number((entry.target as HTMLElement).dataset.page)
          if (!Number.isFinite(index)) continue
          if (entry.isIntersecting) intersecting.add(index)
          else intersecting.delete(index)
        }
        if (intersecting.size > 0) onPageChange(Math.min(...intersecting))
      },
      // A thin band across the middle of the VIEWPORT. Not the content element: that isn't the
      // scroller (`.reader-surface` is), so a percentage rootMargin against it would carve a band
      // out of the middle of the whole chapter and report page ~10 of 20 while page 1 is on screen.
      { rootMargin: '-45% 0px -45% 0px', threshold: 0 },
    )

    for (const element of pages.current) {
      if (element) observer.observe(element)
    }
    return () => observer.disconnect()
  }, [urls, onPageChange])

  // The band tracker above answers "what's in the middle of the screen", which a short last page
  // (e.g. a small end-of-chapter credit image) can fail to ever reach — it never crosses the
  // center band, so the count sticks on the previous, taller page even once the strip is fully
  // scrolled. A 1px sentinel right after the last page catches that: it enters the viewport only
  // once the strip is scrolled essentially to its end, at which point the last page is current
  // regardless of the band.
  useEffect(() => {
    if (urls.length === 0 || !sentinel.current) return
    const target = sentinel.current
    const observer = new IntersectionObserver(
      (entries) => {
        if (entries[0]?.isIntersecting) onPageChange(urls.length - 1)
      },
      { threshold: 0 },
    )
    observer.observe(target)
    return () => observer.disconnect()
  }, [urls, onPageChange])

  // The bottom-of-strip "scroll for next chapter" meter. `.reader-surface` clamps scrollTop at
  // the true max — there's no native overscroll to detect — so instead this reads wheel/touch
  // deltas directly and only counts them while already at the bottom, the same way Kavita's
  // reader does it.
  useEffect(() => {
    if (!hasNext || urls.length === 0) return
    const scroller = container.current?.parentElement
    if (!scroller) return

    const atBottom = () =>
      scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight < BOTTOM_EPSILON

    const advance = (delta: number) => {
      if (delta <= 0 || !atBottom()) {
        if (progress.current !== 0) {
          progress.current = 0
          setPastEndProgress(0)
        }
        return
      }
      progress.current = Math.min(PAST_END_THRESHOLD, progress.current + delta)
      setPastEndProgress(progress.current / PAST_END_THRESHOLD)
      if (progress.current >= PAST_END_THRESHOLD) {
        progress.current = 0
        setPastEndProgress(0)
        onPastEnd()
      }
    }

    const onWheel = (event: WheelEvent) => advance(event.deltaY)

    let touchY: number | null = null
    const onTouchStart = (event: TouchEvent) => {
      touchY = event.touches[0]?.clientY ?? null
    }
    const onTouchMove = (event: TouchEvent) => {
      const y = event.touches[0]?.clientY
      if (y == null || touchY == null) return
      advance(touchY - y)
      touchY = y
    }
    const onTouchEnd = () => {
      touchY = null
    }

    scroller.addEventListener('wheel', onWheel, { passive: true })
    scroller.addEventListener('touchstart', onTouchStart, { passive: true })
    scroller.addEventListener('touchmove', onTouchMove, { passive: true })
    scroller.addEventListener('touchend', onTouchEnd, { passive: true })
    return () => {
      scroller.removeEventListener('wheel', onWheel)
      scroller.removeEventListener('touchstart', onTouchStart)
      scroller.removeEventListener('touchmove', onTouchMove)
      scroller.removeEventListener('touchend', onTouchEnd)
    }
  }, [urls, hasNext, onPastEnd])

  return (
    <>
      <div className="reader-continuous" ref={container} style={{ gap: `${gap}px` }}>
        {urls.map((src, index) => (
          <img
            key={src}
            ref={(element) => {
              pages.current[index] = element
            }}
            data-page={index}
            src={src}
            alt={`${label} — page ${index + 1}`}
            className={`reader-page ${FIT_CLASS[fit]}`}
            loading={index < 3 ? 'eager' : 'lazy'}
            decoding="async"
            draggable={false}
          />
        ))}
        <div ref={sentinel} style={{ height: 1 }} />
      </div>
      {pastEndProgress > 0 && (
        <div className="reader-next-chapter-hint">
          <span>Scroll for next chapter</span>
          <div className="reader-next-chapter-bar">
            <div
              className="reader-next-chapter-fill"
              style={{ width: `${Math.min(1, pastEndProgress) * 100}%` }}
            />
          </div>
        </div>
      )}
    </>
  )
}
