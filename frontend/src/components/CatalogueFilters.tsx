import { useCallback, useMemo, useState } from 'react'
import { Button, Group, MultiSelect, RangeSlider, SimpleGrid, Slider, Text } from '@mantine/core'
import { IconDeviceFloppy } from '@tabler/icons-react'
import { useRecommendationTags, type RecommendationFilters } from '../api/hooks'

export const YEAR_MIN = 1950
export const YEAR_MAX = 2026
export const CHAPTER_MIN = 0
export const CHAPTER_MAX = 500 // upper handle here means "500+" (no maximum)

export const TYPE_OPTIONS = ['manga', 'manhwa', 'manhua', 'oel', 'other']
export const STATUS_OPTIONS = ['completed', 'releasing', 'hiatus', 'cancelled']

// Curated from the MangaBaka genre vocabulary (matching is case-insensitive, so casing variants
// like "Sci-Fi"/"Sci-fi" collapse to one option).
export const GENRE_OPTIONS = [
  'Action', 'Adventure', 'Comedy', 'Drama', 'Ecchi', 'Fantasy', 'Harem', 'Historical', 'Horror',
  'Isekai', 'Josei', 'Martial Arts', 'Mecha', 'Mystery', 'Psychological', 'Romance', 'School Life',
  'Sci-Fi', 'Seinen', 'Shoujo', 'Shounen', 'Slice of Life', 'Sports', 'Supernatural', 'Thriller',
  'Tragedy', 'Boys Love', 'Girls Love',
]

/**
 * A stored spec's catalogue-filter fields, as both saved-default shapes carry them: every field
 * optional and explicitly nullable, because the server round-trips an unset constraint as `null`
 * rather than by omitting the property.
 */
export interface CatalogueFilterSpec {
  yearMin?: number | null
  yearMax?: number | null
  types?: string[] | null
  statuses?: string[] | null
  genres?: string[] | null
  tags?: string[] | null
  minChapters?: number | null
  maxChapters?: number | null
  /** The dump's 0–100 scale, not the slider's 0–10. */
  minRating?: number | null
}

/**
 * A stored spec as the wire filters. The nulls have to be dropped rather than passed through:
 * `{ yearMin: null }` is a filter object with a key in it, so anything counting active constraints
 * (the "Filters (n)" badge, the "no matches" copy) would report one that does not exist.
 */
export function filtersFromSpec(spec: CatalogueFilterSpec): RecommendationFilters {
  const f: RecommendationFilters = {}
  if (spec.yearMin != null) f.yearMin = spec.yearMin
  if (spec.yearMax != null) f.yearMax = spec.yearMax
  if (spec.types?.length) f.types = spec.types
  if (spec.statuses?.length) f.statuses = spec.statuses
  if (spec.genres?.length) f.genres = spec.genres
  if (spec.tags?.length) f.tags = spec.tags
  if (spec.minChapters != null) f.minChapters = spec.minChapters
  if (spec.maxChapters != null) f.maxChapters = spec.maxChapters
  if (spec.minRating != null) f.minRating = spec.minRating
  return f
}

/**
 * The catalogue constraints every Discover surface shares — genres, tags, type, status, chapter
 * count, year and rating. Kept in a hook so the sliders' "no constraint" positions and the 0–10 to
 * 0–100 rating conversion are written once: a filter that says `minRating: 7` where the dump stores
 * 70 silently matches everything, and that is not a mistake worth being able to make twice.
 *
 * Deliberately not the Recommended tab's whole panel, which also owns seeds, obscurity and
 * diversity, and the saved-defaults round trip. Those are properties of the recommender, not of the
 * catalogue.
 */
