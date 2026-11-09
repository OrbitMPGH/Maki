import type { CSSProperties } from 'react'
import {
  IconAlertTriangle,
  IconBan,
  IconCheck,
  IconCircleCheck,
  IconClock,
  IconClockPause,
  IconDownload,
  IconEye,
  IconEyeCheck,
  IconEyeOff,
  IconFileUnknown,
  IconFileZip,
  IconHourglass,
  IconLink,
  IconLinkOff,
  IconLoader2,
  IconPackage,
  IconPlayerPause,
  IconPlayerPlay,
  IconX,
  type Icon,
} from '../../lib/icons'
import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { obtainedCount } from '../../lib/chapterCounts'

export interface StatusVisual {
  /** A colour token as a CSS value (`var(--ok)`), so a Badge, a `--tone` and a fill all draw the same shade. */
  color: string
  /**
   * A descriptor for the words this app chose, a plain string for a wire value it did not
   * recognise. Render it with `useLabel()` from `i18n-context`, which keeps the untranslated half
   * untranslated: a status Maki has no case for is data, and guessing at it in Polish would be
   * worse than showing what the server actually said.
   */
  label: string | MessageDescriptor
  Icon: Icon
}

/** `vars` for a filled `color="var(--ok)"` Button: autoContrast cannot read a var(), so its label would stay white. */
export const okButtonVars = () => ({ root: { '--button-color': 'var(--ok-on)' } })

const tone = (stem: string) => `var(--${stem})`

/** Token stem for a `StatusVisual.color` (`var(--ok)` gives `ok`), for `var(--x)` / `var(--x-soft)` pairs. */
export function statusToken(color: string): string {
  return /^var\(--([\w-]+)\)$/.exec(color)?.[1] ?? 'neutral'
}

/** The same, as a colour value a Mantine `color` prop takes. */
export function statusColor(color: string): string {
  return `var(--${statusToken(color)})`
}

/** The foreground that sits on a `statusColor` fill; it flips with the theme, a fixed white fails on the dark ones. */
export function statusOnColor(color: string): string {
  return `var(--${statusToken(color)}-on)`
}

/** Fill plus its own foreground; a fill alone leaves the label white, which fails on the dark themes. */
export function badgeFill(color: string): CSSProperties {
  return { background: statusColor(color), color: statusOnColor(color) }
}

/** Publication status of a series (from metadata). */
export function seriesStatusVisual(status: string): StatusVisual {
  switch (status) {
    case 'Ongoing':
      return { color: tone('info'), label: msg`Ongoing`, Icon: IconPlayerPlay }
    case 'Completed':
      return { color: tone('ok'), label: msg`Completed`, Icon: IconCircleCheck }
    case 'Hiatus':
      return { color: tone('warn'), label: msg`Hiatus`, Icon: IconClockPause }
    case 'Cancelled':
      return { color: tone('danger'), label: msg`Cancelled`, Icon: IconBan }
    default:
      return { color: tone('neutral'), label: status || msg`Unknown`, Icon: IconHourglass }
  }
}

/** Glyphs for the small status discs on posters; anything else falls back to `seriesStatusVisual`'s icon. */
const STATUS_GLYPH: Record<string, Icon> = {
  Ongoing: IconPlayerPlay,
  Completed: IconCheck,
  Hiatus: IconPlayerPause,
  Cancelled: IconX,
}

export function seriesStatusGlyph(status: string): Icon {
  return STATUS_GLYPH[status] ?? seriesStatusVisual(status).Icon
}

/** Content rating (from metadata), least to most explicit. Null when unrefreshed. */
export function contentRatingVisual(rating: string | null): StatusVisual | null {
  switch (rating) {
    case 'safe':
      return { color: tone('cr-safe'), label: msg`Safe`, Icon: IconEyeCheck }
    case 'suggestive':
      return { color: tone('cr-suggestive'), label: msg`Suggestive`, Icon: IconEye }
    case 'erotica':
      return { color: tone('cr-erotica'), label: msg`Erotica`, Icon: IconEyeOff }
    case 'pornographic':
      return { color: tone('cr-explicit'), label: msg`Pornographic`, Icon: IconAlertTriangle }
    default:
      return null
  }
}

/** Download-queue item status. */
export function queueStatusVisual(status: string): StatusVisual {
  switch (status) {
    case 'Resolving':
      return { color: tone('info'), label: msg`Finding source`, Icon: IconLoader2 }
    case 'Queued':
      return { color: tone('neutral'), label: msg`Queued`, Icon: IconClock }
    case 'FetchingPages':
      return { color: tone('info'), label: msg`Fetching`, Icon: IconLoader2 }
    case 'Downloading':
      return { color: tone('info'), label: msg`Downloading`, Icon: IconDownload }
    case 'Validating':
      return { color: tone('info'), label: msg`Validating`, Icon: IconCheck }
    case 'Packaging':
      return { color: tone('info'), label: msg`Packaging`, Icon: IconFileZip }
    case 'Importing':
      return { color: tone('info'), label: msg`Importing`, Icon: IconPackage }
    case 'AwaitingImport':
      return { color: tone('warn'), label: msg`Needs review`, Icon: IconAlertTriangle }
    case 'Completed':
      return { color: tone('ok'), label: msg`Completed`, Icon: IconCircleCheck }
    case 'Failed':
      return { color: tone('danger'), label: msg`Failed`, Icon: IconAlertTriangle }
    case 'RateLimited':
      return { color: tone('warn'), label: msg`Rate limited`, Icon: IconClockPause }
    case 'Cancelled':
      return { color: tone('neutral'), label: msg`Cancelled`, Icon: IconBan }
    default:
      return { color: tone('neutral'), label: status, Icon: IconHourglass }
  }
}

