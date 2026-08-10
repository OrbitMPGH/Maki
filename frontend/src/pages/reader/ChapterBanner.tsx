import { useEffect, useState } from 'react'

/** Fade in, hold, fade out. Kept in step with the `reader-chapter-banner` animation in theme.css. */
const SHOW_MS = 2400

/**
 * The "you are now in a different chapter" cue. A chapter's credit pages and the next chapter's
 * opening pages are often the same art, so a page turn across the boundary can look like nothing
 * happened at all; the only other evidence is the page counter resetting, which is easy to miss.
 *
 * Remounted by `key={chapterId}` at the call site so re-entering a chapter restarts the animation
 * rather than leaving a finished one on screen.
 */
export default function ChapterBanner({
  seriesTitle,
  label,
  pageCount,
}: {
  seriesTitle: string
  label: string
  pageCount: number
}) {
  const [done, setDone] = useState(false)

  useEffect(() => {
    const timer = setTimeout(() => setDone(true), SHOW_MS)
    return () => clearTimeout(timer)
  }, [])

  if (done) return null

  return (
    <div className="reader-chapter-banner" role="status" aria-live="polite">
      <span className="reader-chapter-banner-series">{seriesTitle}</span>
      <span className="reader-chapter-banner-label">{label}</span>
      <span className="reader-chapter-banner-pages">
        {pageCount} {pageCount === 1 ? 'page' : 'pages'}
      </span>
    </div>
  )
}
