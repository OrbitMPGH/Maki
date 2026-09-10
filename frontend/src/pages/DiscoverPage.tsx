// Loaded in the shell rather than the tab so it lands once, whichever tab opens first.
import '@mantine/charts/styles.css'
import { useCallback, useEffect, useMemo, useState } from 'react'
import { useLocation, useNavigate, useParams } from 'react-router-dom'
import {
  ActionIcon,
  Alert,
  Badge,
  Button,
  Card,
  Collapse,
  Group,
  Modal,
  MultiSelect,
  RangeSlider,
  SimpleGrid,
  Skeleton,
  Slider,
  Stack,
  Tabs,
  Text,
  ThemeIcon,
  Title,
  Tooltip,
} from '@mantine/core'
import {
  IconAdjustmentsHorizontal,
  IconAffiliate,
  IconAlertTriangle,
  IconChevronRight,
  IconCompass,
  IconDeviceFloppy,
  IconFlame,
  IconLibrary,
  IconLayoutGrid,
  IconPlus,
  IconRefresh,
  IconHeartFilled,
  IconSparkles,
  IconUsers,
} from '@tabler/icons-react'
import { useDebouncedValue } from '@mantine/hooks'
import { notifications } from '@mantine/notifications'
import {
  allowedContentRatings,
  CONTENT_RATING_LABELS,
  useDiscover,
  useDiscoverFeed,
  useDiscoverGenres,
  useDiscoverCohort,
  useDiscoverRecentActivity,
  useDiscoverSideInterests,
  READER_COHORT_FEED,
  useMetadataSearch,
  useRecommendationDefaults,
  useRecommendations,
  useRecommendationTags,
  useRootFolders,
  useSaveRecommendationDefaults,
  useSeries,
  useSeriesIdLookup,
  type DiscoverRail,
  type RecommendationDefaults,
  type RecommendationFilters,
  type RecommendationItem,
  type RecommendationRequest,
  type RecommendationApplyState,
} from '../api/hooks'
import { useAuth } from '../auth/AuthProvider'
import {
  CatalogueFilterActions,
  CatalogueFilters,
  CHAPTER_MAX,
  CHAPTER_MIN,
  filtersFromSpec,
  GENRE_OPTIONS,
  STATUS_OPTIONS,
  TYPE_OPTIONS,
  useCatalogueFilters,
  YEAR_MAX,
  YEAR_MIN,
} from '../components/CatalogueFilters'
import { DiscoverCatalogue } from '../components/discover/DiscoverCatalogue'
import { DiscoverGenreWall } from '../components/discover/DiscoverGenreWall'
import { DiscoverHero } from '../components/discover/DiscoverHero'
import { DiscoverSeedStrip } from '../components/discover/DiscoverSeedStrip'
import { DiscoverTasteStrip } from '../components/discover/DiscoverTasteStrip'
import { DiscoverDetailModal } from '../components/discover/DiscoverDetailModal'
import {
  DiscoverRailRow,
  EngineCard,
  EngineRailRow,
  RecommendationCard,
  RecommendationRow,
} from '../components/ui/DiscoverRail'
import { CatalogueBrowser, PosterSkeletons as SharedPosterSkeletons } from '../components/CatalogueBrowser'
import { EmptyState } from '../components/ui/EmptyState'
import { PageHeader } from '../components/ui/PageHeader'
import { SurfaceFrame } from '../components/ui/SurfaceFrame'
import { usePageState } from '../lib/pageState'
import { TasteTab } from './discover/TasteTab'
import { SectionHeader } from '../components/ui/SectionHeader'
import {
  DensityControl,
  POSTER_COLS_BY_DENSITY,
  ViewPrefsControls,
  useDensityPref,
  useViewPrefs,
  type Density,
  type DensityPref,
} from '../components/ui/viewPrefs'

/**
 * Where the Recommended panel is remembered between visits. Its own key rather than the route,
 * because the tab is reached at one path and nothing else on Discover shares its controls.
 */
const MEM = 'discover-recommended'

/** Whether a saved default constrains anything. An empty spec is how "no default" reads back. */
function hasAnyDefault(d: RecommendationDefaults | undefined): boolean {
  if (!d) return false
  return (
    (d.seeds?.length ?? 0) > 0 ||
    d.obscurity !== 0 ||
    d.diversity !== 0 ||
    Object.keys(filtersFromSpec(d)).length > 0
  )
}

function PosterSkeletons({
  density = 'default',
  viewMode = 'grid',
}: {
  density?: Density
  viewMode?: 'grid' | 'list'
}) {
  return <SharedPosterSkeletons density={density} viewMode={viewMode} />
}

function DiscoverHeroSkeleton() {
  return (
    <div className="discover-loading-hero" aria-hidden>
      <div className="discover-loading-feature">
        <Skeleton className="discover-loading-poster" radius="lg" />
        <Stack gap="sm" style={{ flex: 1 }}>
          <Skeleton h={10} w={110} />
          <Skeleton h={34} w="72%" />
          <Skeleton h={14} w="45%" />
          <Group gap="xs">
            <Skeleton h={26} w={74} radius="xl" />
            <Skeleton h={26} w={92} radius="xl" />
            <Skeleton h={26} w={68} radius="xl" />
          </Group>
          <Skeleton h={12} w="84%" mt="xs" />
          <Skeleton h={12} w="63%" />
        </Stack>
      </div>
      <div className="discover-loading-picks">
        <Skeleton h={10} w={84} mb={4} />
        {Array.from({ length: 5 }, (_, i) => (
          <Group key={i} gap="sm" wrap="nowrap">
            <Skeleton h={48} w={32} radius="sm" style={{ flexShrink: 0 }} />
            <Stack gap={6} style={{ flex: 1 }}>
              <Skeleton h={10} w={`${72 - (i % 3) * 10}%`} />
              <Skeleton h={8} w="44%" />
            </Stack>
          </Group>
        ))}
      </div>
    </div>
  )
}

