/**
 * The window the Stats page reports on.
 *
 * Kept apart from the components so the comparison arithmetic has one home: every headline tile
 * shows change against a comparable earlier window, and getting that off by a day makes every number
 * on the page subtly wrong.
 */

import { msg, t as now } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { monthName } from '../../format'

export type RangePreset = '30d' | 'month' | '90d' | '12m' | 'year' | 'all'

export interface DateRange {
  from: string
  to: string
}

/**
 * Descriptors rather than strings, because this module evaluates once: a rendered label would be
 * stuck in whichever language was active then. The caller renders them.
 */
/** `short` is the phone label: the six full labels need about 470px, a phone row has 360. */
export const RANGE_OPTIONS: { value: RangePreset; label: MessageDescriptor; short: MessageDescriptor }[] = [
  { value: '30d', label: msg`30 days`, short: msg({ message: '30d', comment: 'Abbreviated "30 days", a Stats range button on a phone' }) },
  { value: 'month', label: msg`This month`, short: msg`Month` },
  { value: '90d', label: msg`90 days`, short: msg({ message: '90d', comment: 'Abbreviated "90 days", a Stats range button on a phone' }) },
  { value: '12m', label: msg`12 months`, short: msg({ message: '12m', comment: 'Abbreviated "12 months", a Stats range button on a phone' }) },
  { value: 'year', label: msg`Year`, short: msg`Year` },
  { value: 'all', label: msg`All time`, short: msg`All` },
]

/** Local calendar date as yyyy-MM-dd. Not toISOString(), which converts to UTC first. */
export function isoDate(d: Date): string {
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}

function daysAgo(days: number): Date {
  const d = new Date()
  d.setDate(d.getDate() - days)
  return d
}

/** The inclusive local-date range for a whole year, or one month of it. */
export function calendarRange(year: number, month: number | null): DateRange {
  if (month === null) {
    return { from: `${year}-01-01`, to: `${year}-12-31` }
  }
  const lastDay = new Date(year, month, 0).getDate()
  const mm = String(month).padStart(2, '0')
  return { from: `${year}-${mm}-01`, to: `${year}-${mm}-${String(lastDay).padStart(2, '0')}` }
}

/**
 * @param earliestYear first year with any recorded activity, for the All time lower bound. The
 * server has no "everything" mode, so All time is just a very wide explicit range.
 */
export function resolveRange(
  preset: RangePreset,
  year: number,
  month: number | null,
  earliestYear: number,
): DateRange {
  const today = isoDate(new Date())
  switch (preset) {
    case '30d':
      return { from: isoDate(daysAgo(29)), to: today }
    case 'month': {
      const d = new Date()
      return { from: isoDate(new Date(d.getFullYear(), d.getMonth(), 1)), to: today }
    }
    case '90d':
      return { from: isoDate(daysAgo(89)), to: today }
    case '12m': {
      const d = new Date()
      d.setFullYear(d.getFullYear() - 1)
      d.setDate(d.getDate() + 1)
      return { from: isoDate(d), to: today }
    }
    case 'year':
      return calendarRange(year, month)
    case 'all':
      return { from: `${earliestYear}-01-01`, to: today }
  }
}

/** The same calendar date `years` and `months` earlier, clamped to the end of a shorter month. */
function sameDayEarlier(d: Date, years: number, months: number): Date {
  const first = new Date(d.getFullYear() - years, d.getMonth() - months, 1)
  const lastDay = new Date(first.getFullYear(), first.getMonth() + 1, 0).getDate()
  return new Date(first.getFullYear(), first.getMonth(), Math.min(d.getDate(), lastDay))
}

/**
 * The window to compare this one against, for period-over-period deltas.
 *
 * Null for All time, since there is nothing before everything. Calendar windows (This month, and a
 * year or month picked under Year) compare against the same stretch one month or one year earlier,
 * cut off at today when the window is still running: the 1st to the 24th against the 1st to the
 * 24th, January to today against the same days last year. A short month clamps. Rolling windows
 * compare against the equally long window just before them.
 */
export function previousRange(preset: RangePreset, range: DateRange): DateRange | null {
  if (preset === 'all') {
    return null
  }

  const from = new Date(`${range.from}T00:00:00`)
  const to = new Date(`${range.to}T00:00:00`)

  if (preset === 'month' || preset === 'year') {
    const today = new Date(`${isoDate(new Date())}T00:00:00`)
    const end = to > today ? today : to
    if (end < from) {
      return null
    }
    const wholeYear = range.from.endsWith('-01-01') && range.to.endsWith('-12-31')
    const [years, months] = wholeYear ? [1, 0] : [0, 1]
    return {
      from: isoDate(sameDayEarlier(from, years, months)),
      to: isoDate(sameDayEarlier(end, years, months)),
    }
  }

  const days = Math.round((to.getTime() - from.getTime()) / 86_400_000) + 1

  const prevTo = new Date(from)
  prevTo.setDate(prevTo.getDate() - 1)
  const prevFrom = new Date(prevTo)
  prevFrom.setDate(prevFrom.getDate() - (days - 1))

  return { from: isoDate(prevFrom), to: isoDate(prevTo) }
}

/**
 * Fractional change from `previous` to `current`. Null when the baseline was zero: a percentage
 * change from nothing is not a percentage, and rendering it as +100% or ∞ is a lie either way.
 */
export function delta(current: number, previous: number): number | null {
  if (previous === 0) {
    return null
  }
  return (current - previous) / previous
}

/**
 * Human label for the window, used by the Rewind slides and the delta tooltips. Reads mid-sentence
 * ("busiest month in the last 30 days"), which is why these are lowercase and why each is its own
 * message rather than a preset name reused from the picker above.
 *
 * A plain function using the core macro, so it reads the catalogue when it runs. Callers have to be
 * subscribed to the locale themselves for a language switch to reach it.
 */
export function rangeLabel(preset: RangePreset, year: number, month: number | null): string {
  switch (preset) {
    case '30d':
      return now`the last 30 days`
    case 'month':
      return now`this month`
    case '90d':
      return now`the last 90 days`
    case '12m':
      return now`the last 12 months`
    case 'year': {
      if (month === null) return String(year)
      const name = monthName(month)
      return now`${name} ${year}`
    }
    case 'all':
      return now`all time`
  }
}
