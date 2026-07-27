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
 * Poster card for the library grid — cover art is the hero, with a bottom
 * scrim carrying the title, a download-progress bar and status. Doubles as a
 * selection target in bulk mode.
 *
 * Deliberately built from plain elements + CSS classes rather than Mantine's Badge/Tooltip/
 * RingProgress/Checkbox: a library grid mounts hundreds of these at once, and each Mantine
 * component carries styles-api resolution per instance (and Tooltip a floating-ui instance),
 * which is what made a big library jerky to scroll. Same reason there is no `backdrop-filter`
 * on the badges — each one is a compositor layer the browser re-samples every scrolled frame.
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
  const status = seriesStatusVisual(series.status)
  const download = seriesDownloadStateVisual(series)
  // Shared with the list row (`SeriesRow`) so the two views can never report different numbers
  // for the same series. Read progress is its own ring badge rather than a second number/marker
  // sharing the download bar — a second tnum count next to have/total blurred together, and a
  // marker on the same bar read as a glitch more than a stat. A ring is a distinct-enough shape
  // not to compete visually.
  const { total, unmonitored, have, pct, complete, readPct, unread } = seriesProgressVisual(
    series,
    readTracking,
  )

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
          <img src={series.coverUrl} alt={series.title} loading="lazy" decoding="async" />
        ) : (
          <div className="cover-placeholder">{series.title}</div>
        )}
        <div className="cover-scrim" />

        {selectMode && <span className="cover-check" data-checked={selected || undefined} />}

        <div className="cover-corner cover-corner-left">
          {/* In-flight download work. Absent when the series is idle. */}
          {download && (
            <span className="cover-badge" style={{ background: BADGE_COLOR[download.color] }}>
              <download.Icon size={11} />
              {download.label}
            </span>
          )}
          {/* How far into the downloaded chapters you've read — its own ring rather than a
              number competing with the have/total count below. Absent unless Kavita is
              configured and has actually reported reading progress for this series. */}
          {readPct !== null && (
            <span
              className="cover-ring"
              data-tip={`${series.readChapterCount} of ${have} downloaded read`}
              style={{ '--ring-pct': `${readPct}%` } as React.CSSProperties}
            />
          )}
          {unread !== null && unread > 0 && (
            <span className="cover-badge cover-badge-unread" data-tip={`${unread} unread`}>
              {unread}
            </span>
          )}
          {unread === 0 && (
            <span className="cover-badge cover-badge-read" data-tip="All downloaded chapters read">
              <IconCircleCheckFilled size={11} />
              Read
            </span>
          )}
        </div>

        <div className="cover-corner cover-corner-right">
          {/* Monitor state on every card: a subtle eye when watched, a clear eye-off when not. */}
          <span
            className="cover-badge cover-badge-circle"
            data-dim={series.monitored || undefined}
            data-tip={series.monitored ? 'Monitored' : 'Not monitored'}
          >
            {series.monitored ? <IconEye size={12} /> : <IconEyeOff size={12} />}
          </span>
          <span className="cover-badge" style={{ background: BADGE_COLOR[status.color] }}>
            <status.Icon size={11} />
            {status.label}
          </span>
        </div>

        <div className="cover-meta">
          <span className="cover-title" title={series.title}>
            {series.title}
          </span>
          <div className="cover-progress-row">
            <div className="cover-bar">
              <div
                className="cover-bar-fill"
                data-complete={complete || undefined}
                style={{ width: `${pct}%` }}
              />
            </div>
            {complete && <IconCircleCheckFilled size={13} style={{ color: 'var(--ok)' }} />}
            <span
              className="cover-count tnum"
              data-unmonitored={unmonitored || undefined}
              data-tip={
                unmonitored
                  ? `${total} chapter(s) known, none monitored — nothing will download`
                  : undefined
              }
            >
              {have}/{total || '?'}
            </span>
          </div>
        </div>
      </div>
    </Link>
  )
})
