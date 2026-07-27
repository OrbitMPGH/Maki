import { memo } from 'react'
import { IconCircleCheckFilled, IconEye, IconEyeOff } from '@tabler/icons-react'
import { Link } from 'react-router-dom'
import type { SeriesDto } from '../../api/types'
import {
  BADGE_COLOR,
  seriesDownloadStateVisual,
  seriesProgressVisual,
  seriesStatusVisual,
} from './status'

/**
 * List-view card for the library — a horizontal row with cover thumbnail, metadata, and
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
  const { total, unmonitored, have, pct, complete, readPct } = seriesProgressVisual(
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
          <span
            className="cover-badge cover-badge-circle"
            data-dim={series.monitored || undefined}
            style={{ flexShrink: 0 }}
          >
            {series.monitored ? <IconEye size={12} /> : <IconEyeOff size={12} />}
          </span>
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
              style={{ '--ring-pct': `${readPct}%` } as React.CSSProperties}
            />
          )}
          <div className="row-bar">
            <div
              className="cover-bar-fill"
              data-complete={complete || undefined}
              style={{ width: `${pct}%` }}
            />
          </div>
          {complete && <IconCircleCheckFilled size={13} style={{ color: 'var(--ok)', flexShrink: 0 }} />}
          <span className="cover-count tnum" data-unmonitored={unmonitored || undefined}>
            {have}/{total || '?'}
          </span>
        </div>
      </div>
    </Link>
  )
})