function DiscoverRailSkeleton({ engine = false }: { engine?: boolean }) {
  return (
    <div aria-hidden>
      <Group gap="sm" mt="xl" mb="sm">
        <Skeleton circle h={30} />
        <Skeleton h={18} w={190} />
      </Group>
      <div className="discover-rail" data-engine={engine || undefined}>
        {Array.from({ length: 12 }, (_, i) => (
          <div key={i} className="discover-rail-item">
            <Skeleton radius="lg" style={{ aspectRatio: '2 / 3' }} />
          </div>
        ))}
      </div>
    </div>
  )
}

function DiscoverCatalogueSkeleton({ density }: { density: Density }) {
  return (
    <div aria-hidden>
      <Group gap="xs" wrap="wrap" mb="md">
        {[82, 104, 96, 88, 112].map((width) => (
          <Skeleton key={width} h={32} w={width} radius="md" />
        ))}
      </Group>
      <Group justify="space-between" mb="sm">
        <Skeleton h={10} w={64} />
        <Skeleton h={24} w={88} radius="md" />
      </Group>
      <PosterSkeletons density={density} />
    </div>
  )
}

function DiscoverGenreSkeleton() {
  return (
    <div aria-hidden>
      <Group gap="sm" mt="xl" mb="sm">
        <Skeleton circle h={30} />
        <Skeleton h={18} w={130} />
      </Group>
      <div className="discover-genre-wall">
        {Array.from({ length: 8 }, (_, i) => (
          <Skeleton key={i} h={80} radius="lg" />
        ))}
      </div>
    </div>
  )
}

