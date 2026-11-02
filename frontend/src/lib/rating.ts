import type { SeriesDto } from '../api/types'

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
