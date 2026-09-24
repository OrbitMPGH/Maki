import { i18n } from '@lingui/core'
import { t } from '@lingui/core/macro'

/**
 * Number, date and size formatting, all bound to the active interface language.
 *
 * Everything here used to be either a `toLocale*` call taking the browser's locale implicitly, or a
 * hand-rolled English helper. Neither survives a language setting: the browser's locale is not the
 * one the user picked, and an English month name is an English month name whatever the rest of the
 * page says.
 *
 * `Intl` formatters are expensive enough to be worth caching and cheap enough not to be worth
 * caching cleverly, so each is memoised against the locale it was built for and rebuilt when that
 * changes.
 */

function locale(): string {
  return i18n.locale || 'en'
}

function cached<T>(build: (locale: string) => T): () => T {
  let forLocale: string | null = null
  let value: T
  return () => {
    const current = locale()
    if (forLocale !== current) {
      value = build(current)
      forLocale = current
    }
    return value
  }
}

const decimal = cached((l) => new Intl.NumberFormat(l, { maximumFractionDigits: 1 }))
const integer = cached((l) => new Intl.NumberFormat(l))
const shortMonth = cached((l) => new Intl.DateTimeFormat(l, { month: 'short' }))
const longMonth = cached((l) => new Intl.DateTimeFormat(l, { month: 'long' }))
const dateOnly = cached((l) => new Intl.DateTimeFormat(l, { dateStyle: 'medium' }))
const dateAndTime = cached((l) => new Intl.DateTimeFormat(l, { dateStyle: 'medium', timeStyle: 'short' }))
const timeOnly = cached((l) => new Intl.DateTimeFormat(l, { timeStyle: 'short' }))
const shortWeekday = cached((l) => new Intl.DateTimeFormat(l, { weekday: 'short' }))
const longWeekday = cached((l) => new Intl.DateTimeFormat(l, { weekday: 'long' }))
const hourOnly = cached((l) => new Intl.DateTimeFormat(l, { hour: 'numeric', minute: '2-digit' }))
const percents = cached(() => new Map<number, Intl.NumberFormat>())
const signedDecimals = cached(() => new Map<number, Intl.NumberFormat>())

/** "1 234" / "1,234", whichever the language groups with. */
export function formatNumber(value: number): string {
  return integer().format(value)
}

/**
 * "1.5 GB", "512 MB", "0 B". Binary units with decimal names, which is what every desktop OS shows
 * and therefore what a size here is compared against; the three copies this replaces were split
 * between KB and KiB for the same 1024 divisor.
 *
 * Whole numbers past 100 because "512.0 MB" is three characters of noise, and the decimal separator
 * comes from the language, so Swedish reads "1,5 GB".
 */
export function formatBytes(bytes: number | null | undefined): string {
  if (bytes === null || bytes === undefined) return '-'
  if (bytes <= 0) return '0 B'

  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  // Clamped at both ends. A value under 1 — a transfer rate of 0.4 B/s on a stalled download, say —
  // gives a negative exponent, and units[-1] is undefined rather than out of range, so it renders
  // as "410 undefined" instead of failing.
  const unit = Math.min(units.length - 1, Math.max(0, Math.floor(Math.log(bytes) / Math.log(1024))))
  const value = bytes / 1024 ** unit
  const rendered = value >= 100 || unit === 0 ? integer().format(Math.round(value)) : decimal().format(value)
  return `${rendered} ${units[unit]}`
}

/** "15 Sep 2026". */
export function formatDate(value: string | number | Date): string {
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '' : dateOnly().format(date)
}

/** "15 Sep 2026, 16:45". */
export function formatDateTime(value: string | number | Date): string {
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '' : dateAndTime().format(date)
}

/** "16:45", or "4:45 PM" where that is the convention. */
export function formatTime(value: string | number | Date): string {
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '' : timeOnly().format(date)
}

/**
 * Month name for a 1-based month number. Replaces two hand-maintained English `MONTHS` arrays that
 * had drifted into separate copies of the same twelve strings.
 */
export function monthName(month: number, style: 'short' | 'long' = 'long'): string {
  if (!Number.isInteger(month) || month < 1 || month > 12) return ''
  // Local time, not UTC. The formatters below have no time zone, so they render in the viewer's,
  // and a UTC midnight on the 1st is still the previous month anywhere west of Greenwich: in Los
  // Angeles this returned December for January, and so on down the year.
  // Any non-leap year works; only the month is read back out.
  const date = new Date(2001, month - 1, 1)
  return (style === 'short' ? shortMonth() : longMonth()).format(date)
}

/** Weekday name with Monday as 0, the order the stats endpoints send weekdays in. */
export function weekdayName(index: number, style: 'short' | 'long' = 'long'): string {
  if (!Number.isInteger(index) || index < 0 || index > 6) return ''
  // 1 January 2001 was a Monday.
  const date = new Date(2001, 0, 1 + index)
  return (style === 'short' ? shortWeekday() : longWeekday()).format(date)
}

/** An hour of the day, 0-23, as a clock time: "22:00", or "10:00 PM" where that is the convention. */
export function formatHour(hour: number): string {
  const h = ((Math.trunc(hour) % 24) + 24) % 24
  return hourOnly().format(new Date(2001, 0, 1, h, 0))
}

/** A 0..1 fraction as a percentage in the active language: "62%", "62 %", "%62". */
export function formatPercent(fraction: number, digits = 0): string {
  const formats = percents()
  let format = formats.get(digits)
  if (!format) {
    format = new Intl.NumberFormat(locale(), {
      style: 'percent',
      minimumFractionDigits: digits,
      maximumFractionDigits: digits,
    })
    formats.set(digits, format)
  }
  return format.format(fraction)
}

/** A signed one-decimal number: "+4.2", "-1.0", "0.0". The sign is whatever `Intl` produces. */
export function formatSignedDecimal(value: number, digits = 1): string {
  const formats = signedDecimals()
  let format = formats.get(digits)
  if (!format) {
    format = new Intl.NumberFormat(locale(), {
      signDisplay: 'exceptZero',
      minimumFractionDigits: digits,
      maximumFractionDigits: digits,
    })
    formats.set(digits, format)
  }
  return format.format(value)
}

/** "2026-03" as the stats buckets carry it, rendered "Mar 26". */
export function formatMonthBucket(bucket: string): string {
  const [year, month] = bucket.split('-')
  const name = monthName(Number(month), 'short')
  return name ? `${name} ${year.slice(2)}` : bucket
}

/**
 * Reading time as a person would say it: "4h 20m", "35m", "48s". Minutes are dropped once the
 * figure is whole hours, because "12h 0m" reads like a stopwatch rather than an answer.
 *
 * The unit letters are translated rather than hardcoded: "h" and "m" happen to travel, but they are
 * not universal and a language that writes "t" for hours should be able to.
 */
export function formatReadingTime(seconds: number): string {
  const s = (n: number) => t`${n}s`
  const m = (n: number) => t`${n}m`
  const h = (n: number) => t`${n}h`

  if (seconds < 60) return s(Math.max(0, Math.round(seconds)))

  const hours = Math.floor(seconds / 3600)
  const minutes = Math.round((seconds % 3600) / 60)
  if (hours === 0) return m(minutes)
  // 59m30s rounds to 60 minutes; carry it rather than printing "3h 60m".
  if (minutes === 60) return h(hours + 1)
  return minutes === 0 ? h(hours) : `${h(hours)} ${m(minutes)}`
}
