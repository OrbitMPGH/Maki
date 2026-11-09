import { i18n } from '@lingui/core'
import { t } from '@lingui/core/macro'

const UNITS: [Intl.RelativeTimeFormatUnit, number][] = [
  ['year', 365 * 24 * 60 * 60],
  ['month', 30 * 24 * 60 * 60],
  ['day', 24 * 60 * 60],
  ['hour', 60 * 60],
  ['minute', 60],
]

// Rebuilt when the interface language changes. Passing `undefined` would take the browser's locale,
// which is not the one the user picked: a Swedish interface would otherwise say "2 hours ago".
let formatFor: string | null = null
const formats = new Map<Intl.RelativeTimeFormatStyle, Intl.RelativeTimeFormat>()

function formatter(style: Intl.RelativeTimeFormatStyle): Intl.RelativeTimeFormat {
  const locale = i18n.locale || 'en'
  if (formatFor !== locale) {
    formats.clear()
    formatFor = locale
  }
  let format = formats.get(style)
  if (!format) {
    // The narrow form always counts: with `auto`, English mixed "last mo." and "yesterday" into a
    // row of "2mo ago" stamps.
    format = new Intl.RelativeTimeFormat(locale, { numeric: style === 'narrow' ? 'always' : 'auto', style })
    formats.set(style, format)
  }
  return format
}

/**
 * "3 minutes ago", "yesterday", "2 months ago" - for feeds where the exact timestamp is noise.
 *
 * Anything under a minute reads as "just now" rather than "in 0 seconds": clock skew between the
 * server that stamped the row and the browser rendering it routinely puts a fresh row a second or
 * two into the future, and "in 2 seconds" is a worse lie than "just now".
 *
 * `narrow` ("6h ago", "1d ago") is the app's timestamp: every stamp standing on its own in a meta line,
 * card or list uses it. The long form is only for a stamp inside a sentence ("Last run yesterday: 3
 * added", "Retrying in 5 minutes"), where the clipped form reads as a typo.
 */
export function relativeTime(iso: string, style: Intl.RelativeTimeFormatStyle = 'long'): string {
  const then = new Date(iso).getTime()
  if (Number.isNaN(then)) return ''

  const seconds = (then - Date.now()) / 1000
  const magnitude = Math.abs(seconds)
  if (magnitude < 60) return t`just now`

  for (let i = 0; i < UNITS.length; i++) {
    const [unit, size] = UNITS[i]
    if (magnitude < size) continue
    const value = Math.round(seconds / size)
    // 23h40m rounds to 24 hours and 350 days to 12 months: say "1d" and "1y" instead.
    if (i > 0 && Math.abs(value) >= Math.round(UNITS[i - 1][1] / size)) {
      const [larger, largerSize] = UNITS[i - 1]
      return formatter(style).format(Math.round(seconds / largerSize), larger)
    }
    return formatter(style).format(value, unit)
  }

  return t`just now`
}
