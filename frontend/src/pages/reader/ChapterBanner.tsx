import { useEffect, useState } from 'react'
import { Plural } from '@lingui/react/macro'

/** Fade in, hold, fade out. Kept in step with `--dur-banner` in theme.css. */
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
  coverUrl,
}: {
  seriesTitle: string
  label: string
  pageCount: number
  coverUrl?: string | null
}) {
  const [done, setDone] = useState(false)
  const [coverFailed, setCoverFailed] = useState(false)

  useEffect(() => {
    const timer = setTimeout(() => setDone(true), SHOW_MS)
    return () => clearTimeout(timer)
  }, [])

  if (done) return null

  return (
    <div className="reader-card reader-chapter-banner" role="status" aria-live="polite">
      {coverUrl && !coverFailed && (
        <img className="reader-card-cover" src={coverUrl} alt="" onError={() => setCoverFailed(true)} />
      )}
      <div className="reader-card-text">
        <span className="reader-card-eyebrow">{seriesTitle}</span>
        <span className="reader-card-title" data-wrap>
          {label}
        </span>
        <span className="reader-card-meta">
          <Plural value={pageCount} one="# page" other="# pages" />
        </span>
      </div>
    </div>
  )
}
