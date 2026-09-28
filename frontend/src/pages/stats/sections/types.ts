import type { DateRange, RangePreset } from '../StatsRange'

/** What the page hands every section. Library ignores `userId`: the library is shared. */
export interface StatsSectionProps {
  /** undefined means the signed-in reader. */
  userId?: number
  range: DateRange
  /** The window to compare against. Null for All time, which has nothing before it. */
  previous: DateRange | null
  preset: RangePreset
  /** The window as it reads mid-sentence: "the last 30 days", "this month", "March 2026". */
  windowLabel: string
}
