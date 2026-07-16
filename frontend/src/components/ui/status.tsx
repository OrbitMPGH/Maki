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
 * Library-item download lifecycle, derived from a series' chapter/queue counts. This is the
 * "where does this series stand" status shown on library cards — richer than the old binary of
 * downloaded vs missing: a series with work in flight reads as Downloading or Queued.
 */
export function seriesDownloadStateVisual(s: {
  chapterCount: number
  chapterFileCount: number
  downloadingCount: number
  queuedCount: number
}): StatusVisual | null {
  if (s.downloadingCount > 0) {
    return { color: 'blue', label: `Downloading ${s.downloadingCount}`, Icon: IconDownload }
  }
  if (s.queuedCount > 0) {
    return { color: 'grape', label: `Queued ${s.queuedCount}`, Icon: IconClock }
  }
  if (s.chapterCount > 0 && s.chapterFileCount >= s.chapterCount) {
    return { color: 'teal', label: 'Complete', Icon: IconCircleCheck }
  }
  if (s.chapterCount > s.chapterFileCount) {
    return { color: 'orange', label: 'Missing', Icon: IconHourglass }
  }
  return null
}

/** Whether a queue item is still actively working. */
export function isQueueActive(status: string): boolean {
  return (
    status !== 'Completed' &&
    status !== 'Failed' &&
    status !== 'Cancelled'
  )
}
