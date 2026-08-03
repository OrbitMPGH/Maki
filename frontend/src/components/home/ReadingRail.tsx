import { Link } from 'react-router-dom'
import type { HomeReadingItem } from '../../api/hooks'

/**
 * Horizontal rail of "open this chapter" posters, for Home's Continue reading and Jump back in.
 *
 * Deliberately not `components/ui/CoverCard`: that takes a whole `SeriesDto` and links to the
 * series page, whereas these link straight into the reader and carry a chapter label rather than
 * download counts. It reuses that card's CSS classes, so the two match without new layout rules.
 */
export function ReadingRail({ items }: { items: HomeReadingItem[] }) {
  return (
    <div className="discover-rail">
      {items.map((item) => (
        <div key={item.chapterId} className="discover-rail-item">
          <ReadingCard item={item} />
        </div>
      ))}
    </div>
  )
}

function ReadingCard({ item }: { item: HomeReadingItem }) {
  // Kavita-imported rows carry no slice length, so there is no honest fraction to draw.
  const resumePct =
    item.pageCount > 0 ? Math.min(100, (item.page / item.pageCount) * 100) : null

  return (
    <Link
      to={`/read/${item.chapterId}`}
      className="cover-card"
      aria-label={`${item.seriesTitle} - ${item.chapterLabel}`}
    >
      <div className="cover-poster">
        {item.coverUrl ? (
          <img src={item.coverUrl} alt={item.seriesTitle} loading="lazy" decoding="async" />
        ) : (
          <div className="cover-placeholder">{item.seriesTitle}</div>
        )}
        <div className="cover-scrim" />

        {item.unreadChapters > 0 && (
          <div className="cover-corner cover-corner-left">
            <span
              className="cover-badge cover-badge-unread"
              data-tip={`${item.unreadChapters} unread`}
            >
              {item.unreadChapters}
            </span>
          </div>
        )}

        <div className="cover-meta">
          <span className="cover-title" title={item.seriesTitle}>
            {item.seriesTitle}
          </span>
          <span className="home-chapter-label">{item.chapterLabel}</span>
          {resumePct !== null && (
            <div className="home-resume-bar" data-tip={`Page ${item.page + 1} of ${item.pageCount}`}>
              <div className="home-resume-fill" style={{ width: `${resumePct}%` }} />
            </div>
          )}
        </div>
      </div>
    </Link>
  )
}
