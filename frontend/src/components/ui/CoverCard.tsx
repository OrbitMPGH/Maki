import { memo } from 'react'
import {
  IconAlertTriangle,
  IconBellOff,
  IconCheck,
  IconClock,
  IconDownload,
  IconEye,
  IconEyeOff,
} from '@tabler/icons-react'
import { Link } from 'react-router-dom'
import type { SeriesDto } from '../../api/types'
import { seriesDownloadStateVisual, seriesProgressVisual } from './status'
import { useLabel } from '../../i18n-context'
import { useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'

/**
 * Poster card for the library grid: cover art is the hero, with a bottom
 * scrim carrying the title, a download-progress bar and counts. Doubles as a
 * selection target in bulk mode.
 *
 * Deliberately built from plain elements + CSS classes rather than Mantine's Badge/Tooltip/
 * RingProgress/Checkbox: a library grid mounts hundreds of these at once, and each Mantine
 * component carries styles-api resolution per instance (and Tooltip a floating-ui instance),
 * which is what made a big library jerky to scroll. Same reason there is no `backdrop-filter`
 * on the badges: each one is a compositor layer the browser re-samples every scrolled frame.
 *
 * Memoized, so a keystroke in the library filter doesn't reconcile every card. Keep the props
 * stable at the call site (`onToggle` takes the id so one callback serves the whole grid).
 */
export const CoverCard = memo(function CoverCard({
  series,
  selectMode,
  selected,
  readTracking,
  onToggle,
}: {
  series: SeriesDto
  selectMode: boolean
  selected: boolean
  /**
   * Whether read progress is meaningful: Kavita is connected, or the built-in reader has been
   * used. Hides the read ring otherwise, so a stale ReadingState row from a Kavita connection
   * that has since been removed doesn't linger on the card.
   */
  readTracking: boolean
  onToggle: (id: number) => void
}) {
  const renderLabel = useLabel()
  const { t } = useLingui()
  const download = seriesDownloadStateVisual(series)
  // Shared with the list row (`SeriesRow`) so the two views can never report different numbers
  // for the same series.
  const { total, nothingWanted, have, pct, complete, readPct, unread } = seriesProgressVisual(
    series,
    readTracking,
  )
  const { readChapterCount } = series
  const missing = download || nothingWanted ? 0 : Math.max(0, total - have)
  const totalLabel = total || '?'
  const stateTip = download
    ? renderLabel(download.label)
    : missing > 0
      ? plural(missing, { one: '# chapter missing', other: '# chapters missing' })
      : null
  const stateTone = download ? (series.downloadingCount > 0 ? 'info' : undefined) : 'warn'

  return (
    <Link
      to={`/series/${series.id}`}
      className="cover-card"
      data-selected={selected || undefined}
      onClick={(e) => {
        if (selectMode) {
          e.preventDefault()
          onToggle(series.id)
        }
      }}
    >
      <div className="cover-poster">
        {series.coverUrl ? (
          <img src={series.coverUrl} alt={series.displayTitle} loading="lazy" decoding="async" />
        ) : (
          <div className="cover-placeholder">{series.displayTitle}</div>
        )}
        <div className="cover-scrim" />

        {selectMode && <span className="cover-check" data-checked={selected || undefined} />}

        <div className="cover-corners">
          <div className="cover-corner cover-corner-left">
            {readPct !== null && readPct > 0 && (
              <span
                className="cover-ring"
                data-complete={unread === 0 || undefined}
                data-tip={
                  unread === 0
                    ? t`All downloaded chapters read`
                    : t`${readChapterCount} of ${have} downloaded read`
                }
                style={{ '--ring-pct': `${readPct}%` } as React.CSSProperties}
              >
                {unread === 0 && <IconCheck size={14} stroke={2.2} className="cover-ring-check" />}
              </span>
            )}
            {/* At most one chip, and only when there is something to act on. */}
            {stateTip && (
              <span
                className="cover-state"
                data-tone={stateTone}
                data-tip={stateTip}
                role="img"
                aria-label={stateTip}
              >
                {download ? (
                  series.downloadingCount > 0 ? (
                    <IconDownload size={13} stroke={2} />
                  ) : (
                    <IconClock size={13} stroke={2} />
                  )
                ) : (
                  <IconAlertTriangle size={13} stroke={2} />
                )}
              </span>
            )}
          </div>

          <div className="cover-corner cover-corner-right">
            {/* Monitor state on every card: a subtle eye when watched, a clear eye-off when not. */}
            <span
              className="cover-badge cover-badge-circle"
              data-dim={series.monitored || undefined}
              data-tip={series.monitored ? t`Monitored` : t`Not monitored`}
            >
              {series.monitored ? <IconEye size={15} /> : <IconEyeOff size={15} />}
            </span>
            {/* Only when muted: the other three modes are the normal case and would be noise. */}
            {series.notificationMode === 'Muted' && (
              <span
                className="cover-badge cover-badge-circle"
                data-dim
                data-tip={t`Notifications muted`}
              >
                <IconBellOff size={15} />
              </span>
            )}
          </div>
        </div>

        <div className="cover-meta">
          {/* The tooltip stays the canonical title, so the name the folder on disk and search
              use is still reachable when a language preference has moved the label off it. */}
          <span className="cover-title" title={series.title}>
            {series.displayTitle}
          </span>
          <div className="cover-bar">
            <div
              className="cover-bar-fill"
              data-complete={complete || undefined}
              style={{ width: `${pct}%` }}
            />
          </div>
          <div className="cover-row">
            <span
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
              {t`${have} of ${totalLabel}`}
            </span>
            {unread !== null && unread > 0 && (
              <span className="cover-new">{plural(unread, { one: '# new', other: '# new' })}</span>
            )}
          </div>
        </div>
      </div>
    </Link>
  )
})
