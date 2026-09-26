import { Link } from 'react-router-dom'
import { IconPlayerPlay } from '@tabler/icons-react'
import type { HomeReadingItem } from '../../api/hooks'
import { useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'
import { ReadingCardMenu, type ReadingRailKind } from './ReadingCardMenu'

/**
 * Horizontal rail of "open this chapter" posters, for Home's Continue reading and Jump back in.
 *
 * Deliberately not `components/ui/CoverCard`: that takes a whole `SeriesDto` and links to the
 * series page, whereas these link straight into the reader and carry a chapter label rather than
 * download counts. It reuses that card's CSS classes, so the two match without new layout rules.
 */
export function ReadingRail({ items, rail }: { items: HomeReadingItem[]; rail: ReadingRailKind }) {
  return (
    <div className="discover-rail">
      {items.map((item) => (
        <div key={item.chapterId} className="discover-rail-item reading-card">
          <ReadingCard item={item} />
          <ReadingCardMenu item={item} rail={rail} className="reading-card-menu" />
        </div>
      ))}
    </div>
  )
}

function ReadingCard({ item }: { item: HomeReadingItem }) {
  const { t } = useLingui()
  // Kavita-imported rows carry no slice length, so there is no honest fraction to draw.
  const resumePct =
    item.pageCount > 0 ? Math.min(100, (item.page / item.pageCount) * 100) : null
  const { seriesTitle, chapterLabel, unreadChapters, pageCount } = item
  const pageNumber = item.page + 1

  return (
    <Link
      to={`/read/${item.chapterId}`}
      className="cover-card"
      aria-label={t`${seriesTitle} - ${chapterLabel}`}
    >
      <div className="cover-poster">
        {item.coverUrl ? (
          <img src={item.coverUrl} alt={item.seriesTitle} loading="lazy" decoding="async" />
        ) : (
          <div className="cover-placeholder">{item.seriesTitle}</div>
        )}
        <div className="cover-scrim" />

        <span className="discover-corner" data-play="true" aria-hidden="true">
          <IconPlayerPlay size={18} />
        </span>

        {unreadChapters > 0 && (
          <div className="cover-corners">
            <div className="cover-corner cover-corner-left">
              <span
                className="cover-badge cover-badge-unread"
                data-tip={plural(unreadChapters, { one: '# unread', other: '# unread' })}
              >
                {unreadChapters}
              </span>
            </div>
          </div>
        )}

        <div className="cover-meta">
          <span className="cover-title" title={item.seriesTitle}>
            {item.seriesTitle}
          </span>
          <span className="home-chapter-label" data-action>
            <IconPlayerPlay size={10} />
            {item.page > 0 ? t`Resume` : t`Start`}
            <span className="home-chapter-sep" aria-hidden="true">
              {' · '}
            </span>
            {item.chapterLabel}
          </span>
          {resumePct !== null && (
            <div className="home-resume-bar" data-tip={t`Page ${pageNumber} of ${pageCount}`}>
              <div className="home-resume-fill" style={{ width: `${resumePct}%` }} />
            </div>
          )}
        </div>
      </div>
    </Link>
  )
}