export function useCatalogueFilters(initial?: RecommendationFilters) {
  const [genres, setGenres] = useState<string[]>(initial?.genres ?? [])
  const [tags, setTags] = useState<string[]>(initial?.tags ?? [])
  const [types, setTypes] = useState<string[]>(initial?.types ?? [])
  const [statuses, setStatuses] = useState<string[]>(initial?.statuses ?? [])
  const [years, setYears] = useState<[number, number]>([
    initial?.yearMin ?? YEAR_MIN,
    initial?.yearMax ?? YEAR_MAX,
  ])
  const [chapters, setChapters] = useState<[number, number]>([
    initial?.minChapters ?? CHAPTER_MIN,
    initial?.maxChapters ?? CHAPTER_MAX,
  ])
  const [minRating, setMinRating] = useState((initial?.minRating ?? 0) / 10)

  const isCustomized =
    genres.length > 0 ||
    tags.length > 0 ||
    types.length > 0 ||
    statuses.length > 0 ||
    years[0] > YEAR_MIN ||
    years[1] < YEAR_MAX ||
    minRating > 0 ||
    chapters[0] > CHAPTER_MIN ||
    chapters[1] < CHAPTER_MAX

  // Only constrained fields are sent: a slider parked at its end is "no constraint", not a bound,
  // and sending it would drop every row whose year or chapter count the dump doesn't know.
  const build = (): RecommendationFilters => {
    const f: RecommendationFilters = {}
    if (years[0] > YEAR_MIN) f.yearMin = years[0]
    if (years[1] < YEAR_MAX) f.yearMax = years[1]
    if (types.length) f.types = types
    if (statuses.length) f.statuses = statuses
    if (genres.length) f.genres = genres
    if (tags.length) f.tags = tags
    if (chapters[0] > CHAPTER_MIN) f.minChapters = chapters[0]
    if (chapters[1] < CHAPTER_MAX) f.maxChapters = chapters[1]
    if (minRating > 0) f.minRating = minRating * 10 // slider is 0–10, the dump's rating is 0–100
    return f
  }

  // Stable, because callers clear the panel from an effect keyed on what they're filtering (the
  // rail modal resets when a different rail opens). An identity that changed every render would
  // re-run that effect every render, which is a reset loop, not a reset.
  const reset = useCallback(() => {
    setGenres([])
    setTags([])
    setTypes([])
    setStatuses([])
    setYears([YEAR_MIN, YEAR_MAX])
    setChapters([CHAPTER_MIN, CHAPTER_MAX])
    setMinRating(0)
  }, [])

  // Seeds the panel from a stored spec once it arrives. `initial` cannot do this: the saved
  // default is fetched, so it is undefined on the render that runs the state initializers. Stable
  // for the same reason `reset` is — callers hydrate from an effect.
  const hydrate = useCallback((f: RecommendationFilters) => {
    setGenres(f.genres ?? [])
    setTags(f.tags ?? [])
    setTypes(f.types ?? [])
    setStatuses(f.statuses ?? [])
    setYears([f.yearMin ?? YEAR_MIN, f.yearMax ?? YEAR_MAX])
    setChapters([f.minChapters ?? CHAPTER_MIN, f.maxChapters ?? CHAPTER_MAX])
    setMinRating((f.minRating ?? 0) / 10) // stored on the dump's 0–100 scale, the slider is 0–10
  }, [])

  return {
    isCustomized,
    build,
    reset,
    hydrate,
    controls: {
      genres, setGenres,
      tags, setTags,
      types, setTypes,
      statuses, setStatuses,
      years, setYears,
      chapters, setChapters,
      minRating, setMinRating,
    },
  }
}

export type CatalogueFilterControls = ReturnType<typeof useCatalogueFilters>['controls']