/** The recommendation engine: Maki's library-driven "more like what you own" picks. */
function RecommendedTab() {
  const { data: library } = useSeries()
  const { data: rootFolders } = useRootFolders()
  const prefs = useViewPrefs('discover')
  const { viewMode, density } = prefs

  // --- customization controls ---
  // Remembered for the tab session (see usePageState): the panel is a dozen controls, and losing
  // it because you opened one of its own results and came back is losing real work.
  const [customizeOpen, setCustomizeOpen] = usePageState(`${MEM}:panel-open`, false)
  const [seedIds, setSeedIds] = usePageState<string[]>(`${MEM}:seeds`, [])
  const [seedSearch, setSeedSearch] = useState('')
  const [debouncedSearch] = useDebouncedValue(seedSearch, 300)
  const { data: seedSearchResults } = useMetadataSearch(debouncedSearch)
  const [years, setYears] = usePageState<[number, number]>(`${MEM}:years`, [YEAR_MIN, YEAR_MAX])
  const [types, setTypes] = usePageState<string[]>(`${MEM}:types`, [])
  const [statuses, setStatuses] = usePageState<string[]>(`${MEM}:statuses`, [])
  const [genres, setGenres] = usePageState<string[]>(`${MEM}:genres`, [])
  const [tags, setTags] = usePageState<string[]>(`${MEM}:tags`, [])
  const { data: tagOptions } = useRecommendationTags()
  const [chapters, setChapters] = usePageState<[number, number]>(`${MEM}:chapters`, [CHAPTER_MIN, CHAPTER_MAX])
  const [minRating, setMinRating] = usePageState(`${MEM}:min-rating`, 0)
  const [obscurity, setObscurity] = usePageState(`${MEM}:obscurity`, 0)
  const [diversity, setDiversity] = usePageState(`${MEM}:diversity`, 0)
  const [contentRatings, setContentRatings] = usePageState<string[]>(`${MEM}:content-ratings`, [])
  const { me } = useAuth()
  const contentRatingOptions = useMemo(
    () =>
      allowedContentRatings(me?.maxContentRating).map((value) => ({
        value,
        label: CONTENT_RATING_LABELS[value],
      })),
    [me?.maxContentRating],
  )

  // MangaBaka id → title, accumulated from the library and every seed search so selected
  // seeds keep their labels even after the search box clears.
  // Remembered too, or restored seeds would come back as bare ids until the library query lands.
  const [labelCache, setLabelCache] = usePageState<Record<string, string>>(`${MEM}:seed-labels`, {})
  useEffect(() => {
    setLabelCache((prev) => {
      const next = { ...prev }
      for (const s of library ?? []) {
        if (s.mangaBakaId != null) next[String(s.mangaBakaId)] = s.title
      }
      for (const r of seedSearchResults ?? []) next[r.providerId] = r.title
      return next
    })
  }, [library, seedSearchResults, setLabelCache])
  const seedOptions = useMemo(
    () => Object.entries(labelCache).map(([value, label]) => ({ value, label })),
    [labelCache],
  )

  // The request actually driving the query; `nonce` forces a refetch on Apply/Refresh.
  const [applied, setApplied] = usePageState<RecommendationRequest & { nonce: number }>(
    `${MEM}:applied`,
    { nonce: 0 },
  )

  // --- saved defaults ---
  // The panel is seeded from the user's saved default exactly once, and the query stays disabled
  // until that has happened: enabling it earlier would fire an unfiltered request that the
  // hydration then immediately replaces with the filtered one. An error hydrates too, so a failed
  // read of the defaults degrades to "no default" rather than to a tab that never loads.
  const { data: savedDefaults, isSuccess: defaultsLoaded, isError: defaultsFailed } =
    useRecommendationDefaults()
  const saveDefaults = useSaveRecommendationDefaults()
  // Remembered with the panel: a restored panel is already seeded, and letting the saved default
  // run over it would throw away exactly what the restore is for.
  const [hydrated, setHydrated] = usePageState(`${MEM}:hydrated`, false)

  // Filters carried over from the taste profile. Router state, so nothing is written back to the
  // saved default and a reload falls through to it as normal.
  const location = useLocation()
  const navigate = useNavigate()
  // Memoized on the state itself: without it this is a fresh object every render and the hydration
  // effect below re-runs on each one until it manages to latch.
  const carried = useMemo(() => {
    const state = location.state as RecommendationApplyState | null
    return state?.source === 'taste-profile' || state?.source === 'discover-hero'
      ? { filters: state.recommendationFilters, seeds: state.seeds, source: state.source }
      : null
  }, [location.state])

  useEffect(() => {
    // Carried filters are checked before `hydrated`, not after: they are router state the Taste tab
    // has only just set, and the panel can already be hydrated from a remembered visit. Taken
    // before the saved default is consulted too, so a slow /defaults response cannot race in and
    // overwrite what the user just chose to apply.
    if (carried) {
      const { filters: carriedFilters, seeds: carriedSeeds, source } = carried
      setYears([carriedFilters.yearMin ?? YEAR_MIN, carriedFilters.yearMax ?? YEAR_MAX])
      setTypes(carriedFilters.types ?? [])
      setStatuses(carriedFilters.statuses ?? [])
      setGenres(carriedFilters.genres ?? [])
      setTags(carriedFilters.tags ?? [])
      setChapters([carriedFilters.minChapters ?? CHAPTER_MIN, carriedFilters.maxChapters ?? CHAPTER_MAX])
      setMinRating((carriedFilters.minRating ?? 0) / 10)
      setContentRatings(carriedFilters.contentRatings ?? [])
      setObscurity(0)
      setDiversity(0)
      if (source === 'discover-hero') setCustomizeOpen(true)
      // Seeds arrive when the caller asked for one taste group rather than the whole library. Only
      // some carry titles, so the label cache is filled from what there is and the rest resolve
      // once the library query lands.
      setSeedIds((carriedSeeds ?? []).map((seed) => String(seed.id)))
      if (carriedSeeds?.length) {
        setLabelCache((prev) => {
          const next = { ...prev }
          for (const seed of carriedSeeds) {
            if (seed.title) next[String(seed.id)] = seed.title
          }
          return next
        })
      }
      setApplied({
        seedIds: carriedSeeds?.length ? carriedSeeds.map((seed) => seed.id) : undefined,
        filters: Object.keys(carriedFilters).length ? carriedFilters : undefined,
        nonce: 0,
      })
      setHydrated(true)
      // Drop it once used, or a reload or a back/forward would silently re-apply it.
      navigate(location.pathname, { replace: true, state: null })
      return
    }
    if (hydrated) return
    if (defaultsFailed) {
      setHydrated(true)
      return
    }
    if (!defaultsLoaded || !savedDefaults) return

    const d = savedDefaults
    const seeds = d.seeds ?? []
    setSeedIds(seeds.map((s) => String(s.id)))
    setLabelCache((prev) => {
      const next = { ...prev }
      for (const s of seeds) {
        if (s.title) next[String(s.id)] = s.title
      }
      return next
    })
    setYears([d.yearMin ?? YEAR_MIN, d.yearMax ?? YEAR_MAX])
    setTypes(d.types ?? [])
    setStatuses(d.statuses ?? [])
    setGenres(d.genres ?? [])
    setTags(d.tags ?? [])
    setChapters([d.minChapters ?? CHAPTER_MIN, d.maxChapters ?? CHAPTER_MAX])
    setMinRating((d.minRating ?? 0) / 10) // stored on the dump's 0–100 scale, slider is 0–10
    setObscurity(d.obscurity)
    setDiversity(d.diversity)
    setContentRatings(d.contentRatings ?? [])

    const filters = filtersFromSpec(d)
    setApplied({
      seedIds: seeds.length ? seeds.map((s) => s.id) : undefined,
      filters: Object.keys(filters).length ? filters : undefined,
      obscurity: d.obscurity !== 0 ? d.obscurity : undefined,
      diversity: d.diversity !== 0 ? d.diversity : undefined,
      nonce: 0,
    })
    setHydrated(true)
  }, [
    hydrated, defaultsLoaded, defaultsFailed, savedDefaults, carried, location.pathname, navigate,
    setSeedIds, setLabelCache, setYears, setTypes, setStatuses, setGenres, setTags, setChapters,
    setMinRating, setObscurity, setDiversity, setContentRatings, setCustomizeOpen, setApplied,
    setHydrated,
  ])

  const { data, isFetching, error, fetchNextPage, hasNextPage, isFetchingNextPage } =
    useRecommendations(applied, hydrated)
  const related = data?.pages[0]?.related ?? []
  const similar = data?.pages.flatMap((p) => p.similar) ?? []

  const currentFilters = () => {
    const filters: RecommendationFilters = {}
    if (years[0] > YEAR_MIN) filters.yearMin = years[0]
    if (years[1] < YEAR_MAX) filters.yearMax = years[1]
    if (types.length) filters.types = types
    if (statuses.length) filters.statuses = statuses
    if (genres.length) filters.genres = genres
    if (tags.length) filters.tags = tags
    if (chapters[0] > CHAPTER_MIN) filters.minChapters = chapters[0]
    if (chapters[1] < CHAPTER_MAX) filters.maxChapters = chapters[1]
    if (minRating > 0) filters.minRating = minRating * 10 // slider is 0–10, dump rating is 0–100
    if (contentRatings.length) filters.contentRatings = contentRatings
    return filters
  }

  const apply = (refresh = false) => {
    const filters = currentFilters()
    setApplied((prev) => ({
      seedIds: seedIds.length ? seedIds.map(Number) : undefined,
      filters: Object.keys(filters).length ? filters : undefined,
      obscurity: obscurity !== 0 ? obscurity : undefined,
      diversity: diversity !== 0 ? diversity : undefined,
      refresh,
      nonce: prev.nonce + 1,
    }))
  }

  /**
   * Stores the panel as this user's default, so the next visit opens with it already applied.
   * Saving an untouched panel clears the stored default: the server treats an empty spec as
   * "unset", which is what makes the one button both set and clear.
   */
  const saveAsDefault = () => {
    const spec: RecommendationDefaults = {
      ...currentFilters(),
      seeds: seedIds.map((id) => ({ id: Number(id), title: labelCache[id] ?? null })),
      obscurity,
      diversity,
    }
    saveDefaults.mutate(spec, {
      onSuccess: () =>
        notifications.show({
          color: 'green',
          message: isCustomized ? 'Saved as your default' : 'Default cleared',
        }),
      onError: (err) =>
        notifications.show({ color: 'red', message: `Failed to save default: ${String(err)}` }),
    })
  }

  const reset = () => {
    setSeedIds([])
    setYears([YEAR_MIN, YEAR_MAX])
    setTypes([])
    setStatuses([])
    setGenres([])
    setTags([])
    setChapters([CHAPTER_MIN, CHAPTER_MAX])
    setMinRating(0)
    setObscurity(0)
    setDiversity(0)
    setContentRatings([])
    setApplied((prev) => ({ nonce: prev.nonce + 1 }))
  }

  const isCustomized =
    seedIds.length > 0 ||
    years[0] > YEAR_MIN ||
    years[1] < YEAR_MAX ||
    types.length > 0 ||
    statuses.length > 0 ||
    genres.length > 0 ||
    tags.length > 0 ||
    chapters[0] > CHAPTER_MIN ||
    chapters[1] < CHAPTER_MAX ||
    minRating > 0 ||
    obscurity !== 0 ||
    diversity !== 0 ||
    contentRatings.length > 0

  // Compact summary of active constraints, shown under the header when the panel is closed.
  const activeFilterChips = useMemo(() => {
    const chips: string[] = []
    if (seedIds.length > 0) {
      chips.push(seedIds.length === 1 ? '1 seed' : `${seedIds.length} seeds`)
    }
    if (years[0] > YEAR_MIN || years[1] < YEAR_MAX) chips.push(`${years[0]}–${years[1]}`)
    if (minRating > 0) chips.push(`★ ≥ ${minRating.toFixed(1)}`)
    if (chapters[0] > CHAPTER_MIN || chapters[1] < CHAPTER_MAX) {
      chips.push(
        `${chapters[0]}–${chapters[1] >= CHAPTER_MAX ? `${CHAPTER_MAX}+` : chapters[1]} ch`,
      )
    }
    if (obscurity !== 0) chips.push(obscurity > 0 ? 'hidden gems' : 'mainstream')
    if (diversity !== 0) chips.push(`varied (${diversity.toFixed(2)})`)
    for (const g of genres) chips.push(g)
    for (const t of tags) chips.push(t)
    for (const t of types) chips.push(t)
    for (const s of statuses) chips.push(s)
    for (const c of contentRatings) chips.push(CONTENT_RATING_LABELS[c] ?? c)
    return chips
  }, [
    seedIds, years, minRating, chapters, obscurity, diversity, genres, tags, types, statuses,
    contentRatings,
  ])

  // --- detail modal ---
  const [detailItem, setDetailItem] = useState<RecommendationItem | null>(null)

  // MangaBaka id → library series id, for "in library" detection and navigation.
  const seriesIdByMangaBaka = useMemo(() => {
    const map = new Map<number, number>()
    for (const s of library ?? []) {
      if (s.mangaBakaId != null) map.set(s.mangaBakaId, s.id)
    }
    return map
  }, [library])
  const seriesIdFor = (item: RecommendationItem) =>
    seriesIdByMangaBaka.get(Number(item.providerId)) ?? null

  return (
    <>
      <Group className="discover-recommended-controls" justify="flex-end" mb="md">
        <ViewPrefsControls prefs={prefs} />
        <Button
          variant={isCustomized ? 'light' : 'default'}
          leftSection={<IconAdjustmentsHorizontal size={16} />}
          onClick={() => setCustomizeOpen((o) => !o)}
        >
          {isCustomized ? 'Customized' : 'Customize'}
        </Button>
        <Button
          variant="default"
          leftSection={<IconRefresh size={16} />}
          loading={isFetching}
          onClick={() => apply(true)}
        >
          Refresh
        </Button>
      </Group>

      <Collapse expanded={customizeOpen}>
        <Card className="discover-recommended-customize" withBorder radius="md" padding="md" mb="md">
          <Stack gap="md">
            <MultiSelect
              label="Seed from"
              description="Base recommendations on these titles. Search adds any title from MangaBaka. Empty = your whole library."
              placeholder={seedIds.length ? undefined : 'Whole library'}
              data={seedOptions}
              value={seedIds}
              onChange={setSeedIds}
              searchable
              searchValue={seedSearch}
              onSearchChange={setSeedSearch}
              nothingFoundMessage={debouncedSearch.length > 1 ? 'No matches' : 'Type to search…'}
              clearable
              hidePickedOptions
              maxDropdownHeight={260}
            />

            <MultiSelect
              label="Genres"
              description="Only show titles tagged with every selected genre."
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
              description="Only show titles carrying every selected tag (from the MangaBaka tag vocabulary)."
              placeholder={tags.length ? undefined : 'Any'}
              data={tagOptions ?? []}
              value={tags}
              onChange={setTags}
              searchable
              clearable
              hidePickedOptions
              limit={50}
              nothingFoundMessage={
                (tagOptions?.length ?? 0) === 0
                  ? 'Tags appear once the recommendation index is built'
                  : 'No matches'
              }
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
            <MultiSelect
                label="Content rating"
                placeholder={contentRatings.length ? undefined : 'Any'}
                data={contentRatingOptions}
                value={contentRatings}
                onChange={setContentRatings}
                clearable
            />

            <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="lg">
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
                  minRange={0}
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
                  minRange={0}
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
              <div>
                <Text size="sm" fw={500} mb={4}>
                  Obscurity:{' '}
                  {obscurity === 0
                    ? 'balanced'
                    : obscurity > 0
                      ? `hidden gems (+${obscurity.toFixed(2)})`
                      : `mainstream (${obscurity.toFixed(2)})`}
                </Text>
                <Slider
                  min={-1}
                  max={1}
                  step={0.25}
                  value={obscurity}
                  onChange={setObscurity}
                  label={(v) => (v === 0 ? 'balanced' : v > 0 ? 'obscure' : 'popular')}
                  marks={[
                    { value: -1, label: 'popular' },
                    { value: 0, label: '·' },
                    { value: 1, label: 'gems' },
                  ]}
                  color={obscurity >= 0 ? 'grape' : 'blue'}
                />
              </div>
              <div>
                <Text size="sm" fw={500} mb={4}>
                  Variety:{' '}
                  {diversity === 0 ? 'closest matches' : `spread out (${diversity.toFixed(2)})`}
                </Text>
                <Slider
                  min={0}
                  max={1}
                  step={0.1}
                  value={diversity}
                  onChange={setDiversity}
                  label={(v) => (v === 0 ? 'closest' : v.toFixed(1))}
                  marks={[
                    { value: 0, label: 'closest' },
                    { value: 0.5, label: '·' },
                    { value: 1, label: 'varied' },
                  ]}
                  color="teal"
                />
                {/* Mark labels are absolutely positioned, so they take no layout space — this has
                    to clear them by hand or the caption lands on top of "closest"/"varied". */}
                <Text size="xs" c="dimmed" mt={26}>
                  Trades a little similarity for picks that aren't near-copies of each other.
                </Text>
              </div>
            </SimpleGrid>

            <Group justify="space-between">
              <Button
                variant="subtle"
                size="xs"
                leftSection={<IconDeviceFloppy size={14} />}
                loading={saveDefaults.isPending}
                // Nothing set and nothing stored: there is neither a default to save nor one to clear.
                disabled={!isCustomized && !hasAnyDefault(savedDefaults)}
                onClick={saveAsDefault}
                title={
                  isCustomized
                    ? 'Open Recommended with these filters from now on'
                    : 'Clear your saved default'
                }
              >
                {isCustomized ? 'Save as default' : 'Clear default'}
              </Button>
              <Group gap="xs">
                <Button variant="subtle" size="xs" onClick={reset} disabled={!isCustomized}>
                  Reset
                </Button>
                <Button size="xs" onClick={() => apply(false)}>
                  Apply
                </Button>
              </Group>
            </Group>
          </Stack>
        </Card>
      </Collapse>

      {isCustomized && !customizeOpen && (
        <Group className="discover-recommended-chips" gap={6} mb="md">
          {activeFilterChips.map((chip) => (
            <Badge key={chip} variant="light" color="brand" size="sm" radius="sm">
              {chip}
            </Badge>
          ))}
        </Group>
      )}

      {error && (
        <Alert className="discover-recommended-alert" color="yellow" variant="light">
          {String(error)}
        </Alert>
      )}
      {isFetching && !data && (
        <div className="discover-recommended-loading">
          <Text c="dimmed" size="sm" mb="sm">
            Scanning the MangaBaka database for matches…
          </Text>
          <PosterSkeletons density={density} viewMode={viewMode} />
        </div>
      )}

      {data && related.length === 0 && similar.length === 0 && (
        <EmptyState
          icon={IconSparkles}
          title={isCustomized ? 'No matches' : 'Nothing to recommend yet'}
          description={
            isCustomized
              ? 'No matches for these seeds and filters. Try loosening them.'
              : 'Add some series to your library first and Maki will suggest more like them.'
          }
          actionLabel={isCustomized ? undefined : 'Go to library'}
          actionTo={isCustomized ? undefined : '/library'}
          variant={isCustomized ? 'filtered' : 'setup'}
        />
      )}

      {similar.length > 0 && (
        <section className="discover-results-section discover-results-section--similar">
          <SectionHeader
            icon={IconSparkles}
            title={seedIds.length > 0 ? 'Feels like your seeds' : 'Because of what you collect'}
            count={similar.length}
          />
          {viewMode === 'grid' ? (
            <SimpleGrid cols={POSTER_COLS_BY_DENSITY[density]} spacing="md">
              {similar.map((item) => (
                <EngineCard
                  key={item.providerId}
                  item={item}
                  inLibrarySeriesId={seriesIdFor(item)}
                  onOpen={setDetailItem}
                />
              ))}
            </SimpleGrid>
          ) : (
            <Stack gap="xs">
              {similar.map((item) => (
                <RecommendationRow
                  key={item.providerId}
                  item={item}
                  inLibrarySeriesId={seriesIdFor(item)}
                  density={density}
                  onOpen={setDetailItem}
                />
              ))}
            </Stack>
          )}
          {hasNextPage && (
            <Group justify="center" mt="md">
              <Button
                variant="default"
                leftSection={<IconPlus size={16} />}
                loading={isFetchingNextPage}
                onClick={() => fetchNextPage()}
              >
                Show more
              </Button>
            </Group>
          )}
        </section>
      )}

      {related.length > 0 && (
        <section className="discover-results-section discover-results-section--related">
          <SectionHeader
            icon={IconAffiliate}
            title={seedIds.length > 0 ? 'Related to your seeds' : 'Related to your library'}
            count={related.length}
          />
          {viewMode === 'grid' ? (
            <SimpleGrid cols={POSTER_COLS_BY_DENSITY[density]} spacing="md">
              {related.map((item) => (
                <EngineCard
                  key={item.providerId}
                  item={item}
                  inLibrarySeriesId={seriesIdFor(item)}
                  onOpen={setDetailItem}
                />
              ))}
            </SimpleGrid>
          ) : (
            <Stack gap="xs">
              {related.map((item) => (
                <RecommendationRow
                  key={item.providerId}
                  item={item}
                  inLibrarySeriesId={seriesIdFor(item)}
                  density={density}
                  onOpen={setDetailItem}
                />
              ))}
            </Stack>
          )}
        </section>
      )}

      <DiscoverDetailModal
        item={detailItem}
        inLibrarySeriesId={detailItem ? seriesIdFor(detailItem) : null}
        rootFolders={rootFolders}
        onClose={() => setDetailItem(null)}
      />
    </>
  )
}

