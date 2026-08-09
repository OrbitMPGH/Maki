/**
 * The window the Overview tab reports on.
 *
 * Kept apart from the components so the comparison arithmetic has one home: every headline tile
 * shows change against the previous window of the same length, and getting that off by a day makes
 * every number on the page subtly wrong.
 */

export type RangePreset = '30d' | '90d' | '12m' | 'year' | 'all'

export interface DateRange {
  from: string
  to: string
}

export const RANGE_OPTIONS: { value: RangePreset; label: string }[] = [
  { value: '30d', label: '30 days' },
  { value: '90d', label: '90 days' },
  { value: '12m', label: '12 months' },
  { value: 'year', label: 'Year' },
  { value: 'all', label: 'All time' },
]

export const MONTHS = [
  'January',
  'February',
  'March',
  'April',
  'May',
  'June',
  'July',
  'August',
  'September',
  'October',
  'November',
  'December',
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

/**
 * The equally long window immediately before this one, for period-over-period deltas.
 *
 * Null for All time — there is nothing before everything — and null for a range ending in the
 * future (a part-way-through calendar year compared against a whole one reads as a collapse).
 */
export function previousRange(preset: RangePreset, range: DateRange): DateRange | null {
  if (preset === 'all') {
    return null
  }

  const from = new Date(`${range.from}T00:00:00`)
  const to = new Date(`${range.to}T00:00:00`)
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

/** Human label for the window, used by the Rewind slides and the delta tooltips. */
export function rangeLabel(preset: RangePreset, year: number, month: number | null): string {
  switch (preset) {
    case '30d':
      return 'the last 30 days'
    case '90d':
      return 'the last 90 days'
    case '12m':
      return 'the last 12 months'
    case 'year':
      return month === null ? String(year) : `${MONTHS[month - 1]} ${year}`
    case 'all':
      return 'all time'
  }
}
