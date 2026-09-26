// Loaded in the shell rather than the tab so it lands once, whichever tab opens first.
import '@mantine/charts/styles.css'
import { Fragment, useCallback, useEffect, useMemo, useState } from 'react'
import { useLocation, useNavigate, useParams } from 'react-router-dom'
import {
  ActionIcon,
  Alert,
  Button,
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
  IconLayoutDashboard,
  IconLayoutGrid,
  IconLibrary,
  IconPlus,
  IconRefresh,
  IconSparkles,
  IconUsers,
} from '@tabler/icons-react'
import { useIsFetching } from '@tanstack/react-query'
import { useDebouncedValue } from '@mantine/hooks'
import { Trans, Plural, useLingui } from '@lingui/react/macro'
import { plural, t as now } from '@lingui/core/macro'
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
  useRootFolders,
  useSaveRecommendationDefaults,
  useSeries,
  useSeriesIdLookup,
  useUiSettings,
  isRailKey,
  railIdOf,
  type DiscoverRail,
  type DiscoverSectionKey,
  type RecommendationDefaults,
  type RecommendationFilters,
  type RecommendationItem,
  type RecommendationRequest,
  type RecommendationApplyState,
} from '../api/hooks'
import { useAuth } from '../auth/AuthProvider'
import { useLabel } from '../i18n-context'
import {
  CatalogueFilterActions,
  CatalogueFilters,
  CHAPTER_MAX,
  CHAPTER_MIN,
  filtersFromSpec,
  normalizeStoredYears,
  useStatusOptions,
  useTypeOptions,
  useCatalogueFilters,
  YEAR_MAX,
  YEAR_MIN,
} from '../components/CatalogueFilters'
import { DiscoverCatalogue } from '../components/discover/DiscoverCatalogue'
import { DiscoverGenreWall } from '../components/discover/DiscoverGenreWall'
import { DiscoverHero } from '../components/discover/DiscoverHero'
import { RecommenderDials } from '../components/discover/RecommenderDials'
import { DiscoverSeedStrip } from '../components/discover/DiscoverSeedStrip'
import { DiscoverTasteStrip } from '../components/discover/DiscoverTasteStrip'
import { DiscoverDetailModal } from '../components/discover/DiscoverDetailModal'
import { LuckyButton } from '../components/LuckyButton'
import { pickRandom } from '../lib/lucky'
import {
  DiscoverRailRow,
  EngineCard,
  EngineRailRow,
  RecommendationCard,
  RecommendationRow,
} from '../components/ui/DiscoverRail'
import { CatalogueBrowser, PosterSkeletons as SharedPosterSkeletons } from '../components/CatalogueBrowser'
import { FilterMatchCount, TermFilters, useRuleChips, useTermFilters } from '../components/CatalogueRules'
import { HiddenContentButton, PresetMenu } from '../components/DiscoverPresets'
import { AddRailButton, CustomRailSection } from '../components/rails/CustomRailSection'
import { PageLayoutEditor, PageLayoutEditorLoading } from '../components/layout/PageLayoutEditor'
import { useLayoutEditMode } from '../components/layout/useLayoutEditMode'
import { reconcileLayout } from '../components/layout/pageLayout'
import { DISCOVER_LAYOUT_CONFIG, DISCOVER_SECTION_DEFS } from '../components/discover/discoverSectionDefs'
import { CUSTOM_RAIL_PREFIX, customRailAsDiscoverRail, useCustomRails } from '../api/customRails'
import { EmptyState } from '../components/ui/EmptyState'
import { PageHeader } from '../components/ui/PageHeader'
import { Panel } from '../components/ui/Panel'
import { RailSkeleton } from '../components/ui/RailSkeleton'
import { SurfaceFrame } from '../components/ui/SurfaceFrame'
import { TagChip, TagChips } from '../components/ui/TagChip'
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

