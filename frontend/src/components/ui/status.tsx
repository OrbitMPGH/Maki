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
  IconFileZip,
  IconHourglass,
  IconLoader2,
  IconPackage,
  IconPlayerPlay,
  type Icon,
} from '@tabler/icons-react'

/**
 * Every status colour in the app comes from here, and only from here. `color` is one of six
 * Mantine palette names, each overridden in theme.ts to a semantic token:
 *
 *   teal   --ok       on disk, linked, read, synced, completed
 *   yellow --warn     needs a decision from the user
 *   blue   --info     in flight, in progress
 *   red    --danger   failed, missing from disk, destructive
 *   violet --watched  watched, not read
 *   gray   --neutral  known but inert
 *
 * Nothing else is a status colour. A page file writing `color="grape"` on a Badge is a bug, and
 * the fix is a visual here rather than a hue at the call site. See .claude/rules/design-system.md.
 */
export interface StatusVisual {
  /** One of: teal | yellow | blue | red | violet | gray. */
  color: string
  label: string
  Icon: Icon
}

/**
 * Mantine palette keys the library badges use, resolved to CSS vars once instead of per instance.
 * One entry per slot in StatusVisual; anything outside those six has no entry on purpose.
 */
export const BADGE_COLOR: Record<string, string> = {
  teal: 'var(--mantine-color-teal-filled)',
  yellow: 'var(--mantine-color-yellow-filled)',
  blue: 'var(--mantine-color-blue-filled)',
  red: 'var(--mantine-color-red-filled)',
  violet: 'var(--mantine-color-violet-filled)',
  gray: 'var(--mantine-color-gray-filled)',
}

/** Publication status of a series (from metadata). */
export function seriesStatusVisual(status: string): StatusVisual {
  switch (status) {
    case 'Ongoing':
      return { color: 'blue', label: 'Ongoing', Icon: IconPlayerPlay }
    case 'Completed':
      return { color: 'teal', label: 'Completed', Icon: IconCircleCheck }
    case 'Hiatus':
      return { color: 'yellow', label: 'Hiatus', Icon: IconClockPause }
    case 'Cancelled':
      return { color: 'red', label: 'Cancelled', Icon: IconBan }
    default:
      return { color: 'gray', label: status || 'Unknown', Icon: IconHourglass }
  }
}

/** Content rating (from metadata), least to most explicit. Null when unrefreshed. */
export function contentRatingVisual(rating: string | null): StatusVisual | null {
  switch (rating) {
    case 'safe':
      return { color: 'teal', label: 'Safe', Icon: IconEyeCheck }
    case 'suggestive':
      return { color: 'yellow', label: 'Suggestive', Icon: IconEye }
    case 'erotica':
      return { color: 'yellow', label: 'Erotica', Icon: IconEyeOff }
    case 'pornographic':
      return { color: 'red', label: 'Pornographic', Icon: IconAlertTriangle }
    default:
      return null
  }
}

/** Download-queue item status. */
export function queueStatusVisual(status: string): StatusVisual {
  switch (status) {
    case 'Resolving':
      return { color: 'gray', label: 'Finding source', Icon: IconLoader2 }
    case 'Queued':
      return { color: 'gray', label: 'Queued', Icon: IconClock }
    case 'FetchingPages':
      return { color: 'blue', label: 'Fetching', Icon: IconLoader2 }
    case 'Downloading':
      return { color: 'blue', label: 'Downloading', Icon: IconDownload }
    case 'Validating':
      return { color: 'blue', label: 'Validating', Icon: IconCheck }
    case 'Packaging':
      return { color: 'blue', label: 'Packaging', Icon: IconFileZip }
    case 'Importing':
      return { color: 'teal', label: 'Importing', Icon: IconPackage }
    case 'Completed':
      return { color: 'teal', label: 'Completed', Icon: IconCircleCheck }
    case 'Failed':
      return { color: 'red', label: 'Failed', Icon: IconAlertTriangle }
    case 'RateLimited':
      return { color: 'yellow', label: 'Rate limited', Icon: IconClockPause }
    case 'Cancelled':
      return { color: 'gray', label: 'Cancelled', Icon: IconBan }
    default:
      return { color: 'gray', label: status, Icon: IconHourglass }
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
    ? { color: 'blue', label: `Downloading ${outstanding}`, Icon: IconDownload }
    : { color: 'gray', label: `Queued ${outstanding}`, Icon: IconClock }
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
    readChapterCount: number | null
  },
  readTracking: boolean,
): SeriesProgressVisual {
  const wantedTotal = s.wantedChapterCount || 0
  const total = wantedTotal || s.knownChapterCount || 0
  const nothingWanted = wantedTotal === 0 && total > 0
  const have = s.chapterFileCount
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

/** Whether a queue item is still actively working. */
export function isQueueActive(status: string): boolean {
  return (
    status !== 'Completed' &&
    status !== 'Failed' &&
    status !== 'Cancelled'
  )
}
