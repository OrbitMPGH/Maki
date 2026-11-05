import { memo } from 'react'
import {
  IconBellOff,
  IconDeviceMobileCheck,
  IconDownload,
  IconEyeOff,
} from '../../lib/icons'
import { Link } from 'react-router-dom'
import type { SeriesDto } from '../../api/types'
import { seriesDownloadStateVisual, seriesProgressVisual, seriesStatusGlyph, seriesStatusVisual, statusColor, statusOnColor } from './status'
import { coverSrcSet, COVER_CARD_SIZES } from '../../lib/coverSrcSet'
import { useLabel } from '../../i18n-context'
import { useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'
import { CoverCardMenu } from './CoverCardMenu'

/** What a library entry offers while the server is out of reach. */
export type OfflineAvailability = 'saved' | 'unavailable'

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
  offline,
  menu = false,
}: {
  series: SeriesDto
  selectMode: boolean
  selected: boolean
  /**
   * Only while offline: `saved` marks a series with chapters on this device, `unavailable` one whose
   * page this device holds no copy of, which is dimmed and says so in place of its chapter count.
   */
  offline?: OfflineAvailability
  /**
   * Whether read progress is meaningful: Kavita is connected, or the built-in reader has been
   * used. Hides the read ring otherwise, so a stale ReadingState row from a Kavita connection
   * that has since been removed doesn't linger on the card.
   */
  readTracking: boolean
  onToggle: (id: number) => void
  /** The library grid's menu button. Hidden in select mode and offline, where its actions cannot run. */
  menu?: boolean
}) {
  const renderLabel = useLabel()
  const { t } = useLingui()
  const download = seriesDownloadStateVisual(series)
  const status = seriesStatusVisual(series.status)
  const statusLabel = renderLabel(status.label)
  const StatusGlyph = seriesStatusGlyph(series.status)
  // Shared with the list row (`SeriesRow`) so the two views can never report different numbers
  // for the same series.
  const { total, nothingWanted, have, pct, complete, readPct, unread } = seriesProgressVisual(
    series,
    readTracking,
  )
  const { readChapterCount } = series
  // No total at all means no source has listed a chapter yet; "0 of ?" left that to be guessed.
  // With the "N new" pill beside it the long form ran out of room ("30 of ..."), so it goes compact.
  const showsNew = offline !== 'unavailable' && unread !== null && unread > 0
  const countLabel =
    total > 0
      ? showsNew
        ? (
            <>
              {have}
              <span className="cover-count-total">/{total}</span>
            </>
          )
        : t`${have} of ${total}`
      : have > 0
        ? plural(have, { one: '# chapter', other: '# chapters' })
        : t`No chapters`
  const savedTip = t`Saved on this device`
  const countTip = nothingWanted
    ? plural(total, {
        one: '# chapter listed, none wanted, nothing will download',
        other: '# chapters listed, none wanted, nothing will download',
      })
    : total === 0
      ? t`No source has listed this series' chapters yet, so there is no total to count against.`
      : undefined
  // Only a download in progress earns the left corner; queued is a list-view and series-page fact.
  const downloading = series.downloadingCount > 0
  const downloadTip = downloading && download ? renderLabel(download.label) : null
  // Read progress wraps the status disc, so a cover carries one mark for both. Read through closes
  // the ring; the disc keeps its status glyph.
  const showsRead = readPct !== null && readPct > 0
  const readThrough = showsRead && unread === 0
  const readTip = unread === 0 ? t`All downloaded chapters read` : t`${readChapterCount} of ${have} downloaded read`
  const notMonitoredTip = t`Not monitored`
  const mutedTip = t`Notifications muted`
  const statusOnly = series.monitored ? statusLabel : t`${statusLabel}, not monitored`
  const statusTip = showsRead ? t`${statusOnly}. ${readTip}` : statusOnly

  const card = (
    <Link
      to={`/series/${series.id}`}
      className="cover-card"
      data-selected={selected || undefined}
      data-offline={offline}
      role={selectMode ? 'checkbox' : undefined}
      aria-checked={selectMode ? selected : undefined}
      onClick={(e) => {
        if (selectMode) {
          e.preventDefault()
          onToggle(series.id)
        }
      }}
      onKeyDown={(e) => {
        if (selectMode && e.key === ' ') {
          e.preventDefault()
          onToggle(series.id)
        }
      }}
    >
      <div className="cover-poster">
        {series.coverUrl ? (
          <img
            src={series.coverUrl}
            srcSet={coverSrcSet(series.coverUrl)}
            sizes={COVER_CARD_SIZES}
            alt=""
            loading="lazy"
            decoding="async"
          />
        ) : (
          <div className="cover-placeholder" aria-hidden>{series.displayTitle}</div>
        )}
        <div className="cover-scrim" />

        {selectMode && <span className="cover-check" data-checked={selected || undefined} aria-hidden />}

        {/* Ahead of the corner marks in the DOM so the link's accessible name starts with the title. */}
        <div className="cover-meta">
          {/* The tooltip stays the canonical title, so the name the folder on disk and search
              use is still reachable when a language preference has moved the label off it. */}
          <span className="cover-title" title={series.title}>
            {series.displayTitle}
          </span>
          {/* With no total the bar would only ever read 0%; it keeps its space so the grid holds still. */}
          <div className="cover-bar" data-empty={total === 0 || undefined}>
            <div
              className="cover-bar-fill"
              data-complete={complete || undefined}
              style={{ width: `${pct}%` }}
            />
          </div>
          <div className="cover-row">
            {offline === 'unavailable' ? (
              <span>{t`Not available offline`}</span>
            ) : (
              <span data-nothing-wanted={nothingWanted || undefined} data-tip={countTip}>
                {countLabel}
              </span>
            )}
            {showsNew && (
              <span className="cover-new">{plural(unread, { one: '# new', other: '# new' })}</span>
            )}
          </div>
        </div>

        <div className="cover-corners">
          <div className="cover-corner cover-corner-left">
            {offline === 'saved' && (
              <span className="cover-state" data-tone="saved" data-tip={savedTip} role="img" aria-label={savedTip}>
                <IconDeviceMobileCheck size={13} />
              </span>
            )}
            {downloadTip && (
              <span className="cover-state" data-tone="info" data-tip={downloadTip} role="img" aria-label={downloadTip}>
                <IconDownload size={13} />
              </span>
            )}
          </div>

          <div className="cover-corner cover-corner-right">
            {/* Only the exception: monitored is the normal case, and a mark for it is noise. */}
            {!series.monitored && (
              <span
                className="cover-badge cover-badge-circle"
                data-tip={notMonitoredTip}
                role="img"
                aria-label={notMonitoredTip}
              >
                <IconEyeOff size={16} />
              </span>
            )}
            {/* Only when muted: the other three modes are the normal case and would be noise. */}
            {series.notificationMode === 'Muted' && (
              <span
                className="cover-badge cover-badge-circle"
                data-dim
                data-tip={mutedTip}
                role="img"
                aria-label={mutedTip}
              >
                <IconBellOff size={16} />
              </span>
            )}
            {/* Last in the row so it holds the corner; the eye and bell only show on hover. */}
            <span
              className="cover-state"
              data-tone="status"
              data-unmonitored={!series.monitored || undefined}
              data-read={readThrough ? 'all' : showsRead ? 'part' : undefined}
              data-tip={statusTip}
              role="img"
              aria-label={statusTip}
              style={
                {
                  '--tone': statusColor(status.color),
                  '--tone-on': statusOnColor(status.color),
                  '--ring-pct': showsRead ? `${readThrough ? 100 : readPct}%` : undefined,
                } as React.CSSProperties
              }
            >
              <StatusGlyph size={13} />
            </span>
          </div>
        </div>
      </div>
    </Link>
  )

  if (!menu) return card
  return (
    <div className="reading-card library-card">
      {card}
      {!selectMode && !offline && <CoverCardMenu series={series} />}
    </div>
  )
})
