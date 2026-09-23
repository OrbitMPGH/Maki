import { useCallback, useMemo, type ReactNode } from 'react'
import { Trans, useLingui } from '@lingui/react/macro'
import { useLingui as useLinguiReact } from '@lingui/react'
import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { usePageState } from '../lib/pageState'
import { Button, Group, MultiSelect, RangeSlider, SimpleGrid, Slider, Stack, Text } from '@mantine/core'
import { IconDeviceFloppy } from '@tabler/icons-react'
import {
  allowedContentRatings,
  BROWSE_SORTS,
  CONTENT_RATING_LABELS,
  type CatalogueRule,
  type RecommendationFilters,
} from '../api/hooks'
import { TermFilters, useTermFilters } from './CatalogueRules'
import { useAuth } from '../auth/AuthProvider'
import { useLabel } from '../i18n-context'

/** {@link BROWSE_SORTS} as `Select` data in the current language. */
export function useBrowseSortOptions() {
  const renderLabel = useLabel()
  return useMemo(
    () => BROWSE_SORTS.map((s) => ({ value: s.value, label: renderLabel(s.label) })),
    [renderLabel],
  )
}

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
 * The three lists above are wire values: they are matched against the MangaBaka dump and sent to
 * the server, so they stay in English forever. What a reader sees is separate, and these tables
 * hold descriptors rather than strings because a module evaluates once and would freeze whatever
 * language was active then. Render through the hooks below.
 */
export const TYPE_LABELS: Record<string, MessageDescriptor> = {
  manga: msg`Manga`,
  manhwa: msg`Manhwa`,
  manhua: msg`Manhua`,
  oel: msg`OEL`,
  other: msg`Other`,
}

const STATUS_LABELS: Record<string, MessageDescriptor> = {
  completed: msg`Completed`,
  releasing: msg`Releasing`,
  hiatus: msg`Hiatus`,
  cancelled: msg`Cancelled`,
}

export const GENRE_LABELS: Record<string, MessageDescriptor> = {
  Action: msg`Action`,
  Adventure: msg`Adventure`,
  Comedy: msg`Comedy`,
  Drama: msg`Drama`,
  Ecchi: msg`Ecchi`,
  Fantasy: msg`Fantasy`,
  Harem: msg`Harem`,
  Historical: msg`Historical`,
  Horror: msg`Horror`,
  Isekai: msg`Isekai`,
  Josei: msg`Josei`,
  'Martial Arts': msg`Martial Arts`,
  Mecha: msg`Mecha`,
  Mystery: msg`Mystery`,
  Psychological: msg`Psychological`,
  Romance: msg`Romance`,
  'School Life': msg`School Life`,
  'Sci-Fi': msg`Sci-Fi`,
  Seinen: msg`Seinen`,
  Shoujo: msg`Shoujo`,
  Shounen: msg`Shounen`,
  'Slice of Life': msg`Slice of Life`,
  Sports: msg`Sports`,
  Supernatural: msg`Supernatural`,
  Thriller: msg`Thriller`,
  Tragedy: msg`Tragedy`,
  'Boys Love': msg`Boys Love`,
  'Girls Love': msg`Girls Love`,
}

/**
 * `{ value, label }` for a `MultiSelect`, built per render so a language switch reaches the
 * dropdowns. `i18n.locale` is in the deps on purpose: `_` alone is stable across an activate, so a
 * memo keyed only on it keeps handing back the previous language with nothing to show for it.
 */
function useSplitOptions(values: string[], labels: Record<string, MessageDescriptor>) {
  const { _, i18n } = useLinguiReact()
  return useMemo(
    () => values.map((value) => ({ value, label: labels[value] ? _(labels[value]) : value })),
    [values, labels, _, i18n.locale],
  )
}

export function useTypeOptions() {
  return useSplitOptions(TYPE_OPTIONS, TYPE_LABELS)
}

export function useStatusOptions() {
  return useSplitOptions(STATUS_OPTIONS, STATUS_LABELS)
}