function DiscoverCatalogueSkeleton({ density }: { density: Density }) {
  return (
    <div aria-hidden>
      <Group gap={24} wrap="wrap" mb="md" pb={10}>
        {[62, 84, 76, 68, 92].map((width) => (
          <Skeleton key={width} h={12} w={width} />
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
      <Skeleton h={18} w={130} mt="xl" mb="sm" />
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
  const renderLabel = useLabel()
  const { t, i18n } = useLingui()
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
  const [yearsStored, setYears] = usePageState<[number, number]>(`${MEM}:years`, [YEAR_MIN, YEAR_MAX])
  const years = normalizeStoredYears(yearsStored)
  const [types, setTypes] = usePageState<string[]>(`${MEM}:types`, [])
  const [statuses, setStatuses] = usePageState<string[]>(`${MEM}:statuses`, [])
  const terms = useTermFilters(`${MEM}:terms`)
  const { hydrate: hydrateTerms, reset: resetTerms } = terms
  const ruleChips = useRuleChips()
  const typeOptions = useTypeOptions()
  const statusOptions = useStatusOptions()
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
        label: renderLabel(CONTENT_RATING_LABELS[value]),
      })),
    [me?.maxContentRating, renderLabel, i18n.locale],
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
    return state?.source === 'taste-profile' || state?.source === 'discover-hero' ||
      state?.source === 'custom-rail'
      ? {
          filters: state.recommendationFilters,
          seeds: state.seeds,
          source: state.source,
          obscurity: state.obscurity ?? 0,
          diversity: state.diversity ?? 0,
        }
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
      hydrateTerms(carriedFilters)
      setChapters([carriedFilters.minChapters ?? CHAPTER_MIN, carriedFilters.maxChapters ?? CHAPTER_MAX])
      setMinRating((carriedFilters.minRating ?? 0) / 10)
      setContentRatings(carriedFilters.contentRatings ?? [])
      setObscurity(carried.obscurity)
      setDiversity(carried.diversity)
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
        obscurity: carried.obscurity !== 0 ? carried.obscurity : undefined,
        diversity: carried.diversity !== 0 ? carried.diversity : undefined,
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
    hydrateTerms(filtersFromSpec(d))
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
    setSeedIds, setLabelCache, setYears, setTypes, setStatuses, hydrateTerms, setChapters,
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
    Object.assign(filters, terms.build())
    if (chapters[0] > CHAPTER_MIN) filters.minChapters = chapters[0]
    if (chapters[1] < CHAPTER_MAX) filters.maxChapters = chapters[1]
    if (minRating > 0) filters.minRating = minRating * 10 // slider is 0–10, dump rating is 0–100
    if (contentRatings.length) filters.contentRatings = contentRatings
    return filters
  }

  /** A saved catalogue filter into the panel. Seeds and the two dials are not part of one. */
  const loadCatalogueFilters = (f: RecommendationFilters) => {
    setYears([f.yearMin ?? YEAR_MIN, f.yearMax ?? YEAR_MAX])
    setTypes(f.types ?? [])
    setStatuses(f.statuses ?? [])
    hydrateTerms(f)
    setChapters([f.minChapters ?? CHAPTER_MIN, f.maxChapters ?? CHAPTER_MAX])
    setMinRating((f.minRating ?? 0) / 10)
    setContentRatings(f.contentRatings ?? [])
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
          color: 'var(--ok)',
          message: isCustomized ? now`Saved as your default` : now`Default cleared`,
        }),
      onError: (err) => {
        const detail = err instanceof Error ? err.message : String(err)
        notifications.show({ color: 'var(--danger)', message: now`Failed to save default: ${detail}` })
      },
    })
  }

  const reset = () => {
    setSeedIds([])
    setYears([YEAR_MIN, YEAR_MAX])
    setTypes([])
    setStatuses([])
    resetTerms()
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
    terms.isCustomized ||
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
      chips.push(plural(seedIds.length, { one: '# seed', other: '# seeds' }))
    }
    if (years[0] > YEAR_MIN || years[1] < YEAR_MAX) chips.push(`${years[0]}–${years[1]}`)
    if (minRating > 0) chips.push(`★ ≥ ${minRating.toFixed(1)}`)
    if (chapters[0] > CHAPTER_MIN || chapters[1] < CHAPTER_MAX) {
      const chapterMinChip = chapters[0]
      const chapterMaxChip = chapters[1] >= CHAPTER_MAX ? `${CHAPTER_MAX}+` : chapters[1]
      chips.push(t`${chapterMinChip}–${chapterMaxChip} ch`)
    }
    if (obscurity !== 0) chips.push(obscurity > 0 ? t`hidden gems` : t`mainstream`)
    if (diversity !== 0) {
      const diversityChip = diversity.toFixed(2)
      chips.push(t`varied (${diversityChip})`)
    }
    chips.push(...ruleChips(terms.rules))
    for (const typeName of types) chips.push(typeName)
    for (const s of statuses) chips.push(s)
    for (const c of contentRatings) chips.push(renderLabel(CONTENT_RATING_LABELS[c] ?? c))
    return chips
  }, [
    seedIds, years, minRating, chapters, obscurity, diversity, terms.rules, ruleChips, types,
    statuses, contentRatings, renderLabel, i18n.locale, t,
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

  const luckyItems = useMemo(() => {
    const seen = new Set<string>()
    const pages = data?.pages ?? []
    return [...(pages[0]?.related ?? []), ...pages.flatMap((p) => p.similar)].filter((item) => {
      if (seen.has(item.providerId)) return false
      seen.add(item.providerId)
      return true
    })
  }, [data])
  const luckyPool = useMemo(
    () =>
      luckyItems.map((item) => ({
        key: item.providerId,
        title: item.title,
        coverUrl: item.thumbUrlHiDpi ?? item.thumbUrl ?? item.coverUrl,
      })),
    [luckyItems],
  )
  const rerollDetail = () => {
    const next = pickRandom(luckyItems, (item) => item.providerId === detailItem?.providerId)
    if (next) setDetailItem(next)
  }

  // Named locals for the panel's caption sentences below: Lingui names a placeholder after the
  // expression only when it is a plain identifier, so a member access or method call has to be
  // hoisted first or it extracts as an unlabelled {0}.
  const chapterRangeMin = chapters[0]
  const chapterRangeMax = chapters[1] >= CHAPTER_MAX ? `${CHAPTER_MAX}+` : chapters[1]
  const yearRangeMin = years[0]
  const yearRangeMax = years[1]
  const minRatingDisplay = minRating.toFixed(1)

  return (
    <>
      <Group justify="flex-end" mb="md">
        <ViewPrefsControls prefs={prefs} />
        <Button
          variant={isCustomized ? 'light' : 'default'}
          leftSection={<IconAdjustmentsHorizontal size={16} />}
          onClick={() => setCustomizeOpen((o) => !o)}
        >
          {isCustomized ? <Trans>Customized</Trans> : <Trans>Customize</Trans>}
        </Button>
        <LuckyButton
          candidates={luckyPool}
          onPick={(key) => setDetailItem(luckyItems.find((item) => item.providerId === key) ?? null)}
        />
        <Button
          variant="default"
          leftSection={<IconRefresh size={16} />}
          loading={isFetching}
          onClick={() => apply(true)}
        >
          <Trans>Refresh</Trans>
        </Button>
      </Group>

      <Collapse expanded={customizeOpen}>
        <Panel p="md" mb="md">
          <Stack gap="md">
            <MultiSelect
              label={t`Seed from`}
              description={t`Base recommendations on these titles. Search adds any title from MangaBaka. Empty = your whole library.`}
              placeholder={seedIds.length ? undefined : t`Whole library`}
              data={seedOptions}
              value={seedIds}
              onChange={setSeedIds}
              searchable
              searchValue={seedSearch}
              onSearchChange={setSeedSearch}
              nothingFoundMessage={debouncedSearch.length > 1 ? t`No matches` : t`Type to search…`}
              clearable
              hidePickedOptions
              maxDropdownHeight={260}
            />

            <TermFilters controls={terms} />

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

            <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="lg">
              <div>
                <Text size="sm" fw={500} mb={4}>
                  <Trans>Chapters: {chapterRangeMin}–{chapterRangeMax}</Trans>
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
                  <Trans>Year: {yearRangeMin}–{yearRangeMax}</Trans>
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
                  {minRating > 0 ? (
                    <Trans>Minimum rating: ★ {minRatingDisplay}</Trans>
                  ) : (
                    <Trans>Minimum rating: any</Trans>
                  )}
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
              <RecommenderDials
                obscurity={obscurity}
                setObscurity={setObscurity}
                diversity={diversity}
                setDiversity={setDiversity}
              />
            </SimpleGrid>

            <Group justify="space-between">
              <Group gap="xs">
                <PresetMenu
                  current={currentFilters}
                  onLoad={loadCatalogueFilters}
                  railDraft={() => ({
                    source: 'recommendations',
                    filters: currentFilters(),
                    seeds: seedIds.map((id) => ({ id: Number(id), title: labelCache[id] ?? null })),
                    obscurity,
                    diversity,
                  })}
                />
                <HiddenContentButton />
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
                      ? t`Open Recommended with these filters from now on`
                      : t`Clear your saved default`
                  }
                >
                  {isCustomized ? <Trans>Save as default</Trans> : <Trans>Clear default</Trans>}
                </Button>
              </Group>
              <Group gap="xs">
                <Button variant="subtle" size="xs" onClick={reset} disabled={!isCustomized}>
                  <Trans>Reset</Trans>
                </Button>
                <Button size="xs" onClick={() => apply(false)}>
                  <Trans>Apply</Trans>
                </Button>
              </Group>
            </Group>
          </Stack>
        </Panel>
      </Collapse>

      {isCustomized && !customizeOpen && (
        <TagChips style={{ marginBottom: 'var(--mantine-spacing-md)' }}>
          {activeFilterChips.map((chip) => (
            <TagChip key={chip}>{chip}</TagChip>
          ))}
        </TagChips>
      )}

      {error && (
        <Alert color="var(--warn)" variant="light">
          {String(error)}
        </Alert>
      )}
      {isFetching && !data && (
        <>
          <Text c="var(--ink-3)" size="sm" mb="sm">
            <Trans>Scanning the MangaBaka database for matches…</Trans>
          </Text>
          <PosterSkeletons density={density} viewMode={viewMode} />
        </>
      )}

      {data && related.length === 0 && similar.length === 0 && (
        <EmptyState
          title={isCustomized ? t`No matches` : t`Nothing to recommend yet`}
          description={
            isCustomized
              ? t`No matches for these seeds and filters. Try loosening them.`
              : t`Add some series to your library first and Maki will suggest more like them.`
          }
          actionLabel={isCustomized ? undefined : t`Go to library`}
          actionTo={isCustomized ? undefined : '/library'}
        />
      )}

      {similar.length > 0 && (
        <>
          <SectionHeader
            icon={IconSparkles}
            title={seedIds.length > 0 ? t`Feels like your seeds` : t`Because of what you collect`}
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
                <Trans>Show more</Trans>
              </Button>
            </Group>
          )}
        </>
      )}

      {related.length > 0 && (
        <>
          <SectionHeader
            icon={IconAffiliate}
            title={seedIds.length > 0 ? t`Related to your seeds` : t`Related to your library`}
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
        </>
      )}

      <DiscoverDetailModal
        item={detailItem}
        feedbackContext={{ surface: 'recommended' }}
        inLibrarySeriesId={detailItem ? seriesIdFor(detailItem) : null}
        rootFolders={rootFolders}
        onClose={() => setDetailItem(null)}
        onReroll={luckyItems.length > 1 ? rerollDetail : undefined}
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
  const { t } = useLingui()
  const catalogue = useCatalogueFilters()
  const [applied, setApplied] = useState<RecommendationFilters>({})
  // Its own scope: the rails behind it are fixed-size rows, so this density is nobody else's.
  const { density, setDensity, cols } = useDensityPref('discover-expand')

  // Reset filters whenever a different rail is opened. A custom rail opens with its filter loaded
  // instead, since the filter is the whole point of the rail.
  const railKey = rail?.key
  const presetFilters = railKey?.startsWith(CUSTOM_RAIL_PREFIX) ? rail?.filters : null
  const resetAll = catalogue.reset
  const hydrateAll = catalogue.hydrate
  useEffect(() => {
    if (presetFilters) {
      hydrateAll(presetFilters)
      setApplied(presetFilters)
    } else {
      resetAll()
      setApplied({})
    }
  }, [railKey, presetFilters, resetAll, hydrateAll])

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
      ? {
          feed: rail.feed,
          genre: rail.genre,
          filters: applied,
          limit: 120,
          sort: rail.sort,
          excludeOwned: rail.excludeOwned,
        }
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
      <Panel p="md" mb="md">
        <Stack gap="md">
          <CatalogueFilters controls={catalogue.controls} />
          <CatalogueFilterActions
            isCustomized={catalogue.isCustomized || Object.keys(applied).length > 0}
            onReset={() => {
              catalogue.reset()
              setApplied({})
            }}
            onApply={() => setApplied(catalogue.build())}
            extra={
              <>
                <PresetMenu
                  current={catalogue.build}
                  onLoad={(f) => {
                    catalogue.hydrate(f)
                    setApplied(f)
                  }}
                />
                <HiddenContentButton />
                {!personalised && !cohort && rail && (
                  <FilterMatchCount filters={catalogue.build()} feed={rail.feed} genre={rail.genre} />
                )}
              </>
            }
          />
        </Stack>
      </Panel>

      {error && (
        <Alert color="var(--warn)" variant="light">
          {String(error)}
        </Alert>
      )}

      {isFetching && !items && <PosterSkeletons density={density} />}

      {items && items.length === 0 && (
        <EmptyState
          title={t`No matches`}
          description={t`No titles match these filters. Try loosening them.`}
        />
      )}

      {items && items.length > 0 && (
        <>
          <Group justify="space-between" mb="sm">
            <Text c="var(--ink-3)" size="sm">
              <Plural value={items.length} one="# title" other="# titles" />
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
                <Trans>Show more</Trans>
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
  editing,
  onExitEditing,
}: {
  /** Bumped by the page header's refresh action; busts the server-side rail cache. */
  refreshNonce: number
  onRefresh: () => void
  /** The page's Compact / Default / Comfortable, owned by the shell so its control can sit in the
      page header. Every card below sizes from it: the rails through CSS variables on the wrapper,
      the catalogue grid through the shared column counts. */
  density: DensityPref
  /** Layout edit mode: the sections become compact cards, and nothing below fetches. */
  editing: boolean
  onExitEditing: () => void
}) {
  const { t } = useLingui()
  const { data: ui } = useUiSettings()
  const { data: customRails } = useCustomRails()
  const discoverRails = useMemo(
    () => customRails?.filter((r) => r.placement === 'discover'),
    [customRails],
  )

  // Shipping order while the setting loads, so the page doesn't reflow once it arrives.
  const layout = useMemo(
    () =>
      ui?.discoverLayout?.sections ??
      reconcileLayout([], DISCOVER_LAYOUT_CONFIG, (discoverRails ?? []).map((r) => r.id)),
    [ui, discoverRails],
  )
  const on = (key: DiscoverSectionKey) =>
    !editing && (layout.find((s) => s.key === key)?.enabled ?? false)

  // Every query is gated on the section that shows it, so a switched-off section costs nothing.
  // The hero is the exception that proves it: it borrows the recent-activity picks, and Trending
  // when those run short, so it keeps both of those alive on its own.
  const recent = useDiscoverRecentActivity(refreshNonce, on('recent') || on('hero'))
  const { data: recentRail, isFetching: recentFetching } = recent
  const heroNeedsTrending = on('hero') && !recentFetching && (recentRail?.items.length ?? 0) < 3
  const { data: rails, isFetching, error } = useDiscover(
    refreshNonce,
    on('trending') || on('catalogue') || heroNeedsTrending,
  )
  const { data: sideInterests, isFetching: sideInterestsFetching } = useDiscoverSideInterests(
    refreshNonce,
    on('sideinterests'),
  )
  const { data: genreRails, isFetching: genresFetching } = useDiscoverGenres(0, on('genres'))
  const cohortRequest = useMemo(() => ({}), [])
  const { data: cohortRail, isFetching: cohortFetching } = useDiscoverCohort(cohortRequest, on('cohort'))

  const { data: rootFolders } = useRootFolders()
  const navigate = useNavigate()
  const [detailItem, setDetailItem] = useState<RecommendationItem | null>(null)
  const [expandedRail, setExpandedRail] = useState<DiscoverRail | null>(null)
  const seriesIdFor = useSeriesIdLookup()

  // A catalogue rail on Home expands here, since this is where the expand view lives.
  const location = useLocation()
  const expandRailId = (location.state as { expandRailId?: number } | null)?.expandRailId
  useEffect(() => {
    if (expandRailId == null || !customRails) return
    const rail = customRails.find((r) => r.id === expandRailId)
    if (rail) setExpandedRail(customRailAsDiscoverRail(rail, []))
    navigate(location.pathname, { replace: true, state: null })
  }, [expandRailId, customRails, navigate, location.pathname])

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

  if (editing) {
    return ui && customRails ? (
      <PageLayoutEditor
        page="discover"
        placement="discover"
        registry={DISCOVER_SECTION_DEFS}
        config={DISCOVER_LAYOUT_CONFIG}
        initial={layout}
        rails={discoverRails}
        railLimit={40}
        summary={(key) =>
          key === 'sideinterests' && sideInterests
            ? t`${sideInterests.length} rows right now`
            : null
        }
        preview={(key) => {
          const items =
            key === 'hero'
              ? heroItems
              : key === 'recent'
                ? recentRail?.items
                : key === 'cohort'
                  ? cohortRail?.items
                  : key === 'trending'
                    ? trendingRail?.items
                    : key === 'catalogue'
                      ? catalogueRails[0]?.items
                      : key === 'sideinterests'
                        ? sideInterests?.[0]?.items
                        : undefined
          return (items ?? []).map((i) => i.thumbUrl ?? i.coverUrl)
        }}
        onExit={onExitEditing}
      />
    ) : (
      <PageLayoutEditorLoading />
    )
  }

  // The catalogue query's failure and loading states. They live with the catalogue section rather
  // than at the top of the page, since the hero and the personalised rows come from other endpoints
  // and survive this one being down; with that section off they move to Trending instead.
  const catalogueStatus = error ? (
    <Alert
      color="var(--warn)"
      variant="light"
      icon={<IconAlertTriangle size={18} />}
      title={t`Catalogue unavailable`}
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
          <Trans>Try again</Trans>
        </Button>
      </Stack>
    </Alert>
  ) : null

  const sections: Record<DiscoverSectionKey, React.ReactNode> = {
    hero:
      heroItems.length > 0 ? (
        <DiscoverHero items={heroItems} onOpen={setDetailItem} onRecommend={recommendFrom} />
      ) : (isFetching && !rails) || (recentFetching && recentRail === undefined) ? (
        <DiscoverHeroSkeleton />
      ) : null,

    taste: <DiscoverTasteStrip />,

    recent: recentRail ? (
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
              <Trans>Show more</Trans>
            </Button>
          }
        />
        {/* The covers of the series this was built from, with the server's prose subtitle as the
            fallback for a reader whose seeds no longer resolve to library rows. */}
        {recentRail.seedIds && recentRail.seedIds.length > 0 ? (
          <DiscoverSeedStrip seedIds={recentRail.seedIds} />
        ) : (
          recentRail.subtitle && (
            <Text c="var(--ink-3)" size="sm" mb="sm">
              {recentRail.subtitle}
            </Text>
          )
        )}
        <EngineRailRow items={recentRail.items} seriesIdFor={seriesIdFor} onOpen={setDetailItem} />
      </div>
    ) : recentFetching ? (
      <RailSkeleton engine title />
    ) : null,

    sideinterests: (
      <>
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
                  <Trans>Show more</Trans>
                </Button>
              }
            />
            {rail.seedIds && rail.seedIds.length > 0 ? (
              <DiscoverSeedStrip seedIds={rail.seedIds} label={t`From your library`} />
            ) : (
              <Text c="var(--ink-3)" size="sm" mb="sm">{rail.subtitle}</Text>
            )}
            <EngineRailRow items={rail.items} seriesIdFor={seriesIdFor} onOpen={setDetailItem} />
          </div>
        ))}
        {!sideInterests && sideInterestsFetching ? <RailSkeleton engine title /> : null}
      </>
    ),

    cohort: cohortRail ? (
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
              <Trans>Show more</Trans>
            </Button>
          }
        />
        {cohortRail.subtitle && (
          <Text c="var(--ink-3)" size="sm" mb="sm">
            {cohortRail.subtitle}
          </Text>
        )}
        {/* Not an engine rail, despite being personalised: cohort items hydrate straight from the
            MangaBaka dump, so they carry no `coRead`/`matchedTags`/`becauseOfTitle` and every
            card's footer would read "Similar feel". The heading is the only grounds there is. */}
        <DiscoverRailRow items={cohortRail.items} seriesIdFor={seriesIdFor} onOpen={setDetailItem} />
      </div>
    ) : cohortFetching ? (
      <RailSkeleton title />
    ) : null,

    trending: (
      <>
        {!on('catalogue') && catalogueStatus}
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
                  <Trans>Show more</Trans>
                </Button>
              }
            />
            {/* Ranks are the point of a trending row, so the row is numbered. The counter lives on a
                Discover-only wrapper: `.discover-rail-item` is shared with five other surfaces. */}
            <div className="discover-ranked">
              <DiscoverRailRow items={trendingRail.items} seriesIdFor={seriesIdFor} onOpen={setDetailItem} />
            </div>
          </div>
        ) : isFetching && !rails ? (
          <RailSkeleton title />
        ) : null}
      </>
    ),

    catalogue: (
      <div>
        <SectionHeader
          icon={IconCompass}
          title={t`Browse the catalogue`}
          action={
            <Button
              variant="subtle"
              size="xs"
              leftSection={<IconAdjustmentsHorizontal size={14} />}
              onClick={() =>
                setExpandedRail({
                  key: 'catalogue',
                  title: t`The whole catalogue`,
                  feed: 'Popular',
                  genre: null,
                  items: [],
                })
              }
            >
              <Trans>Filter the catalogue</Trans>
            </Button>
          }
        />
        {catalogueStatus ??
          (isFetching && !rails ? (
            <>
              <Text c="var(--ink-3)" size="sm" mb="sm">
                <Trans>Scanning the MangaBaka catalogue…</Trans>
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
              title={t`Nothing to browse yet`}
              description={t`The catalogue rails need the local MangaBaka database (Settings → Metadata → local DB).`}
            />
          ))}
      </div>
    ),

    genres:
      genreRails && genreRails.length > 0 ? (
        <div>
          <SectionHeader icon={IconLayoutGrid} title={t`Every genre`} count={genreRails.length} />
          <DiscoverGenreWall rails={genreRails} onOpen={setExpandedRail} />
        </div>
      ) : genresFetching ? (
        <DiscoverGenreSkeleton />
      ) : null,
  }

  const renderSection = (key: string) => {
    if (!isRailKey(key)) return sections[key as DiscoverSectionKey]
    const rail = discoverRails?.find((r) => r.id === railIdOf(key))
    return rail ? (
      <CustomRailSection rail={rail} limit={40} onOpen={setDetailItem} onShowMoreCatalogue={setExpandedRail} />
    ) : null
  }

  const body = (
    <div className="discover-density" data-density={density.density}>
      {layout.filter((s) => s.enabled).map((s) => (
        <Fragment key={s.key}>{renderSection(s.key)}</Fragment>
      ))}

      <Group justify="center" mt="xl">
        <AddRailButton placement="discover" />
      </Group>

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
        feedbackContext={{ surface: 'browse' }}
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
      placeholder={t`Describe what you're after, a title, or author:"Junji Ito"`}
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
  const { t } = useLingui()
  const { tab } = useParams()
  const navigate = useNavigate()
  const active: DiscoverTab =
    tab === 'recommended'
      ? 'recommended'
      : tab === 'taste'
        ? 'taste'
        : 'browse'

  // The rails are cached for an hour on both sides, so the only way back to a fresh catalogue is
  // this. It lives up here rather than in the tab so it can sit in the page header. The spinner
  // watches the tab's query rather than running its own, which would fetch the catalogue even with
  // every section that shows it switched off.
  const [refreshNonce, setRefreshNonce] = useState(0)
  const railsFetching = useIsFetching({ queryKey: ['discover-rails'] }) > 0
  const refreshRails = useCallback(() => setRefreshNonce((n) => n + 1), [])
  const { editing, enter: enterEditing, exit: exitEditing } = useLayoutEditMode(active === 'browse')

  // One density for the whole Discover tab, up here for the same reason as the refresh action: the
  // control belongs in the page header. Its own scope rather than `discover`, which the Recommended
  // tab already owns through `useViewPrefs` - two states over one key would go stale against each
  // other, since only one tab is mounted at a time.
  const browseDensity = useDensityPref('discover-browse')

  return (
    <SurfaceFrame width="full" pageStyle="editorial">
      <PageHeader
        title={t`Discover`}
        description={t`Browse the MangaBaka catalogue, or get personalised picks from your library's feel.`}
        actions={
          active === 'browse' && !editing ? (
            <Group gap="xs" wrap="wrap">
              <Button variant="default" leftSection={<IconLayoutDashboard size={16} />} onClick={enterEditing}>
                <Trans>Edit layout</Trans>
              </Button>
              <DensityControl
                value={browseDensity.density}
                onChange={browseDensity.setDensity}
              />
              <Tooltip label={t`Refresh the catalogue`} withArrow>
                <ActionIcon
                  variant="subtle"
                  color="var(--neutral)"
                  size="lg"
                  loading={railsFetching}
                  onClick={refreshRails}
                  aria-label={t`Refresh the catalogue`}
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
        variant="unstyled"
        classNames={{ list: 'series-tabs page-tabs', tab: 'series-tab' }}
      >
        <Tabs.List>
          <Tabs.Tab value="browse">
            <Trans>Discover</Trans>
          </Tabs.Tab>
          <Tabs.Tab value="recommended" disabled={editing}>
            <Trans>Recommended</Trans>
          </Tabs.Tab>
          <Tabs.Tab value="taste" disabled={editing}>
            <Trans>Your Taste</Trans>
          </Tabs.Tab>
        </Tabs.List>
      </Tabs>

      {active === 'recommended' ? (
        <RecommendedTab />
      ) : active === 'taste' ? (
        <TasteTab />
      ) : (
        <DiscoverBrowseTab
          refreshNonce={refreshNonce}
          onRefresh={refreshRails}
          density={browseDensity}
          editing={editing}
          onExitEditing={exitEditing}
        />
      )}
    </SurfaceFrame>
  )
}
