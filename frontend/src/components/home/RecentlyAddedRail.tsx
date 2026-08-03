import { Link, useNavigate } from 'react-router-dom'
import { IconBook } from '@tabler/icons-react'
import type { HomeRecentSeriesItem } from '../../api/hooks'

/** "3 hours ago", "2 days ago": coarse on purpose, since the rail is ordered, not a log. */
function relativeTime(iso: string): string {
  const then = new Date(iso).getTime()
  if (Number.isNaN(then)) return ''
  const minutes = Math.max(0, Math.round((Date.now() - then) / 60_000))
  if (minutes < 1) return 'just now'
  if (minutes < 60) return `${minutes}m ago`
  const hours = Math.round(minutes / 60)
  if (hours < 24) return `${hours}h ago`
  const days = Math.round(hours / 24)
  return days < 30 ? `${days}d ago` : `${Math.round(days / 30)}mo ago`
}

/**
 * Horizontal rail of series that recently gained chapter files. Cards go to the series page;
 * the small Read button jumps straight into the next unread chapter.
 */
export function RecentlyAddedRail({ items }: { items: HomeRecentSeriesItem[] }) {
  return (
    <div className="discover-rail">
      {items.map((item) => (
        <div key={item.seriesId} className="discover-rail-item">
          <RecentCard item={item} />
        </div>
      ))}
    </div>
  )
}

function RecentCard({ item }: { item: HomeRecentSeriesItem }) {
  const navigate = useNavigate()
  const openReader = (e: React.SyntheticEvent) => {
    e.preventDefault()
    e.stopPropagation()
    navigate(`/read/${item.readChapterId}`)
  }

  return (
    <Link to={`/series/${item.seriesId}`} className="cover-card" aria-label={item.seriesTitle}>
      <div className="cover-poster">
        {item.coverUrl ? (
          <img src={item.coverUrl} alt={item.seriesTitle} loading="lazy" decoding="async" />
        ) : (
          <div className="cover-placeholder">{item.seriesTitle}</div>
        )}
        <div className="cover-scrim" />

        <div className="cover-corner cover-corner-left">
          <span
            className="cover-badge cover-badge-unread"
            data-tip={`${item.newChapterCount} recent chapter file(s)`}
          >
            +{item.newChapterCount}
          </span>
        </div>

        {item.readChapterId != null && (
          <div className="cover-corner cover-corner-right">
            {/* Nested inside a Link, so this must not be an anchor of its own: it navigates
                imperatively and stops the outer card's navigation. */}
            <span
              className="cover-badge home-read-badge"
              role="button"
              tabIndex={0}
              data-tip="Read next chapter"
              onClick={openReader}
              onKeyDown={(e) => {
                if (e.key === 'Enter' || e.key === ' ') openReader(e)
              }}
            >
              <IconBook size={11} />
              Read
            </span>
          </div>
        )}

        <div className="cover-meta">
          <span className="cover-title" title={item.seriesTitle}>
            {item.seriesTitle}
          </span>
          <span className="home-chapter-label">
            {item.newestChapterLabel ?? 'New chapters'} · {relativeTime(item.addedAt)}
          </span>
        </div>
      </div>
    </Link>
  )
}
