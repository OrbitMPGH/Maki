import type { RecommendationFilters, TasteProfile } from '../../api/hooks'

/**
 * How over-indexed a genre has to be before it is worth narrowing on. Below this it is a genre the
 * reader owns rather than one they reach for, and filtering on it says nothing.
 */
const OVER_INDEX_THRESHOLD = 1.3

/** Years before the earliest read year to keep, so the band frames the taste rather than clipping it. */
const YEAR_PAD_BACK = 3

/** Distinct years the band needs before it is a preference rather than one series' release date. */
const MIN_YEARS = 2

/**
 * The profile as something the recommender can actually run.
 *
 * Deliberately narrow. Genres and tags are both ANDed server-side, so handing over a top ten of
 * either returns nothing at all: one genre plus a year band is about as far as this can go and
 * still come back with picks. Tags stay out entirely and get their own single-tag action instead.
 *
 * Creators are absent because they cannot be expressed: `RecommendationFilters` has no creator
 * field. Content ratings and rating floors are left alone too, since those are the reader's own
 * ceilings rather than anything their taste implies.
 */
export function buildFiltersFromProfile(profile: TasteProfile): RecommendationFilters {
  const filters: RecommendationFilters = {}

  // The genre the reader reaches for, or, on a library too small for any ratio to be meaningful,
  // simply their biggest one. Falling back matters: on a handful of series every ratio comes back
  // null, and a profile page whose only action is permanently greyed out is worse than a rough
  // guess the user can see and change.
  const topGenre =
    profile.genres.find(
      (g) => g.overIndexShelf !== null && g.overIndexShelf >= OVER_INDEX_THRESHOLD,
    ) ?? profile.genres[0]
  if (topGenre) {
    filters.genres = [topGenre.name]
  }

  const years = profile.years.map((y) => y.year)
  if (years.length >= MIN_YEARS) {
    filters.yearMin = Math.min(...years) - YEAR_PAD_BACK
    filters.yearMax = Math.max(...years)
  }

  return filters
}

/** Whether applying the profile would narrow anything at all. */
export function hasAnyFilter(filters: RecommendationFilters): boolean {
  return Object.values(filters).some((v) => (Array.isArray(v) ? v.length > 0 : v != null))
}
