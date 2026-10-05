import { memo } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { IconPlayerPlayFilled } from '@tabler/icons-react'
import type { HomeRecentSeriesItem } from '../../api/hooks'
import { Rail } from '../ui/Rail'
import { relativeTime } from '../ui/time'
import { useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'

/**
 * Horizontal rail of series that recently gained chapter files. Cards go to the series page;
 * the small Read button jumps straight into the next unread chapter.
 */
export function RecentlyAddedRail({ items }: { items: HomeRecentSeriesItem[] }) {
  return (
    <Rail>
      {items.map((item) => (
        <div key={item.seriesId} className="discover-rail-item">
          <RecentCard item={item} />
        </div>
      ))}
    </Rail>
  )
}

// Memoized: `item` keeps its reference across renders, so unrelated Home state does not re-render the cards.
const RecentCard = memo(function RecentCard({ item }: { item: HomeRecentSeriesItem }) {
  const navigate = useNavigate()
  const { t } = useLingui()
  const { newChapterCount } = item
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

        <div className="cover-corners">
          {item.newestChapterLabel && (
            <div className="cover-corner cover-corner-left">
              <span className="cover-chapter">
                <span>{item.newestChapterLabel}</span>
              </span>
            </div>
          )}
          {item.readChapterId != null && (
            <div className="cover-corner cover-corner-right">
              {/* Nested inside a Link, so this must not be an anchor of its own: it navigates
                  imperatively and stops the outer card's navigation. */}
              <span
                className="home-read-button"
                role="button"
                tabIndex={0}
                data-tip={t`Read next chapter`}
                aria-label={t`Read next chapter`}
                onClick={openReader}
                onKeyDown={(e) => {
                  if (e.key === 'Enter' || e.key === ' ') openReader(e)
                }}
              >
                <IconPlayerPlayFilled size={10} />
              </span>
            </div>
          )}
        </div>

        <div className="cover-meta">
          <span className="cover-title" title={item.seriesTitle}>
            {item.seriesTitle}
          </span>
          <div className="cover-row">
            <span>{relativeTime(item.addedAt)}</span>
            <span
              className="cover-new"
              data-tip={plural(newChapterCount, {
                one: '# recent chapter file',
                other: '# recent chapter files',
              })}
            >
              {plural(newChapterCount, { one: '# new', other: '# new' })}
            </span>
          </div>
        </div>
      </div>
    </Link>
  )
})