/**
 * Fullscreen "Show more" view of one rail: the same feed, but filterable (genre / status / type /
 * year / rating / chapters, like the Recommended panel) and showing many more than the rail's 40.
 * Card clicks bubble up to the shared detail modal via {@link onOpenItem}.
 */
function FeedExpandModal({
  rail,
  seriesIdFor,
  onOpenItem,
  onClose,
}: {
  rail: DiscoverRail | null
  seriesIdFor: (item: RecommendationItem) => number | null
  onOpenItem: (item: RecommendationItem) => void
  onClose: () => void
}) {
  const catalogue = useCatalogueFilters()
  const [applied, setApplied] = useState<RecommendationFilters>({})
  // Its own scope: the rails behind it are fixed-size rows, so this density is nobody else's.
  const { density, setDensity, cols } = useDensityPref('discover-expand')

  // Reset filters whenever a different rail is opened.
  const railKey = rail?.key
  const resetAll = catalogue.reset
  useEffect(() => {
    resetAll()
    setApplied({})
  }, [railKey, resetAll])

  // The personalised rail carries seeds instead of a browse feed, and `GetFeedAsync` has no
  // ordering for it — page the recommender with those seeds instead. Both queries are declared
  // unconditionally (hooks rules) and whichever one this rail isn't sits disabled.
  const seedIds = rail?.seedIds ?? null
  const personalised = (seedIds?.length ?? 0) > 0
  const sideInterest = rail?.feed === 'SideInterest'

  // The cohort rail is the third case: it carries neither a browse feed nor seeds, because its
  // ordering is "what your cohorts finished that you have not" and lives in neither the catalogue
  // nor the recommender. It pages its own endpoint, filters and all.
  const cohort = rail?.feed === READER_COHORT_FEED

  const feedRequest =
    rail && !personalised && !cohort
      ? { feed: rail.feed, genre: rail.genre, filters: applied, limit: 120 }
      : null
  const feedQuery = useDiscoverFeed(feedRequest)

  const cohortRequest = useMemo(
    () => (cohort ? { filters: applied, limit: 120 } : null),
    [cohort, applied],
  )
  const cohortQuery = useDiscoverCohort(cohortRequest, cohort)

  const recRequest = useMemo(() => {
    const base = rail?.filters
    const filters: RecommendationFilters = { ...base, ...applied }
    // A side-interest's tag or genre is its identity. Extra modal filters narrow that theme rather
    // than replacing it, while content ratings and scalar ranges can safely take the user's value.
    if (base?.genres?.length) {
      filters.genres = [...new Set([...base.genres, ...(applied.genres ?? [])])]
    }
    if (base?.tags?.length) {
      filters.tags = [...new Set([...base.tags, ...(applied.tags ?? [])])]
    }
    return { seedIds: seedIds ?? undefined, filters }
  }, [seedIds, rail?.filters, applied])
  const recQuery = useRecommendations(recRequest, personalised)
  // Relations lead here for the same reason they lead the rail itself: a sequel to something just
  // finished is the most actionable pick. They come from page 0 only — the pager walks `similar`.
  const recItems = useMemo(
    () =>
      recQuery.data
        ? [
            ...(sideInterest ? [] : (recQuery.data.pages[0]?.related ?? [])),
            ...recQuery.data.pages.flatMap((p) => p.similar),
          ]
        : undefined,
    [recQuery.data, sideInterest],
  )

  // A cohort rail whose filters exclude everything answers null rather than an empty rail, and the
  // two have to stay distinguishable from "not loaded yet": undefined holds the skeletons, [] shows
  // the empty state. Keyed on isSuccess because `data` is undefined in both the loading and the
  // never-ran cases.
  const cohortItems = cohortQuery.isSuccess ? (cohortQuery.data?.items ?? []) : undefined
  const items = personalised ? recItems : cohort ? cohortItems : feedQuery.data
  const isFetching = personalised
    ? recQuery.isFetching
    : cohort
      ? cohortQuery.isFetching
      : feedQuery.isFetching
  const error = personalised ? recQuery.error : cohort ? cohortQuery.error : feedQuery.error

  return (
    <Modal
      opened={rail != null}
      onClose={onClose}
      fullScreen
      title={
        <Group gap="xs">
          <ThemeIcon variant="light" color="brand" size="md" radius="md">
            <IconSparkles size={16} />
          </ThemeIcon>
          <Title order={4}>{rail?.title}</Title>
        </Group>
      }
      styles={{ body: { paddingTop: 'var(--mantine-spacing-md)' } }}
    >
      <Card withBorder radius="md" padding="md" mb="md">
        <Stack gap="md">
          <CatalogueFilters controls={catalogue.controls} />
          <CatalogueFilterActions
            isCustomized={catalogue.isCustomized || Object.keys(applied).length > 0}
            onReset={() => {
              catalogue.reset()
              setApplied({})
            }}
            onApply={() => setApplied(catalogue.build())}
          />
        </Stack>
      </Card>

      {error && (
        <Alert color="yellow" variant="light">
          {String(error)}
        </Alert>
      )}

      {isFetching && !items && <PosterSkeletons density={density} />}

      {items && items.length === 0 && (
        <EmptyState
          icon={IconCompass}
          title="No matches"
          description="No titles match these filters. Try loosening them."
        />
      )}

      {items && items.length > 0 && (
        <>
          <Group justify="space-between" mb="sm">
            <Text c="dimmed" size="sm">
              {items.length} title{items.length === 1 ? '' : 's'}
            </Text>
            <DensityControl value={density} onChange={setDensity} />
          </Group>
          <SimpleGrid cols={cols} spacing="md">
            {items.map((item) => (
              <RecommendationCard
                key={item.providerId}
                item={item}
                inLibrarySeriesId={seriesIdFor(item)}
                onOpen={onOpenItem}
                // A browse rail's cards all share one reason ("popular"), so the line is noise.
                // On the personalised rail it says which seed drove the pick, which is the point.
                reasonOverride={personalised ? undefined : null}
              />
            ))}
          </SimpleGrid>
          {personalised && recQuery.hasNextPage && (
            <Group justify="center" mt="md">
              <Button
                variant="default"
                leftSection={<IconPlus size={16} />}
                loading={recQuery.isFetchingNextPage}
                onClick={() => recQuery.fetchNextPage()}
              >
                Show more
              </Button>
            </Group>
          )}
        </>
      )}
    </Modal>
  )
}

