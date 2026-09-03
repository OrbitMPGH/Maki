import { memo } from 'react'
import { IconBellOff, IconCircleCheckFilled, IconEyeOff } from '@tabler/icons-react'
import { Link } from 'react-router-dom'
import type { SeriesDto } from '../../api/types'
import {
  BADGE_COLOR,
  seriesDownloadStateVisual,
  seriesProgressVisual,
  seriesStatusVisual,
} from './status'

/**
 * List-view card for the library: a horizontal row with cover thumbnail, metadata, and
 * download/read progress. Dense enough to scan quickly, informative enough to replace the
 * grid when the user prefers a list. Same memo + plain-element strategy as CoverCard.
 */
export const SeriesRow = memo(function SeriesRow({
  series,
  selectMode,
  selected,
  readTracking,
  density,
  onToggle,
}: {
  series: SeriesDto
  selectMode: boolean
  selected: boolean
  /** Same meaning as on `CoverCard`: read progress is only shown when something tracks it. */
  readTracking: boolean
  density: 'compact' | 'default' | 'comfortable'
  onToggle: (id: number) => void
}) {
  const status = seriesStatusVisual(series.status)
  const download = seriesDownloadStateVisual(series)
  const { total, nothingWanted, have, pct, complete, readPct, unread } = seriesProgressVisual(
    series,
    readTracking,
  )

  const thumbSize = density === 'compact' ? 48 : density === 'comfortable' ? 72 : 56
  const thumbH = thumbSize * 1.5

  return (
    <Link
      to={`/series/${series.id}`}
      className={`series-row ${density}`}
      data-selected={selected || undefined}
      onClick={(e) => {
        if (selectMode) {
          e.preventDefault()
          onToggle(series.id)
        }
      }}
    >
      {selectMode && <span className="row-check" data-checked={selected || undefined} />}

      <div
        className="row-cover"
        style={{ width: thumbSize, height: thumbH, flexShrink: 0 }}
      >
        {series.coverUrl ? (
          <img src={series.coverUrl} alt={series.title} loading="lazy" decoding="async" />
        ) : (
          <div className="row-cover-placeholder">{series.title}</div>
        )}
      </div>

      <div className="row-body">
        <div className="row-header">
          <span className="row-title" title={series.title}>
            {series.title}
          </span>
          {series.year && <span className="row-year">{series.year}</span>}
          <span
            className="cover-badge"
            style={{ background: BADGE_COLOR[status.color], flexShrink: 0 }}
          >
            <status.Icon size={11} />
            {status.label}
          </span>
          {/* Same rule as the grid card: shown only when a series is not monitored, since
              monitored is the normal case. Icon-only, so the tooltip is the only thing naming it. */}
          {!series.monitored && (
            <span
              className="cover-badge cover-badge-circle"
              data-tip="Not monitored"
              style={{ flexShrink: 0 }}
            >
              <IconEyeOff size={12} />
            </span>
          )}
          {/* Only when muted, same rule as the grid card. */}
          {series.notificationMode === 'Muted' && (
            <span
              className="cover-badge cover-badge-circle"
              data-dim
              data-tip="Notifications muted"
              style={{ flexShrink: 0 }}
            >
              <IconBellOff size={12} />
            </span>
          )}
        </div>

        {series.overview && (
          <div className="row-description">{series.overview}</div>
        )}

        <div className="row-progress">
          {download && (
            <span className="cover-badge" style={{ background: BADGE_COLOR[download.color], flexShrink: 0 }}>
              <download.Icon size={11} />
              {download.label}
            </span>
          )}
          {readPct !== null && (
            <span
              className="cover-ring"
              data-tip={`${series.readChapterCount} of ${have} downloaded read`}
              style={{ '--ring-pct': `${readPct}%` } as React.CSSProperties}
            />
          )}
          {/* Same pair the grid card shows: the outstanding count, or a plain "Read" once none
              are left. The ring is easy to miss at either density. */}
          {unread !== null && unread > 0 && (
            <span
              className="cover-badge cover-badge-unread"
              data-tip={`${unread} unread`}
              style={{ flexShrink: 0 }}
            >
              {unread}
            </span>
          )}
          {unread === 0 && (
            <span
              className="cover-badge cover-badge-read"
              data-tip="All downloaded chapters read"
              style={{ flexShrink: 0 }}
            >
              <IconCircleCheckFilled size={11} />
              Read
            </span>
          )}
          <div className="row-bar">
            <div
              className="cover-bar-fill"
              data-complete={complete || undefined}
              style={{ width: `${pct}%` }}
            />
          </div>
          {complete && <IconCircleCheckFilled size={13} style={{ color: 'var(--ok)', flexShrink: 0 }} />}
          <span
            className="cover-count tnum"
            data-nothing-wanted={nothingWanted || undefined}
            data-tip={
              nothingWanted
                ? `${total} chapter(s) listed, none wanted, nothing will download`
                : undefined
            }
          >
            {have}/{total || '?'}
          </span>
        </div>
      </div>
    </Link>
  )
})
