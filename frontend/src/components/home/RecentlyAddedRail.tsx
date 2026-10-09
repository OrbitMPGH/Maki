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
        <div key={item.seriesId} className="discover-rail-item reading-card">
          <RecentCard item={item} />
          {item.readChapterId != null && <ReadNextButton chapterId={item.readChapterId} />}
        </div>
      ))}
    </Rail>
  )
}

// A sibling of the card link rather than inside it: interactive content nested in an anchor is invalid.
function ReadNextButton({ chapterId }: { chapterId: number }) {
  const navigate = useNavigate()
  const { t } = useLingui()
  return (
    <button
      type="button"
      className="home-read-button"
      data-tip={t`Read next chapter`}
      aria-label={t`Read next chapter`}
      onClick={() => navigate(`/read/${chapterId}`)}
    >
      <IconPlayerPlayFilled size={10} />
    </button>
  )
}

// Memoized: `item` keeps its reference across renders, so unrelated Home state does not re-render the cards.
const RecentCard = memo(function RecentCard({ item }: { item: HomeRecentSeriesItem }) {
  const { newChapterCount } = item

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
        </div>

        <div className="cover-meta">
          <span className="cover-title" title={item.seriesTitle}>
            {item.seriesTitle}
          </span>
          <div className="cover-row">
            <span>{relativeTime(item.addedAt)}</span>
            <span
              className="cover-new cover-new-quiet"
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