/**
 * Catalogue browse: Popular / New / Trending / … rails, independent of the library. The search box
 * takes over the tab while it has a query: rails are for wandering, search is for looking.
 *
 * Everything below the rails now lives in `CatalogueBrowser`, shared with the Add series page and
 * the creator page. Discover keeps its curated rails by handing them over as the idle state; the
 * pages that have no rails browse the filtered catalogue there instead.
 */
function DiscoverBrowseTab({
  refreshNonce,
  onRefresh,
  density,
}: {
  /** Bumped by the page header's refresh action; busts the server-side rail cache. */
  refreshNonce: number
  onRefresh: () => void
  /** The page's Compact / Default / Comfortable, owned by the shell so its control can sit in the
      page header. Every card below sizes from it: the rails through CSS variables on the wrapper,
      the catalogue grid through the shared column counts. */
  density: DensityPref
}) {
  const { data: rails, isFetching, error } = useDiscover(refreshNonce)
  const { data: recentRail, isFetching: recentFetching } = useDiscoverRecentActivity(refreshNonce)
  const { data: sideInterests, isFetching: sideInterestsFetching } = useDiscoverSideInterests(refreshNonce)
  const { data: genreRails, isFetching: genresFetching } = useDiscoverGenres()
  const cohortRequest = useMemo(() => ({}), [])
  const { data: cohortRail, isFetching: cohortFetching } = useDiscoverCohort(cohortRequest)

  const { data: rootFolders } = useRootFolders()
  const navigate = useNavigate()
  const [detailItem, setDetailItem] = useState<RecommendationItem | null>(null)
  const [expandedRail, setExpandedRail] = useState<DiscoverRail | null>(null)
  const seriesIdFor = useSeriesIdLookup()
  const recommendFrom = useCallback(
    (item: RecommendationItem) =>
      navigate('/discover/recommended', {
        state: {
          recommendationFilters: {},
          seeds: [{ id: Number(item.providerId), title: item.title }],
          source: 'discover-hero',
        } satisfies RecommendationApplyState,
      }),
    [navigate],
  )

  // The band is synthesized from the picks the page already has: the per-seed rails first, since
  // they are the ones tuned to this reader, and the trending rail when there is no reading history
  // to seed with. There is no spotlight endpoint to ask instead.
  const heroItems = useMemo(() => {
    const fromSeeds = (recentRail?.items ?? []).slice(0, 6)
    if (fromSeeds.length >= 3) return fromSeeds
    const trending = rails?.find((r) => r.feed === 'Trending')?.items ?? []
    return [...fromSeeds, ...trending].slice(0, 6)
  }, [recentRail, rails])

  // Trending keeps a rail of its own; the rest of the catalogue feeds become one switchable grid.
  const trendingRail = rails?.find((r) => r.feed === 'Trending')
  const catalogueRails = useMemo(
    () => (rails ?? []).filter((r) => r.feed !== 'Trending'),
    [rails],
  )

  const body = (
    <div className="discover-browse-body discover-density" data-density={density.density}>
      {heroItems.length > 0 ? (
        <DiscoverHero items={heroItems} onOpen={setDetailItem} onRecommend={recommendFrom} />
      ) : ((isFetching && !rails) || (recentFetching && recentRail === undefined)) ? (
        <DiscoverHeroSkeleton />
      ) : null}

      <DiscoverTasteStrip />

      {recentRail ? (
        <div>
          <SectionHeader
            icon={IconLibrary}
            title={recentRail.title}
            count={recentRail.items.length}
            action={
              <Button
                variant="subtle"
                size="xs"
                rightSection={<IconChevronRight size={14} />}
                onClick={() => setExpandedRail(recentRail)}
              >
                Show more
              </Button>
            }
          />
          {/* The covers of the series this was built from, with the server's prose subtitle as the
              fallback for a reader whose seeds no longer resolve to library rows. */}
          {recentRail.seedIds && recentRail.seedIds.length > 0 ? (
            <DiscoverSeedStrip seedIds={recentRail.seedIds} />
          ) : (
            recentRail.subtitle && (
              <Text c="dimmed" size="sm" mb="sm">
                {recentRail.subtitle}
              </Text>
            )
          )}
          <EngineRailRow items={recentRail.items} seriesIdFor={seriesIdFor} onOpen={setDetailItem} />
        </div>
      ) : recentFetching ? (
        <DiscoverRailSkeleton engine />
      ) : null}

      {sideInterests?.map((rail) => (
        <div key={rail.key}>
          <SectionHeader
            icon={IconCompass}
            title={rail.title}
            count={rail.items.length}
            action={
              <Button
                variant="subtle"
                size="xs"
                rightSection={<IconChevronRight size={14} />}
                onClick={() => setExpandedRail(rail)}
              >
                Show more
              </Button>
            }
          />
          {rail.seedIds && rail.seedIds.length > 0 ? (
            <DiscoverSeedStrip seedIds={rail.seedIds} label="From your library" />
          ) : (
            <Text c="dimmed" size="sm" mb="sm">{rail.subtitle}</Text>
          )}
          <EngineRailRow items={rail.items} seriesIdFor={seriesIdFor} onOpen={setDetailItem} />
        </div>
      ))}
      {!sideInterests && sideInterestsFetching ? <DiscoverRailSkeleton engine /> : null}

      {cohortRail ? (
        <div>
          <SectionHeader
            icon={IconUsers}
            title={cohortRail.title}
            count={cohortRail.items.length}
            action={
              <Button
                variant="subtle"
                size="xs"
                rightSection={<IconChevronRight size={14} />}
                onClick={() => setExpandedRail(cohortRail)}
              >
                Show more
              </Button>
            }
          />
          {cohortRail.subtitle && (
            <Text c="dimmed" size="sm" mb="sm">
              {cohortRail.subtitle}
            </Text>
          )}
          {/* Not an engine rail, despite being personalised: cohort items hydrate straight from the
              MangaBaka dump, so they carry no `coRead`/`matchedTags`/`becauseOfTitle` and every
              card's footer would read "Similar feel". The heading is the only grounds there is. */}
          <DiscoverRailRow
            items={cohortRail.items}
            seriesIdFor={seriesIdFor}
            onOpen={setDetailItem}
          />
        </div>
      ) : cohortFetching ? (
        <DiscoverRailSkeleton />
      ) : null}

      {trendingRail ? (
        <div>
          <SectionHeader
            icon={IconFlame}
            title={trendingRail.title}
            count={trendingRail.items.length}
            action={
              <Button
                variant="subtle"
                size="xs"
                rightSection={<IconChevronRight size={14} />}
                onClick={() => setExpandedRail(trendingRail)}
              >
                Show more
              </Button>
            }
          />
          {/* Ranks are the point of a trending row, so the row is numbered. The counter lives on a
              Discover-only wrapper: `.discover-rail-item` is shared with five other surfaces. */}
          <div className="discover-ranked">
            <DiscoverRailRow
              items={trendingRail.items}
              seriesIdFor={seriesIdFor}
              onOpen={setDetailItem}
            />
          </div>
        </div>
      ) : isFetching && !rails ? (
        <DiscoverRailSkeleton />
      ) : null}

      {/* The rails, and whatever stands in for them. The failure and loading states live down here
          rather than at the top of the page: the hero and the personalised rows come from other
          endpoints and survive this one being down, so a bar above them mislabels the whole page as
          broken. */}
      <div className="discover-catalogue">
        <SectionHeader icon={IconCompass} title="Browse the catalogue" />
        {error ? (
          <Alert
            color="yellow"
            variant="light"
            icon={<IconAlertTriangle size={18} />}
            title="Catalogue unavailable"
          >
            <Stack gap="sm" align="flex-start">
              <Text size="sm">{String(error)}</Text>
              <Button
                size="xs"
                variant="default"
                leftSection={<IconRefresh size={14} />}
                loading={isFetching}
                onClick={onRefresh}
              >
                Try again
              </Button>
            </Stack>
          </Alert>
        ) : isFetching && !rails ? (
          <>
            <Text c="dimmed" size="sm" mb="sm">
              Scanning the MangaBaka catalogue…
            </Text>
            <DiscoverCatalogueSkeleton density={density.density} />
          </>
        ) : catalogueRails.length > 0 ? (
          <DiscoverCatalogue
            rails={catalogueRails}
            cols={density.cols}
            seriesIdFor={seriesIdFor}
            onOpen={setDetailItem}
            onShowMore={setExpandedRail}
          />
        ) : (
          <EmptyState
            icon={IconCompass}
            title="Nothing to browse yet"
            description="The catalogue rails need the local MangaBaka database (Settings → Metadata → local DB)."
          />
        )}
      </div>

      {genreRails && genreRails.length > 0 ? (
        <div>
          <SectionHeader icon={IconLayoutGrid} title="Every genre" count={genreRails.length} />
          <DiscoverGenreWall rails={genreRails} onOpen={setExpandedRail} />
        </div>
      ) : genresFetching ? (
        <DiscoverGenreSkeleton />
      ) : null}

      {expandedRail && (
        <FeedExpandModal
          rail={expandedRail}
          seriesIdFor={seriesIdFor}
          onOpenItem={setDetailItem}
          onClose={() => setExpandedRail(null)}
        />
      )}

      <DiscoverDetailModal
        item={detailItem}
        inLibrarySeriesId={detailItem ? seriesIdFor(detailItem) : null}
        rootFolders={rootFolders}
        onClose={() => setDetailItem(null)}
      />
    </div>
  )

  return (
    <CatalogueBrowser
      scope="discover"
      idle={body}
      placeholder={`Describe what you're after, a title, or author:"Junji Ito"`}
      hideSearch
    />
  )
}

