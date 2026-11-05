import type { ActivityTotals } from '../../api/hooks'

/**
 * How much reading a Stats sentence or percentage needs behind it before it is worth saying.
 *
 * Below these, one stray session reads as a habit ("A typical sitting runs 17s") and a change off a
 * small base reads as a trend ("5m, -42%" against nine minutes). The figures themselves always show;
 * only the claims made about them wait.
 */

/** Sittings, and timed reading, before the Rhythm sentence describes when and how long you read. */
export const MIN_SITTINGS = 3
export const MIN_TIMED_SECONDS = 15 * 60

/** Series started before the finish rate becomes a sentence: one series makes it 0% or 100%. */
export const MIN_SERIES_STARTED = 3

/**
 * The smallest previous-period value a percentage change is computed from. Roughly five chapters'
 * worth each (about 20 pages and 6 minutes a chapter), where one chapter more or less moves the
 * result by 20% rather than 50%; three for the counts that are naturally small.
 */
const MIN_DELTA_BASE: Partial<Record<keyof ActivityTotals, number>> = {
  chaptersRead: 5,
  pagesRead: 100,
  readingSeconds: 30 * 60,
  daysActive: 3,
  seriesStarted: 3,
  seriesFinished: 3,
}

/** Whether a period-over-period change against `previous` means anything. */
export function deltaHasBase(metric: keyof ActivityTotals, previous: number): boolean {
  return previous >= (MIN_DELTA_BASE[metric] ?? 1)
}
