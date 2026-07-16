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

/** Whether a queue item is still actively working. */
export function isQueueActive(status: string): boolean {
  return (
    status !== 'Completed' &&
    status !== 'Failed' &&
    status !== 'Cancelled'
  )
}