/** The inputs for `useCatalogueFilters`' state. Layout only; it owns nothing. */
export function CatalogueFilters({
  controls,
  cols = { base: 1, sm: 2, lg: 4 },
}: {
  controls: CatalogueFilterControls
  cols?: Record<string, number>
}) {
  const { data: tagOptions } = useRecommendationTags()
  const {
    genres, setGenres,
    tags, setTags,
    types, setTypes,
    statuses, setStatuses,
    years, setYears,
    chapters, setChapters,
    minRating, setMinRating,
  } = controls

  const tagNothingFound = useMemo(
    () =>
      (tagOptions?.length ?? 0) === 0
        ? 'Tags appear once the recommendation index is built'
        : 'No matches',
    [tagOptions],
  )

  return (
    <SimpleGrid cols={cols} spacing="lg">
      <MultiSelect
        label="Genres"
        placeholder={genres.length ? undefined : 'Any'}
        data={GENRE_OPTIONS}
        value={genres}
        onChange={setGenres}
        searchable
        clearable
        hidePickedOptions
        maxDropdownHeight={260}
      />
      <MultiSelect
        label="Tags"
        placeholder={tags.length ? undefined : 'Any'}
        data={tagOptions ?? []}
        value={tags}
        onChange={setTags}
        searchable
        clearable
        hidePickedOptions
        limit={50}
        nothingFoundMessage={tagNothingFound}
        maxDropdownHeight={260}
      />
      <MultiSelect
        label="Type"
        placeholder={types.length ? undefined : 'Any'}
        data={TYPE_OPTIONS}
        value={types}
        onChange={setTypes}
        clearable
      />
      <MultiSelect
        label="Status"
        placeholder={statuses.length ? undefined : 'Any'}
        data={STATUS_OPTIONS}
        value={statuses}
        onChange={setStatuses}
        clearable
      />
      <div>
        <Text size="sm" fw={500} mb={4}>
          Chapters: {chapters[0]}–{chapters[1] >= CHAPTER_MAX ? `${CHAPTER_MAX}+` : chapters[1]}
        </Text>
        <RangeSlider
          min={CHAPTER_MIN}
          max={CHAPTER_MAX}
          step={5}
          value={chapters}
          onChange={setChapters}
          label={(v) => (v >= CHAPTER_MAX ? `${CHAPTER_MAX}+` : `${v}`)}
          marks={[
            { value: CHAPTER_MIN, label: '0' },
            { value: 250, label: '250' },
            { value: CHAPTER_MAX, label: '500+' },
          ]}
        />
      </div>
      <div>
        <Text size="sm" fw={500} mb={4}>
          Year: {years[0]}–{years[1]}
        </Text>
        <RangeSlider
          min={YEAR_MIN}
          max={YEAR_MAX}
          value={years}
          onChange={setYears}
          marks={[
            { value: YEAR_MIN, label: `${YEAR_MIN}` },
            { value: YEAR_MAX, label: `${YEAR_MAX}` },
          ]}
        />
      </div>
      <div>
        <Text size="sm" fw={500} mb={4}>
          Minimum rating: {minRating > 0 ? `★ ${minRating.toFixed(1)}` : 'any'}
        </Text>
        <Slider
          min={0}
          max={9.5}
          step={0.5}
          value={minRating}
          onChange={setMinRating}
          label={(v) => (v > 0 ? `★ ${v.toFixed(1)}` : 'any')}
          marks={[
            { value: 0, label: 'any' },
            { value: 7, label: '7' },
            { value: 9, label: '9' },
          ]}
        />
      </div>
    </SimpleGrid>
  )
}

/**
 * Reset/Apply pair, so the callers agree on wording and disabled state. "Save as default" only
 * appears where there is somewhere to save to: the rail modal's filters are scoped to the rail
 * that is open, so a default there would mean nothing.
 */
export function CatalogueFilterActions({
  isCustomized,
  onReset,
  onApply,
  onSaveAsDefault,
  saving = false,
}: {
  isCustomized: boolean
  onReset: () => void
  onApply: () => void
  onSaveAsDefault?: () => void
  saving?: boolean
}) {
  return (
    <Group justify="flex-end">
      {onSaveAsDefault && (
        <Button
          variant="subtle"
          size="xs"
          leftSection={<IconDeviceFloppy size={14} />}
          loading={saving}
          onClick={onSaveAsDefault}
          // Never disabled: saving an untouched panel is how a stored default gets cleared.
          title={
            isCustomized
              ? 'Open the search with these filters from now on'
              : 'Clear your saved default'
          }
        >
          Save as default
        </Button>
      )}
      <Button variant="subtle" size="xs" onClick={onReset} disabled={!isCustomized}>
        Reset
      </Button>
      <Button size="xs" onClick={onApply}>
        Apply
      </Button>
    </Group>
  )
}