type DiscoverTab = 'browse' | 'recommended' | 'taste'
const TAB_PATHS: Record<DiscoverTab, string> = {
  browse: '/discover',
  recommended: '/discover/recommended',
  taste: '/discover/taste',
}

/**
 * Discover shell: four URL-synced tabs - catalogue Browse (default), per-Genre, Recommended, and
 * the reader's own taste profile.
 */
export default function DiscoverPage() {
  const { tab } = useParams()
  const navigate = useNavigate()
  const active: DiscoverTab =
    tab === 'recommended'
      ? 'recommended'
      : tab === 'taste'
        ? 'taste'
        : 'browse'

  // The rails are cached for an hour on both sides, so the only way back to a fresh catalogue is
  // this. It lives up here rather than in the tab so it can sit in the page header, and reads the
  // same query the tab does: same key, so React Query serves it from cache and nothing extra is
  // requested.
  const [refreshNonce, setRefreshNonce] = useState(0)
  const { isFetching: railsFetching } = useDiscover(refreshNonce, active === 'browse')
  const refreshRails = useCallback(() => setRefreshNonce((n) => n + 1), [])

  // One density for the whole Discover tab, up here for the same reason as the refresh action: the
  // control belongs in the page header. Its own scope rather than `discover`, which the Recommended
  // tab already owns through `useViewPrefs` - two states over one key would go stale against each
  // other, since only one tab is mounted at a time.
  const browseDensity = useDensityPref('discover-browse')

  return (
    <>
      <SurfaceFrame pageStyle="editorial" className={`discover-surface discover-surface--${active}`}>
      <PageHeader
        className="discover-page-header"
        title="Discover"
        description="Browse the MangaBaka catalogue, or get personalised picks from your library's feel."
        actions={
          active === 'browse' ? (
            <Group gap="xs" wrap="nowrap">
              <DensityControl
                value={browseDensity.density}
                onChange={browseDensity.setDensity}
              />
              <Tooltip label="Refresh the catalogue" withArrow>
                <ActionIcon
                  variant="subtle"
                  color="gray"
                  size="lg"
                  loading={railsFetching}
                  onClick={refreshRails}
                  aria-label="Refresh the catalogue"
                >
                  <IconRefresh size={18} />
                </ActionIcon>
              </Tooltip>
            </Group>
          ) : undefined
        }
      />

      <Tabs
        className="discover-page-tabs"
        value={active}
        onChange={(v) => navigate(TAB_PATHS[(v as DiscoverTab) ?? 'browse'])}
        mb="md"
      >
        <Tabs.List>
          <Tabs.Tab value="browse" leftSection={<IconCompass size={16} />}>
            Discover
          </Tabs.Tab>
          <Tabs.Tab value="recommended" leftSection={<IconSparkles size={16} />}>
            Recommended
          </Tabs.Tab>
          <Tabs.Tab value="taste" leftSection={<IconHeartFilled size={16} />}>
            Your Taste
          </Tabs.Tab>
        </Tabs.List>
      </Tabs>

      {/* Keyed on the mode so the panel remounts and its enter animation restarts. The children
          already swap component type between modes; this just makes the wrapper follow. */}
      <div key={active} className={`discover-tab-panel discover-tab-panel--${active}`}>
        {active === 'recommended' ? (
          <RecommendedTab />
        ) : active === 'taste' ? (
          <TasteTab />
        ) : (
          <DiscoverBrowseTab
            refreshNonce={refreshNonce}
            onRefresh={refreshRails}
            density={browseDensity}
          />
        )}
      </div>
      </SurfaceFrame>
    </>
  )
}
