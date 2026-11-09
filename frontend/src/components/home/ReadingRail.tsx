import { memo, type CSSProperties } from 'react'
import { Link } from 'react-router-dom'
import { IconBookFilled } from '../../lib/icons'
import { coverChapterLabel } from '../../lib/chapterLabel'
import type { HomeReadingItem } from '../../api/hooks'
import { useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'
import { Rail } from '../ui/Rail'
import { ReadingCardMenu } from './ReadingCardMenu'

/**
 * Horizontal rail of "open this chapter" posters, for Home's Continue reading.
 *
 * Deliberately not `components/ui/CoverCard`: that takes a whole `SeriesDto` and links to the
 * series page, whereas these link straight into the reader and carry a chapter label rather than
 * download counts. It reuses that card's CSS classes, so the two match without new layout rules.
 */
export const ReadingRail = memo(function ReadingRail({ items }: { items: HomeReadingItem[] }) {
  return (
    <Rail>
      {items.map((item) => (
        <div
          key={item.chapterId}
          className={item.coverAccent ? 'discover-rail-item reading-card has-accent' : 'discover-rail-item reading-card'}
          style={item.coverAccent ? ({ '--series-accent': item.coverAccent } as CSSProperties) : undefined}
        >
          <ReadingCard item={item} />
          <ReadingCardMenu item={item} className="reading-card-menu" />
        </div>
      ))}
    </Rail>
  )
})

/**
 * One poster that opens the reader. `note` replaces the "# unread" count in the corner of the meta row.
 * Memoized: `item` keeps its reference across renders, so unrelated Home state does not re-render the cards.
 */
export const ReadingCard = memo(function ReadingCard({ item, note }: { item: HomeReadingItem; note?: string }) {
  const { t } = useLingui()
  // Kavita-imported rows carry no slice length, so there is no honest fraction to draw.
  const resumePct =
    item.pageCount > 0 ? Math.min(100, (item.page / item.pageCount) * 100) : null
  const { chapterLabel, unreadChapters, pageCount } = item
  const pageNumber = item.page + 1

  return (
    <Link to={`/read/${item.chapterId}`} className="cover-card">
      <div className="cover-poster">
        {item.coverUrl ? (
          <img src={item.coverUrl} alt="" loading="lazy" decoding="async" />
        ) : (
          <div className="cover-placeholder" aria-hidden>{item.seriesTitle}</div>
        )}
        <div className="cover-scrim" />

        <div className="cover-corners">
          <div className="cover-corner cover-corner-left">
            <span className="cover-chapter" title={chapterLabel}>
              <IconBookFilled size={10} />
              <span>{coverChapterLabel(chapterLabel)}</span>
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
            <span className="cover-row-verb">{item.page > 0 ? t`Resume` : t`Start`}</span>
            {note ? (
              <span className="cover-new">{note}</span>
            ) : (
              unreadChapters > 0 && (
                <span className="cover-new">
                  {plural(unreadChapters, { one: '# unread', other: '# unread' })}
                </span>
              )
            )}
          </div>
        </div>
      </div>
    </Link>
  )
})
