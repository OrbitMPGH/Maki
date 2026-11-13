import { memo, useMemo } from 'react'
import {
  IconBellOff,
  IconCircleCheckFilled,
  IconCloudOff,
  IconDeviceMobileCheck,
  IconEye,
  IconEyeOff,
} from '../../lib/icons'
import { Link } from 'react-router-dom'
import { Thumb } from './Thumb'
import type { SeriesDto } from '../../api/types'
import type { OfflineAvailability } from './CoverCard'
import {
  seriesDownloadStateVisual,
  seriesProgressVisual,
  seriesStatusVisual,
  statusColor,
} from './status'
import { LightBadge } from '../series/lightMantine'
import { useLabel } from '../../i18n-context'
import { Trans, useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'
import { cleanSynopsis, splitSynopsis } from '../../lib/synopsis'

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
  offline,
}: {
  series: SeriesDto
  selectMode: boolean
  selected: boolean
  /** Same meaning as on `CoverCard`: read progress is only shown when something tracks it. */
  readTracking: boolean
  density: 'compact' | 'default' | 'comfortable'
  onToggle: (id: number, shiftKey?: boolean) => void
  /** Same meaning as on `CoverCard`. */
  offline?: OfflineAvailability
}) {
  const renderLabel = useLabel()
  const { t } = useLingui()
  const synopsis = useMemo(() => splitSynopsis(cleanSynopsis(series.overview)).body, [series.overview])
  const status = seriesStatusVisual(series.status)
  const download = seriesDownloadStateVisual(series)
  const { total, nothingWanted, have, pct, complete, readPct, unread } = seriesProgressVisual(
    series,
    readTracking,
  )
  const { readChapterCount, newChapterCount = 0 } = series
  const readTip = t`${readChapterCount} of ${have} downloaded read`
  const monitoredTip = series.monitored ? t`Monitored` : t`Not monitored`
  const mutedTip = t`Notifications muted`
  const newTip = plural(newChapterCount, { one: '# recent chapter file', other: '# recent chapter files' })

  const thumbSize = density === 'compact' ? 48 : density === 'comfortable' ? 72 : 56

  return (
    <Link
      to={`/series/${series.id}`}
      className={`series-row ${density}`}
      data-selected={selected || undefined}
      data-offline={offline}
      onClick={(e) => {
        if (selectMode) {
          e.preventDefault()
          onToggle(series.id, e.shiftKey)
        }
      }}
    >
      {selectMode && <span className="row-check" data-checked={selected || undefined} />}

      <Thumb className="row-cover" src={series.coverUrl} size="lg" width={thumbSize} />

      <div className="row-body">
        <div className="row-header">
          {/* Tooltip keeps the canonical title, as on the poster card. */}
          <span className="row-title series-title" title={series.title}>
            {series.displayTitle}
          </span>
          {series.year && <span className="row-year">{series.year}</span>}
          {offline === 'saved' && (
            <LightBadge
              color="var(--ok)"
              data-offline-badge="saved"
              data-tip={t`Saved on this device`}
              leftSection={<IconDeviceMobileCheck size={12} />}
            >
              <span className="offline-badge-label">{t`Saved on this device`}</span>
            </LightBadge>
          )}
          {offline === 'unavailable' && (
            <LightBadge
              color="var(--neutral)"
              data-offline-badge="unavailable"
              data-tip={t`Not available offline`}
              leftSection={<IconCloudOff size={12} />}
            >
              <span className="offline-badge-label">{t`Not available offline`}</span>
            </LightBadge>
          )}
          <LightBadge color={statusColor(status.color)} leftSection={<status.Icon size={12} />}>
            {renderLabel(status.label)}
          </LightBadge>
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

        {synopsis && <div className="row-description">{synopsis}</div>}

        <div className="row-progress">
          {download && (
            <LightBadge color={statusColor(download.color)} leftSection={<download.Icon size={12} />}>
              {renderLabel(download.label)}
            </LightBadge>
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
          {/* The grid card's "N new" pill, or a plain "Read" once nothing downloaded is left. The
              ring is easy to miss at either density. */}
          {offline !== 'unavailable' && newChapterCount > 0 && (
            <LightBadge color="var(--neutral)" data-tip={newTip}>
              {plural(newChapterCount, { one: '# new', other: '# new' })}
            </LightBadge>
          )}
          {unread === 0 && (
            <LightBadge
              color="var(--brand)"
              leftSection={<IconCircleCheckFilled size={12} />}
              data-tip={t`All downloaded chapters read`}
            >
              <Trans>Read</Trans>
            </LightBadge>
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
                : total === 0
                  ? t`No source has listed this series' chapters yet, so there is no total to count against.`
                  : undefined
            }
          >
            {total > 0 ? `${have}/${total}` : have > 0 ? have : t`No chapters`}
          </span>
        </div>
      </div>
    </Link>
  )
})
