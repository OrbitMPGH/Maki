import { Tooltip } from '@mantine/core'
import { formatDateTime, formatRecentDate } from '../../format'
import { relativeTime } from './time'

const DAY = 24 * 60 * 60 * 1000

function shortWhen(iso: string): string {
  const age = Math.abs(Date.now() - new Date(iso).getTime())
  if (age >= 7 * DAY) return formatRecentDate(iso)
  if (age >= DAY && age < 2 * DAY) return relativeTime(iso)
  return relativeTime(iso, 'narrow')
}

/** A row's timestamp: "3m ago", "yesterday", then a short date, with the full date and time on hover. */
export function TimeAgo({ value, className }: { value: string | null | undefined; className?: string }) {
  if (!value || Number.isNaN(new Date(value).getTime())) return null
  return (
    <Tooltip label={formatDateTime(value)} withArrow openDelay={300}>
      <time dateTime={value} className={className}>
        {shortWhen(value)}
      </time>
    </Tooltip>
  )
}
