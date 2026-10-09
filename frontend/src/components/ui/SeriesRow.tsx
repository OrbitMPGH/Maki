import { memo } from 'react'
import { IconBellOff, IconCircleCheckFilled, IconEye, IconEyeOff } from '@tabler/icons-react'
import { Link } from 'react-router-dom'
import type { SeriesDto } from '../../api/types'
import {
  badgeFill,
  seriesDownloadStateVisual,
  seriesProgressVisual,
  seriesStatusVisual,
} from './status'
import { useLabel } from '../../i18n-context'
import { Trans, useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'

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
  const renderLabel = useLabel()
  const { t } = useLingui()
  const status = seriesStatusVisual(series.status)
  const download = seriesDownloadStateVisual(series)
  const { total, nothingWanted, have, pct, complete, readPct, unread } = seriesProgressVisual(
    series,
    readTracking,
  )
  const { readChapterCount } = series
  const readTip = t`${readChapterCount} of ${have} downloaded read`
  const monitoredTip = series.monitored ? t`Monitored` : t`Not monitored`
  const mutedTip = t`Notifications muted`
  const unreadTip = unread ? plural(unread, { one: '# unread', other: '# unread' }) : ''

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
          <img src={series.coverUrl} alt="" loading="lazy" decoding="async" />
        ) : (
          <div className="row-cover-placeholder">{series.displayTitle}</div>
        )}
      </div>

      <div className="row-body">
        <div className="row-header">
          {/* Tooltip keeps the canonical title, as on the poster card. */}
          <span className="row-title" title={series.title}>
            {series.displayTitle}
          </span>
          {series.year && <span className="row-year">{series.year}</span>}
          <span
            className="cover-badge"
            style={{ ...badgeFill(status.color), flexShrink: 0 }}
          >
            <status.Icon size={11} />
            {renderLabel(status.label)}
          </span>
          {/* Monitor state, same as the grid card: a subtle eye when watched, a clear eye-off
              when not. Icon-only, so the tooltip is the only thing that names it. */}
          <span
            className="cover-badge cover-badge-circle"
            data-dim={series.monitored || undefined}
            data-tip={monitoredTip}
            role="img"
            aria-label={monitoredTip}
            style={{ flexShrink: 0 }}
          >
            {series.monitored ? <IconEye size={12} /> : <IconEyeOff size={12} />}
          </span>
          {/* Only when muted, same rule as the grid card. */}
          {series.notificationMode === 'Muted' && (
            <span
              className="cover-badge cover-badge-circle"
              data-dim
              data-tip={mutedTip}
              role="img"
              aria-label={mutedTip}
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
            <span className="cover-badge" style={{ ...badgeFill(download.color), flexShrink: 0 }}>
              <download.Icon size={11} />
              {renderLabel(download.label)}
            </span>
          )}
          {readPct !== null && (
            <span
              className="cover-ring"
              data-tip={readTip}
              role="img"
              aria-label={readTip}
              style={{ '--ring-pct': `${readPct}%` } as React.CSSProperties}
            />
          )}
          {/* Same pair the grid card shows: the outstanding count, or a plain "Read" once none
              are left. The ring is easy to miss at either density. */}
          {unread !== null && unread > 0 && (
            <span
              className="cover-badge cover-badge-unread"
              data-tip={unreadTip}
              role="img"
              aria-label={unreadTip}
              style={{ flexShrink: 0 }}
            >
              {unread}
            </span>
          )}
          {unread === 0 && (
            <span
              className="cover-badge cover-badge-read"
              data-tip={t`All downloaded chapters read`}
              style={{ flexShrink: 0 }}
            >
              <IconCircleCheckFilled size={11} />
              <Trans>Read</Trans>
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
                ? plural(total, {
                    one: '# chapter listed, none wanted, nothing will download',
                    other: '# chapters listed, none wanted, nothing will download',
                  })
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
