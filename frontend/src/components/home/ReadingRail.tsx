import { memo } from 'react'
import { Link } from 'react-router-dom'
import { IconPlayerPlayFilled } from '@tabler/icons-react'
import type { HomeReadingItem } from '../../api/hooks'
import { useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'
import { Rail } from '../ui/Rail'
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
    <Rail>
      {items.map((item) => (
        <div key={item.chapterId} className="discover-rail-item reading-card">
          <ReadingCard item={item} />
          <ReadingCardMenu item={item} rail={rail} className="reading-card-menu" />
        </div>
      ))}
    </Rail>
  )
}

// Memoized: `item` keeps its reference across renders, so unrelated Home state does not re-render the cards.
const ReadingCard = memo(function ReadingCard({ item }: { item: HomeReadingItem }) {
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

        <div className="cover-corners">
          <div className="cover-corner cover-corner-left">
            <span className="cover-chapter">
              <IconPlayerPlayFilled size={10} />
              <span>{chapterLabel}</span>
            </span>
          </div>
        </div>

        <div className="cover-meta">
          <span className="cover-title" title={item.seriesTitle}>
            {item.seriesTitle}
          </span>
          {resumePct !== null && (
            <div className="home-resume-bar" data-tip={t`Page ${pageNumber} of ${pageCount}`}>
              <div className="home-resume-fill" style={{ width: `${resumePct}%` }} />
            </div>
          )}
          <div className="cover-row">
            <span>{item.page > 0 ? t`Resume` : t`Start`}</span>
            {unreadChapters > 0 && (
              <span className="cover-new">
                {plural(unreadChapters, { one: '# new', other: '# new' })}
              </span>
            )}
          </div>
        </div>
      </div>
    </Link>
  )
})
