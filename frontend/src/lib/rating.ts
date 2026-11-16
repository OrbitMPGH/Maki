import type { SeriesDto } from '../api/types'
import { formatFixedDecimal, formatNumber } from '../format'

type RatingState = Pick<SeriesDto, 'rating' | 'provisionalRating'>

/** The score the reader sees on a card or the series page: the final rating, else the "so far" one. */
export const shownRating = (s: RatingState): number | null => s.rating ?? s.provisionalRating ?? null

/** The shown score is a "so far" one. */
export const isProvisional = (s: RatingState): boolean => s.rating == null && s.provisionalRating != null

/**
 * Whether the stars set a "so far" score rather than the rating: the reader is partway through and
 * has no final rating yet. Finished, or nothing read, means a deliberate verdict.
 */
export const ratesProvisionally = (s: RatingState & Pick<SeriesDto, 'finished' | 'readChapterCount'>): boolean =>
  s.rating == null && !s.finished && (s.readChapterCount ?? 0) > 0

export type RatingPrecision = 'whole' | 'decimal'

export interface RatingCategory {
  key: string
  /** Set on a custom category only; built-ins are translated from their key. */
  name: string | null
  enabled: boolean
  weight: number
}

export const RATING_STEP = 0.1

export const roundRating = (value: number): number => Math.round(Math.min(10, Math.max(1, value)) * 10) / 10

/** The rating as the reader's precision shows it: "7.8" (locale aware) or "8". */
export function formatRating(value: number, precision: RatingPrecision): string {
  return precision === 'decimal' ? formatFixedDecimal(value, 1) : formatNumber(Math.round(value))
}

/** The weighted mean of the scored, enabled categories to one decimal, or null below two scores. */
export function breakdownScore(scores: Record<string, number>, categories: RatingCategory[]): number | null {
  let total = 0
  let weights = 0
  let count = 0
  for (const c of categories) {
    const score = scores[c.key]
    if (!c.enabled || score == null) continue
    total += score * c.weight
    weights += c.weight
    count++
  }
  return count < 2 ? null : Math.round((total / weights) * 10) / 10
}

/** Reads "7,8" and "7.8" alike; null for anything that isn't a rating. */
export function parseRating(text: string): number | null {
  const value = Number(text.trim().replace(',', '.'))
  return Number.isFinite(value) && value >= 1 && value <= 10 ? roundRating(value) : null
}