/**
 * Library-item download activity, derived from a series' queue counts. Only in-flight work gets a
 * badge: an idle series shows nothing, since the progress bar already says complete vs missing.
 * The count is the series' whole outstanding queue, not just the chapters the two download workers
 * happen to hold right now.
 */
export function seriesDownloadStateVisual(s: {
  downloadingCount: number
  queuedCount: number
}): StatusVisual | null {
  const outstanding = s.downloadingCount + s.queuedCount
  if (outstanding === 0) return null
  return s.downloadingCount > 0
    ? { color: tone('info'), label: msg`Downloading ${outstanding}`, Icon: IconDownload }
    : { color: tone('neutral'), label: msg`Queued ${outstanding}`, Icon: IconClock }
}

export interface SeriesProgressVisual {
  /** Denominator the card renders: wanted chapters, falling back to every known chapter. */
  total: number
  /** Nothing wanted, so `total` is the known-chapter fallback and isn't real progress. */
  nothingWanted: boolean
  /** Chapters actually on disk. */
  have: number
  /** Download bar width, 0–100. */
  pct: number
  complete: boolean
  /** Read ring, 0–100, or null when there is nothing trustworthy to show. */
  readPct: number | null
  /**
   * Downloaded chapters still unread: 0 meaning "all read", null meaning nothing tracks it.
   * The ring alone is easy to miss, so both views spell the same number out in a badge.
   */
  unread: number | null
}

/**
 * Download/read progress for one library item. The grid card and the list row must agree on every
 * one of these (two copies of the arithmetic drift the first time the denominator changes), so
 * this is the single definition both render from.
 *
 * Nothing wanted and nothing downloaded makes the normal total 0, which would render a bare
 * "0/?" next to a Chapters tab listing every known chapter as missing. Fall back to the known
 * count so the card reads "0/207", and mark it so it isn't mistaken for real progress.
 *
 * The denominator only moves when the user changes what they want. Chapters merely waiting to
 * download are still wanted, so a series held back by Smart mode or fetched in batches reads
 * "10 / 207" rather than the "10 / 10" it used to.
 *
 * `readTracking` false blanks the read fields: nothing is tracking reading, so a stale
 * ReadingState row from a Kavita connection that has since been removed can't linger on a card.
 */
export function seriesProgressVisual(
  s: {
    wantedChapterCount: number
    knownChapterCount: number
    chapterFileCount: number
    removedChapterCount?: number
    readChapterCount: number | null
  },
  readTracking: boolean,
): SeriesProgressVisual {
  const wantedTotal = s.wantedChapterCount || 0
  const total = wantedTotal || s.knownChapterCount || 0
  const nothingWanted = wantedTotal === 0 && total > 0
  const have = obtainedCount(s)
  const tracked = readTracking && s.readChapterCount != null && have > 0
  return {
    total,
    nothingWanted,
    have,
    pct: !nothingWanted && total > 0 ? Math.min(100, (have / total) * 100) : 0,
    complete: !nothingWanted && total > 0 && have >= total,
    readPct: tracked ? Math.min(100, (s.readChapterCount! / have) * 100) : null,
    unread: tracked ? Math.max(0, have - s.readChapterCount!) : null,
  }
}

/** A download that has finished but is waiting for someone to say how it should be imported. */
export function needsImportReview(status: string): boolean {
  return status === 'AwaitingImport'
}

/**
 * Whether a queue item is still actively working. A download parked for import review is not: it
 * has nothing left to do until a person answers, and counting it as busy leaves every progress
 * indicator in the app spinning on something that will never move on its own.
 */
export function isQueueActive(status: string): boolean {
  return (
    status !== 'Completed' &&
    status !== 'Failed' &&
    status !== 'Cancelled' &&
    !needsImportReview(status)
  )
}

/** A tracker's reading status for one series, as returned by scrobble sync. */
export function trackerStatusVisual(status: string): StatusVisual {
  switch (status) {
    case 'completed':
      return { color: tone('ok'), label: msg`Completed`, Icon: IconCircleCheck }
    case 'reading':
      return { color: tone('info'), label: msg`Reading`, Icon: IconPlayerPlay }
    case 'plan_to_read':
      return { color: tone('neutral'), label: msg`Plan to read`, Icon: IconClock }
    // A status the user set on the tracker that a sync never overrides. The wire value does not
    // tell paused from dropped, so it takes paused's amber rather than claim a drop.
    case 'other':
      return { color: tone('warn'), label: msg`Paused or dropped`, Icon: IconHourglass }
    default:
      return { color: tone('neutral'), label: status || msg`Listed`, Icon: IconHourglass }
  }
}

/** A tracker connection's dot/state: connected, set up but not connected, or not set up. */
export function trackerConnectionVisual(connected: boolean, configured: boolean): StatusVisual {
  if (connected) return { color: tone('ok'), label: msg`Connected`, Icon: IconLink }
  if (configured) return { color: tone('danger'), label: msg`Not connected`, Icon: IconLinkOff }
  return { color: tone('neutral'), label: msg`Not set up`, Icon: IconLinkOff }
}

/** A library file's link state against the series' chapters. */
export function fileStatusVisual(status: string): StatusVisual {
  switch (status) {
    case 'linked':
      return { color: tone('ok'), label: msg`Linked`, Icon: IconLink }
    case 'unlinked':
      return { color: tone('warn'), label: msg`Not linked`, Icon: IconLinkOff }
    case 'missing':
      return { color: tone('warn'), label: msg`File missing`, Icon: IconFileUnknown }
    default:
      return { color: tone('warn'), label: msg`Unrecognized`, Icon: IconFileUnknown }
  }
}
