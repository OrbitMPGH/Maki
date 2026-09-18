import { Link, useNavigate } from 'react-router-dom'
import { IconBook } from '@tabler/icons-react'
import type { HomeRecentSeriesItem } from '../../api/hooks'
import { relativeTime } from '../ui/time'
import { Trans, useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'

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

        <div className="cover-corner cover-corner-left">
          <span
            className="cover-badge cover-badge-unread"
            data-tip={plural(newChapterCount, {
              one: '# recent chapter file',
              other: '# recent chapter files',
            })}
          >
            +{newChapterCount}
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
              data-tip={t`Read next chapter`}
              onClick={openReader}
              onKeyDown={(e) => {
                if (e.key === 'Enter' || e.key === ' ') openReader(e)
              }}
            >
              <IconBook size={11} />
              <Trans>Read</Trans>
            </span>
          </div>
        )}

        <div className="cover-meta">
          <span className="cover-title" title={item.seriesTitle}>
            {item.seriesTitle}
          </span>
          <span className="home-chapter-label">
            {item.newestChapterLabel ?? t`New chapters`} · {relativeTime(item.addedAt)}
          </span>
        </div>
      </div>
    </Link>
  )
}
