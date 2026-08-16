const UNITS: [Intl.RelativeTimeFormatUnit, number][] = [
  ['year', 365 * 24 * 60 * 60],
  ['month', 30 * 24 * 60 * 60],
  ['day', 24 * 60 * 60],
  ['hour', 60 * 60],
  ['minute', 60],
]

const FORMAT = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' })

/**
 * "3 minutes ago", "yesterday", "2 months ago" — for feeds where the exact timestamp is noise.
 * Uses the browser's locale via `Intl`, so nothing here needs translating.
 *
 * Anything under a minute reads as "just now" rather than "in 0 seconds": clock skew between the
 * server that stamped the row and the browser rendering it routinely puts a fresh row a second or
 * two into the future, and "in 2 seconds" is a worse lie than "just now".
 */
export function relativeTime(iso: string): string {
  const then = new Date(iso).getTime()
  if (Number.isNaN(then)) return ''

  const seconds = (then - Date.now()) / 1000
  const magnitude = Math.abs(seconds)
  if (magnitude < 60) return 'just now'

  for (const [unit, size] of UNITS) {
    if (magnitude >= size) {
      return FORMAT.format(Math.round(seconds / size), unit)
    }
  }

  return 'just now'
}