export function useGenreOptions() {
  return useSplitOptions(GENRE_OPTIONS, GENRE_LABELS)
}

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
  contentRatings?: string[] | null
  rules?: CatalogueRule[] | null
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
  if (spec.contentRatings?.length) f.contentRatings = spec.contentRatings
  if (spec.rules?.length) f.rules = spec.rules
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
/**
 * @param scope Names a place for the panel to be remembered under, so leaving the page and coming
 *   back finds the same filters. Omit it for a panel with no page of its own, such as the one in
 *   the rail modal, which is reset every time it opens anyway.
 */
export function useCatalogueFilters(initial?: RecommendationFilters, scope?: string) {
  // A null key is `usePageState` behaving as plain `useState`, which is what an unscoped panel
  // wants: the rail modal resets itself every time it opens, so remembering it would be noise.
  const at = (field: string) => (scope ? `${scope}:${field}` : null)

  const terms = useTermFilters(at('terms'), initial)
  const [types, setTypes] = usePageState<string[]>(at('types'), initial?.types ?? [])
  const [statuses, setStatuses] = usePageState<string[]>(at('statuses'), initial?.statuses ?? [])
  const [years, setYears] = usePageState<[number, number]>(at('years'), [
    initial?.yearMin ?? YEAR_MIN,
    initial?.yearMax ?? YEAR_MAX,
  ])
  const [chapters, setChapters] = usePageState<[number, number]>(at('chapters'), [
    initial?.minChapters ?? CHAPTER_MIN,
    initial?.maxChapters ?? CHAPTER_MAX,
  ])
  const [minRating, setMinRating] = usePageState(at('min-rating'), (initial?.minRating ?? 0) / 10)
  const [contentRatings, setContentRatings] = usePageState<string[]>(
    at('content-ratings'),
    initial?.contentRatings ?? [],
  )

  const isCustomized =
    terms.isCustomized ||
    types.length > 0 ||
    statuses.length > 0 ||
    years[0] > YEAR_MIN ||
    years[1] < YEAR_MAX ||
    minRating > 0 ||
    chapters[0] > CHAPTER_MIN ||
    chapters[1] < CHAPTER_MAX ||
    contentRatings.length > 0

  // Only constrained fields are sent: a slider parked at its end is "no constraint", not a bound,
  // and sending it would drop every row whose year or chapter count the dump doesn't know.
  const build = (): RecommendationFilters => {
    const f: RecommendationFilters = {}
    if (years[0] > YEAR_MIN) f.yearMin = years[0]
    if (years[1] < YEAR_MAX) f.yearMax = years[1]
    if (types.length) f.types = types
    if (statuses.length) f.statuses = statuses
    Object.assign(f, terms.build())
    if (chapters[0] > CHAPTER_MIN) f.minChapters = chapters[0]
    if (chapters[1] < CHAPTER_MAX) f.maxChapters = chapters[1]
    if (minRating > 0) f.minRating = minRating * 10 // slider is 0–10, the dump's rating is 0–100
    if (contentRatings.length) f.contentRatings = contentRatings
    return f
  }

  // Stable, because callers clear the panel from an effect keyed on what they're filtering (the
  // rail modal resets when a different rail opens). An identity that changed every render would
  // re-run that effect every render, which is a reset loop, not a reset.
  const resetTerms = terms.reset
  const hydrateTerms = terms.hydrate
  const reset = useCallback(() => {
    resetTerms()
    setTypes([])
    setStatuses([])
    setYears([YEAR_MIN, YEAR_MAX])
    setChapters([CHAPTER_MIN, CHAPTER_MAX])
    setMinRating(0)
    setContentRatings([])
    // Every setter here is a `useState` setter, page-backed or not, so this stays stable.
  }, [resetTerms, setTypes, setStatuses, setYears, setChapters, setMinRating, setContentRatings])

  // Seeds the panel from a stored spec once it arrives. `initial` cannot do this: the saved
  // default is fetched, so it is undefined on the render that runs the state initializers. Stable
  // for the same reason `reset` is — callers hydrate from an effect.
  const hydrate = useCallback((f: RecommendationFilters) => {
    hydrateTerms(f)
    setTypes(f.types ?? [])
    setStatuses(f.statuses ?? [])
    setYears([f.yearMin ?? YEAR_MIN, f.yearMax ?? YEAR_MAX])
    setChapters([f.minChapters ?? CHAPTER_MIN, f.maxChapters ?? CHAPTER_MAX])
    setMinRating((f.minRating ?? 0) / 10) // stored on the dump's 0–100 scale, the slider is 0–10
    setContentRatings(f.contentRatings ?? [])
  }, [hydrateTerms, setTypes, setStatuses, setYears, setChapters, setMinRating, setContentRatings])

  return {
    isCustomized,
    build,
    reset,
    hydrate,
    controls: {
      terms: { state: terms.state, setState: terms.setState },
      types, setTypes,
      statuses, setStatuses,
      years, setYears,
      chapters, setChapters,
      minRating, setMinRating,
      contentRatings, setContentRatings,
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
  const { t } = useLingui()
  const { me } = useAuth()
  const renderLabel = useLabel()
  const { i18n } = useLingui()
  const typeOptions = useTypeOptions()
  const statusOptions = useStatusOptions()
  const {
    terms,
    types, setTypes,
    statuses, setStatuses,
    years, setYears,
    chapters, setChapters,
    minRating, setMinRating,
    contentRatings, setContentRatings,
  } = controls

  // Only ratings at or below the signed-in user's own ceiling: picking one they can't see would
  // just come back empty, and the option shouldn't be offered in the first place.
  const contentRatingOptions = useMemo(
    () =>
      allowedContentRatings(me?.maxContentRating).map((value) => ({
        value,
        label: renderLabel(CONTENT_RATING_LABELS[value]),
      })),
    [me?.maxContentRating, renderLabel, i18n.locale],
  )

  const chaptersMin = chapters[0]
  const chaptersMax = chapters[1] >= CHAPTER_MAX ? `${CHAPTER_MAX}+` : chapters[1]
  const yearMin = years[0]
  const yearMax = years[1]
  const ratingLabel = minRating > 0 ? `★ ${minRating.toFixed(1)}` : t`any`

  return (
    <Stack gap="lg">
      <TermFilters controls={terms} />
      <SimpleGrid cols={cols} spacing="lg">
        <MultiSelect
          label={t`Type`}
          placeholder={types.length ? undefined : t`Any`}
          data={typeOptions}
          value={types}
          onChange={setTypes}
          clearable
        />
        <MultiSelect
          label={t`Status`}
          placeholder={statuses.length ? undefined : t`Any`}
          data={statusOptions}
          value={statuses}
          onChange={setStatuses}
          clearable
        />
        <MultiSelect
          label={t`Content rating`}
          placeholder={contentRatings.length ? undefined : t`Any`}
          data={contentRatingOptions}
          value={contentRatings}
          onChange={setContentRatings}
          clearable
        />
        <div>
          <Text size="sm" fw={500} mb={4}>
            <Trans>
              Chapters: {chaptersMin}–{chaptersMax}
            </Trans>
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
            <Trans>
              Year: {yearMin}–{yearMax}
            </Trans>
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
            minRange={0}
          />
        </div>
        <div>
          <Text size="sm" fw={500} mb={4}>
            <Trans>Minimum rating: {ratingLabel}</Trans>
          </Text>
          <Slider
            min={0}
            max={9.5}
            step={0.5}
            value={minRating}
            onChange={setMinRating}
            label={(v) => (v > 0 ? `★ ${v.toFixed(1)}` : t`any`)}
            marks={[
              { value: 0, label: t`any` },
              { value: 7, label: '7' },
              { value: 9, label: '9' },
            ]}
          />
        </div>
      </SimpleGrid>
    </Stack>
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
  extra,
}: {
  isCustomized: boolean
  onReset: () => void
  onApply: () => void
  onSaveAsDefault?: () => void
  saving?: boolean
  /** Sits on the left: saved filters, the never-show list, the live count. */
  extra?: ReactNode
}) {
  const { t } = useLingui()
  return (
    <Group justify="space-between" gap="xs" wrap="wrap">
      <Group gap="sm" wrap="wrap">{extra}</Group>
      <Group justify="flex-end" gap="xs">
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
                ? t`Open the search with these filters from now on`
                : t`Clear your saved default`
            }
          >
            {t`Save as default`}
          </Button>
        )}
        <Button variant="subtle" size="xs" onClick={onReset} disabled={!isCustomized}>
          {t`Reset`}
        </Button>
        <Button size="xs" onClick={onApply}>
          {t`Apply`}
        </Button>
      </Group>
    </Group>
  )
}
