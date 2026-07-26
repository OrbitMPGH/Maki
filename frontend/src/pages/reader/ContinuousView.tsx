import { useEffect, useRef } from 'react'
import type { ReaderFit } from './prefs'

const FIT_CLASS: Record<ReaderFit, string> = {
  width: 'reader-fit-width',
  height: 'reader-fit-height',
  screen: 'reader-fit-width',
  original: 'reader-fit-original',
}

/**
 * The webtoon strip: every page stacked, scrolled continuously. The current page is whichever
 * one owns the middle of the viewport, tracked with an IntersectionObserver rather than a scroll
 * handler so long chapters don't run layout maths on every frame.
 */
export default function ContinuousView({
  urls,
  page,
  onPageChange,
  fit,
  gap,
  label,
}: {
  urls: string[]
  page: number
  onPageChange: (page: number) => void
  fit: ReaderFit
  gap: number
  label: string
}) {
  const container = useRef<HTMLDivElement>(null)
  const pages = useRef<(HTMLImageElement | null)[]>([])
  // Only the *first* render of a chapter jumps to the saved position; afterwards the scroll
  // position is the source of truth and re-scrolling would fight the user.
  const jumped = useRef(false)

  useEffect(() => {
    jumped.current = false
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

  return (
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
    </div>
  )
}
