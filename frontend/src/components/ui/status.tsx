import {
  IconAlertTriangle,
  IconBan,
  IconCheck,
  IconCircleCheck,
  IconClock,
  IconClockPause,
  IconDownload,
  IconFileZip,
  IconHourglass,
  IconLoader2,
  IconPackage,
  IconPlayerPlay,
  type Icon,
} from '@tabler/icons-react'

export interface StatusVisual {
  color: string
  label: string
  Icon: Icon
}

/** Mantine palette keys the library badges use, resolved to CSS vars once instead of per instance. */
export const BADGE_COLOR: Record<string, string> = {
  blue: 'var(--mantine-color-blue-filled)',
  teal: 'var(--mantine-color-teal-filled)',
  yellow: 'var(--mantine-color-yellow-filled)',
  red: 'var(--mantine-color-red-filled)',
  gray: 'var(--mantine-color-gray-filled)',
  grape: 'var(--mantine-color-grape-filled)',
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

/** Download-queue item status. */
export function queueStatusVisual(status: string): StatusVisual {
  switch (status) {
    case 'Queued':
      return { color: 'gray', label: 'Queued', Icon: IconClock }
    case 'FetchingPages':
      return { color: 'blue', label: 'Fetching', Icon: IconLoader2 }
    case 'Downloading':
      return { color: 'blue', label: 'Downloading', Icon: IconDownload }
    case 'Validating':
      return { color: 'cyan', label: 'Validating', Icon: IconCheck }
    case 'Packaging':
      return { color: 'cyan', label: 'Packaging', Icon: IconFileZip }
    case 'Importing':
      return { color: 'teal', label: 'Importing', Icon: IconPackage }
    case 'Completed':
      return { color: 'teal', label: 'Completed', Icon: IconCircleCheck }
    case 'Failed':
      return { color: 'red', label: 'Failed', Icon: IconAlertTriangle }
    case 'RateLimited':
      return { color: 'orange', label: 'Rate limited', Icon: IconClockPause }
    case 'Cancelled':
      return { color: 'gray', label: 'Cancelled', Icon: IconBan }
    default:
      return { color: 'gray', label: status, Icon: IconHourglass }
  }
}

/**
 * Library-item download activity, derived from a series' queue counts. Only in-flight work gets a
 * badge — an idle series shows nothing, since the progress bar already says complete vs missing.
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
    : { color: 'grape', label: `Queued ${outstanding}`, Icon: IconClock }
}

export interface SeriesProgressVisual {
  /** Denominator the card renders: monitored chapters, falling back to every known chapter. */
  total: number
  /** Nothing monitored, so `total` is the known-chapter fallback and isn't real progress. */
  unmonitored: boolean
  /** Chapters actually on disk. */
  have: number
  /** Download bar width, 0–100. */
  pct: number
  complete: boolean
  /** Read ring, 0–100, or null when there is nothing trustworthy to show. */
  readPct: number | null
  /**
   * Downloaded chapters still unread — 0 meaning "all read", null meaning nothing tracks it.
   * The ring alone is easy to miss, so both views spell the same number out in a badge.
   */
  unread: number | null
}

/**
 * Download/read progress for one library item. The grid card and the list row must agree on every
 * one of these — two copies of the arithmetic drift the first time the denominator changes — so
 * this is the single definition both render from.
 *
 * Nothing monitored and nothing downloaded makes the normal total 0, which would render a bare
 * "0/?" next to a Chapters tab listing every known chapter as missing. Fall back to the known
 * count so the card reads "0/207", and mark it so it isn't mistaken for real progress.
 *
 * `readTracking` false blanks the read fields — nothing is tracking reading, so a stale
 * ReadingState row from a Kavita connection that has since been removed can't linger on a card.
 */
export function seriesProgressVisual(
  s: {
    chapterCount: number
    knownChapterCount: number
    chapterFileCount: number
    readChapterCount: number | null
  },
  readTracking: boolean,
): SeriesProgressVisual {
  const monitoredTotal = s.chapterCount || 0
  const total = monitoredTotal || s.knownChapterCount || 0
  const unmonitored = monitoredTotal === 0 && total > 0
  const have = s.chapterFileCount
  const tracked = readTracking && s.readChapterCount != null && have > 0
  return {
    total,
    unmonitored,
    have,
    pct: !unmonitored && total > 0 ? Math.min(100, (have / total) * 100) : 0,
    complete: !unmonitored && total > 0 && have >= total,
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
