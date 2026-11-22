// Loaded in the shell rather than the tab so it lands once, whichever tab opens first.
import { errorText } from '../api/errorText'
import '@mantine/charts/styles.css'
import { Suspense, lazy, useCallback, useEffect, useId, useMemo, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { useLocation, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import {
  ActionIcon,
  Button,
  Group,
  Menu,
  Modal,
  SimpleGrid,
  Skeleton,
  Stack,
  Tabs,
  Text,
  Tooltip,
  useMatches,
} from '@mantine/core'
import {
  IconAdjustmentsHorizontal,
  IconAffiliate,
  IconAlertTriangle,
  IconCompass,
  IconFlame,
  IconLayoutDashboard,
  IconLayoutGrid,
  IconLibrary,
  IconPlant,
  IconPlus,
  IconRefresh,
  IconUsers,
} from '../lib/icons'
import { useIsFetching } from '@tanstack/react-query'
import type { MessageDescriptor } from '@lingui/core'
import { Trans, Plural, useLingui } from '@lingui/react/macro'
import { msg, t as now } from '@lingui/core/macro'
import { formatNumber } from '../format'
import { useLabel } from '../i18n-context'
import { pageScroller } from '../lib/pageScroll'
import { onRovingKeyDown } from '../lib/rovingFocus'
import { notifications } from '../lib/toast'
import {
  useDiscover,
  useDiscoverFeed,
  useDiscoverGenres,
  useDiscoverCohort,
  useDiscoverRecentActivity,
  useDiscoverSideInterests,
  READER_COHORT_FEED,
  useRecommendationDefaults,
  useRecommendations,
  useRootFolders,
  useSaveRecommendationDefaults,
  useSeries,
  useSeriesIdLookup,
  useUiSettings,
  isRailKey,
  railIdOf,
  type CatalogueCredit,
  type DiscoverRail,
  type DiscoverSectionKey,
  type RecommendationDefaults,
  type RecommendationFilters,
  type RecommendationItem,
  recommendationStub,
  type RecommendationRequest,
  type RecommendationApplyState,
} from '../api/hooks'
import { useLibraryCoverLookup } from '../api/seriesLookup'
import {
  CHAPTER_MAX,
  CHAPTER_MIN,
  filtersFromSpec,
  normalizeFilters,
  normalizeStoredYears,
  useCatalogueFilters,
  useLiveApply,
  YEAR_MAX,
  YEAR_MIN,
} from '../components/CatalogueFilters'
import {
  CatalogueFilterBar,
  SeedPicker,
  SeedThumb,
  useCatalogueFilterSlots,
  type FilterSlot,
} from '../components/CatalogueFilterBar'
import { DiscoverCatalogue } from '../components/discover/DiscoverCatalogue'
import { DiscoverGenreWall } from '../components/discover/DiscoverGenreWall'
import { DiscoverHero } from '../components/discover/DiscoverHero'
import { RecommenderDials } from '../components/discover/RecommenderDials'
import { DiscoverSeedStrip } from '../components/discover/DiscoverSeedStrip'
import { DiscoverDetailModal } from '../components/discover/DiscoverDetailModal'
import { FollowingRail } from '../components/discover/FollowingRail'
import { LuckyButton } from '../components/LuckyButton'
import { pickRandom } from '../lib/lucky'
import { railSubtitle } from '../lib/railSubtitle'
import {
  DiscoverRailRow,
  EngineCard,
  EngineRailRow,
  engineReason,
  useSeedCover,
  RecommendationRow,
  type OpenItem,
} from '../components/ui/DiscoverRail'
import { type CatalogueSeed, PosterSkeletons as SharedPosterSkeletons, Results } from '../components/CatalogueBrowser'
import { FilterMatchCount, useTermFilters } from '../components/CatalogueRules'
import { PresetMenu } from '../components/DiscoverPresets'
import { AddRailButton, CustomRailSection } from '../components/rails/CustomRailSection'
import { LayoutEditLoading, LayoutEditSurface } from '../components/layout/LayoutEditSurface'
import { useLayoutDraft } from '../components/layout/useLayoutDraft'
import { useLayoutEditMode } from '../components/layout/useLayoutEditMode'
import { reconcileLayout } from '../components/layout/pageLayout'
import { DISCOVER_LAYOUT_CONFIG, DISCOVER_SECTION_DEFS } from '../components/discover/discoverSectionDefs'
import { CUSTOM_RAIL_PREFIX, customRailAsDiscoverRail, useCustomRails } from '../api/customRails'
import { FOLLOWING_RAIL_KEY } from '../api/following'
import { OTHER_SOURCE, railSource, sectionSource, useShortlistCount, type ShortlistSource } from '../api/shortlist'
import { CountIndicator } from '../components/ui/CountIndicator'
import { ModalHeading } from '../components/ui/ModalShell'
import { EmptyState } from '../components/ui/EmptyState'
import { PageHeader } from '../components/ui/PageHeader'
import { RailSkeleton } from '../components/ui/RailSkeleton'
import { DelayedLoader } from '../components/ui/DelayedLoader'
import { SurfaceFrame } from '../components/ui/SurfaceFrame'
import { usePageState } from '../lib/pageState'
import { SectionHeader } from '../components/ui/SectionHeader'
import {
  DensityMenuItems,
  POSTER_COLS_BY_DENSITY,
  ToolbarMenuButton,
  ViewModeToggle,
  readStored,
  ViewPrefsControls,
  writeStored,
  useViewPrefs,
  type ViewPrefs,
  type Density,
  type DensityPref,
} from '../components/ui/viewPrefs'
import { useModalHistory } from '../lib/useModalHistory'
import { Callout } from '../components/ui/Callout'

const DENSITY_VALUES: readonly Density[] = ['compact', 'default', 'comfortable']

const RELATION_KIND_ORDER = ['Sequel', 'Prequel', 'Spin-off', 'Side story', 'Main story'] as const
const RELATION_KIND_LABELS: Record<string, MessageDescriptor> = {
  Sequel: msg`Sequels`,
  Prequel: msg`Prequels`,
  'Spin-off': msg`Spin-offs`,
  'Side story': msg`Side stories`,
  'Main story': msg`Main stories`,
}
const OTHER_RELATION_LABEL = msg`Other`

type RecommendedSection = 'similar' | 'related'

function migrateBrowseDensity() {
  try {
    if (localStorage.getItem('discover-density') !== null) return
    const old = readStored('discover-browse-density', DENSITY_VALUES, 'default')
    if (localStorage.getItem('discover-browse-density') === null) return
    writeStored('discover-density', old)
    localStorage.removeItem('discover-browse-density')
  } catch {
    /* storage unavailable: nothing to carry over */
  }
}

/**
 * Where the Recommended panel is remembered between visits. Its own key rather than the route,
 * because the tab is reached at one path and nothing else on Discover shares its controls.
 */
const MEM = 'discover-recommended'

/** Longer than the catalogue's 400 ms: a recommendation rebuild costs seconds on a whole library. */
const LIVE_APPLY_MS = 600

/** JSON with object keys sorted at every level, so two builds of one request compare equal. */
function canonicalJson(value: unknown): string {
  return JSON.stringify(value, (_key, v: unknown) =>
    v && typeof v === 'object' && !Array.isArray(v)
      ? Object.fromEntries(Object.entries(v as Record<string, unknown>).sort(([a], [b]) => a.localeCompare(b)))
      : v,
  )
}

// Pulls in the charting library, which the Browse tab never needs.
const TasteTab = lazy(() => import('./discover/TasteTab').then((m) => ({ default: m.TasteTab })))
const ShortlistTab = lazy(() => import('./discover/ShortlistTab').then((m) => ({ default: m.ShortlistTab })))

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

function NextPageSkeleton({
  density,
  viewMode,
}: {
  density: Density
  viewMode: 'grid' | 'list'
}) {
  const columns = useMatches(POSTER_COLS_BY_DENSITY[density])
  if (viewMode === 'list') {
    const thumbSize = density === 'compact' ? 48 : density === 'comfortable' ? 72 : 56
    return (
      <Stack gap="xs" className="recommended-next-skeleton" aria-hidden>
        {Array.from({ length: 3 }, (_, i) => (
          <div key={i} className={`series-row ${density}`}>
            <Skeleton
              radius="var(--radius-thumb)"
              style={{ width: thumbSize, height: thumbSize * 1.5, flexShrink: 0 }}
            />
            <div className="row-body">
              <Skeleton h={14} w="42%" mb="sm" />
              <Skeleton h={10} w="68%" mb="sm" />
              <Skeleton h={10} w="24%" />
            </div>
          </div>
        ))}
      </Stack>
    )
  }
  return (
    <SimpleGrid cols={POSTER_COLS_BY_DENSITY[density]} spacing="md" className="recommended-next-skeleton" aria-hidden>
      {Array.from({ length: columns }, (_, i) => (
        <Skeleton key={i} radius="var(--radius-cover)" style={{ aspectRatio: '2 / 3' }} />
      ))}
    </SimpleGrid>
  )
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

/**
 * The recommendation engine: Maki's library-driven "more like what you own" picks. Its toolbar
 * portals into `toolsTarget`, a slot in the page header, rather than taking a row of its own.
 */
function RecommendedTab({ prefs, toolsTarget }: { prefs: ViewPrefs; toolsTarget: HTMLElement | null }) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const tabsId = useId()
  const { data: library } = useSeries()
  const coverFor = useLibraryCoverLookup()
  const { data: rootFolders } = useRootFolders()
  const { viewMode, density } = prefs
  const [section, setSection] = usePageState<RecommendedSection | null>(`${MEM}:section`, null)
  const [relationKind, setRelationKind] = useState('all')

  // --- customization controls ---
  // Remembered for the tab session (see usePageState): the bar is a dozen controls, and losing
  // them because you opened one of its own results and came back is losing real work.
  const [seedIds, setSeedIds] = usePageState<string[]>(`${MEM}:seeds`, [])
  const [yearsStored, setYears] = usePageState<[number, number]>(`${MEM}:years`, [YEAR_MIN, YEAR_MAX])
  const years = normalizeStoredYears(yearsStored)
  const [types, setTypes] = usePageState<string[]>(`${MEM}:types`, [])
  const [statuses, setStatuses] = usePageState<string[]>(`${MEM}:statuses`, [])
  const terms = useTermFilters(`${MEM}:terms`)
  const { hydrate: hydrateTerms, reset: resetTerms } = terms
  const [chapters, setChapters] = usePageState<[number, number]>(`${MEM}:chapters`, [CHAPTER_MIN, CHAPTER_MAX])
  const [minRating, setMinRating] = usePageState(`${MEM}:min-rating`, 0)
  const [obscurity, setObscurity] = usePageState(`${MEM}:obscurity`, 0)
  const [diversity, setDiversity] = usePageState(`${MEM}:diversity`, 0)
  const [contentRatings, setContentRatings] = usePageState<string[]>(`${MEM}:content-ratings`, [])
  const [credits, setCredits] = usePageState<CatalogueCredit[]>(`${MEM}:credits`, [])

  // MangaBaka id → title, accumulated from the library and every seed search so selected
  // seeds keep their labels even after the search box clears.
  // Remembered too, or restored seeds would come back as bare ids until the library query lands.
  const [labelCache, setLabelCache] = usePageState<Record<string, string>>(`${MEM}:seed-labels`, {})
  const [coverCache, setCoverCache] = usePageState<Record<string, string | null>>(`${MEM}:seed-covers`, {})
  useEffect(() => {
    setLabelCache((prev) => {
      const next = { ...prev }
      for (const s of library ?? []) {
        if (s.mangaBakaId != null) next[String(s.mangaBakaId)] = s.displayTitle
      }
      return next
    })
  }, [library, setLabelCache])

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
      const { filters: carriedFilters, seeds: carriedSeeds } = carried
      setYears([carriedFilters.yearMin ?? YEAR_MIN, carriedFilters.yearMax ?? YEAR_MAX])
      setTypes(carriedFilters.types ?? [])
      setStatuses(carriedFilters.statuses ?? [])
      hydrateTerms(carriedFilters)
      setChapters([carriedFilters.minChapters ?? CHAPTER_MIN, carriedFilters.maxChapters ?? CHAPTER_MAX])
      setMinRating((carriedFilters.minRating ?? 0) / 10)
      setContentRatings(carriedFilters.contentRatings ?? [])
      setCredits(carriedFilters.credits ?? [])
      setObscurity(carried.obscurity)
      setDiversity(carried.diversity)
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
        filters: Object.keys(carriedFilters).length ? normalizeFilters(carriedFilters) : undefined,
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
    setCredits([])

    const filters = normalizeFilters(filtersFromSpec(d))
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
    setMinRating, setObscurity, setDiversity, setContentRatings, setCredits, setApplied,
    setHydrated,
  ])

  const {
    data,
    isFetching,
    isPlaceholderData,
    error,
    fetchNextPage,
    hasNextPage,
    isFetchingNextPage,
    isFetchNextPageError,
  } = useRecommendations(applied, hydrated, true)
  // The headings describe the picks on screen, which trail the bar while a live change loads.
  const appliedSeeded = (applied.seedIds?.length ?? 0) > 0
  const appliedCustomized =
    appliedSeeded || applied.filters != null || applied.obscurity != null || applied.diversity != null
  const related = useMemo(() => data?.pages[0]?.related ?? [], [data])
  const similar = useMemo(() => data?.pages.flatMap((p) => p.similar) ?? [], [data])
  const similarCount = similar.length
  const totalSimilar = hasNextPage
    ? Math.max(data?.pages[0]?.totalSimilar ?? 0, similarCount)
    : similarCount
  const showSubTabs = related.length > 0
  const activeSection: RecommendedSection =
    section === 'related' && related.length === 0
      ? 'similar'
      : section != null
        ? section
        : !appliedCustomized && similarCount === 0 && related.length > 0
          ? 'related'
          : 'similar'
  const similarTabId = `${tabsId}-tab-similar`
  const relatedTabId = `${tabsId}-tab-related`
  const similarPanelId = `${tabsId}-panel-similar`
  const relatedPanelId = `${tabsId}-panel-related`

  const relationKinds = useMemo(() => {
    const counts = new Map<string, number>()
    for (const item of related) {
      const kind = item.relationKind ?? ''
      counts.set(kind, (counts.get(kind) ?? 0) + 1)
    }
    const ordered: { value: string; count: number }[] = []
    for (const kind of RELATION_KIND_ORDER) {
      const count = counts.get(kind)
      if (count) {
        ordered.push({ value: kind, count })
        counts.delete(kind)
      }
    }
    for (const [value, count] of counts) {
      ordered.push({ value, count })
    }
    return ordered
  }, [related])

  const activeRelationKind =
    relationKind !== 'all' && relationKinds.some((k) => k.value === relationKind) ? relationKind : 'all'
  const visibleRelated = useMemo(
    () =>
      activeRelationKind === 'all'
        ? related
        : related.filter((item) => (item.relationKind ?? '') === activeRelationKind),
    [related, activeRelationKind],
  )

  const [sentinel, setSentinel] = useState<HTMLDivElement | null>(null)
  useEffect(() => {
    if (
      activeSection !== 'similar' ||
      !sentinel ||
      !hasNextPage ||
      isFetching ||
      isFetchingNextPage ||
      isFetchNextPageError ||
      isPlaceholderData
    ) {
      return
    }
    const observer = new IntersectionObserver(
      (entries) => {
        if (entries.some((e) => e.isIntersecting)) {
          void fetchNextPage()
        }
      },
      { root: pageScroller(), rootMargin: '0px 0px 600px 0px' },
    )
    observer.observe(sentinel)
    return () => observer.disconnect()
  }, [
    activeSection,
    sentinel,
    hasNextPage,
    isFetching,
    isFetchingNextPage,
    isFetchNextPageError,
    isPlaceholderData,
    fetchNextPage,
    similarCount,
  ])

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
    if (credits.length) filters.credits = credits
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
    setCredits(f.credits ?? [])
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

  // Live apply: the request follows the bar a beat after it stops changing. The nonce is kept, so
  // stepping back to an earlier set of filters is a cache hit rather than a rebuild.
  const liveFilters = currentFilters()
  const liveKey = canonicalJson({
    seedIds: seedIds.length ? seedIds.map(Number) : undefined,
    filters: Object.keys(liveFilters).length ? liveFilters : undefined,
    obscurity: obscurity !== 0 ? obscurity : undefined,
    diversity: diversity !== 0 ? diversity : undefined,
  })
  useEffect(() => {
    if (!hydrated) return
    const id = window.setTimeout(() => {
      setApplied((prev) => {
        // eslint-disable-next-line @typescript-eslint/no-unused-vars
        const { nonce, refresh, ...identity } = prev
        return canonicalJson(identity) === liveKey ? prev : { ...JSON.parse(liveKey), nonce }
      })
    }, LIVE_APPLY_MS)
    return () => window.clearTimeout(id)
  }, [liveKey, hydrated, setApplied])

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
    setCredits([])
    setApplied((prev) => ({ nonce: prev.nonce }))
  }

  const hasCatalogueFilters =
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
    contentRatings.length > 0 ||
    credits.length > 0

  const isCustomized = seedIds.length > 0 || hasCatalogueFilters


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
  const seedCover = useSeedCover()

  const luckyItems = useMemo(() => {
    const source = activeSection === 'related' ? visibleRelated : similar
    const seen = new Set<string>()
    return source.filter((item) => {
      if (seen.has(item.providerId)) return false
      seen.add(item.providerId)
      return true
    })
  }, [activeSection, visibleRelated, similar])
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

  const catalogueSlots = useCatalogueFilterSlots({
    terms: { state: terms.state, setState: terms.setState },
    types, setTypes,
    statuses, setStatuses,
    years, setYears,
    chapters, setChapters,
    minRating, setMinRating,
    contentRatings, setContentRatings,
    credits, setCredits,
  }, { creators: true })
  const firstSeed = seedIds[0]
  const firstSeedTitle = firstSeed == null ? null : (labelCache[firstSeed] ?? firstSeed)
  const firstSeedCover = firstSeed == null ? null : (coverFor(firstSeed) ?? coverCache[firstSeed] ?? null)
  const extraSeeds = seedIds.length - 1
  const tuningParts = [
    obscurity > 0 ? t`Hidden gems` : obscurity < 0 ? t`Mainstream` : null,
    diversity > 0 ? t`Varied` : null,
  ].filter((part): part is string => part != null)
  const filterSlots: FilterSlot[] = [
    {
      id: 'seeds',
      icon: IconPlant,
      label: t`Seeds`,
      value:
        firstSeedTitle == null ? null : extraSeeds > 0 ? t`${firstSeedTitle} +${extraSeeds}` : firstSeedTitle,
      thumb: firstSeedCover ? <SeedThumb url={firstSeedCover} /> : undefined,
      width: 360,
      onClear: () => setSeedIds([]),
      editor: (
        <SeedPicker
          value={seedIds}
          labels={labelCache}
          covers={coverCache}
          onPick={(pick) => {
            setLabelCache((prev) => ({ ...prev, [pick.id]: pick.title }))
            setCoverCache((prev) => ({ ...prev, [pick.id]: pick.cover }))
            setSeedIds((prev) => (prev.includes(pick.id) ? prev : [...prev, pick.id]))
          }}
          onRemove={(id) => setSeedIds((prev) => prev.filter((s) => s !== id))}
        />
      ),
    },
    ...catalogueSlots,
    {
      id: 'tuning',
      icon: IconAdjustmentsHorizontal,
      label: t`Tuning`,
      value: tuningParts.length > 0 ? tuningParts.join(', ') : null,
      width: 380,
      onClear: () => {
        setObscurity(0)
        setDiversity(0)
      },
      editor: (
        <RecommenderDials
          obscurity={obscurity}
          setObscurity={setObscurity}
          diversity={diversity}
          setDiversity={setDiversity}
        />
      ),
    },
  ]

  return (
    <div className="discover-tab-body">
      {toolsTarget &&
        createPortal(
          <>
            <ViewModeToggle value={prefs.viewMode} onChange={prefs.setViewMode} />
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
          </>,
          toolsTarget,
        )}

      {showSubTabs && (
        <div
          className="series-tabs sub-tabs recommended-sub-tabs"
          role="tablist"
          aria-label={t`Recommendation views`}
          onKeyDown={onRovingKeyDown}
        >
          <button
            type="button"
            role="tab"
            id={similarTabId}
            aria-controls={similarPanelId}
            aria-selected={activeSection === 'similar'}
            tabIndex={activeSection === 'similar' ? 0 : -1}
            data-active={activeSection === 'similar' || undefined}
            className="series-tab"
            onClick={() => setSection('similar')}
          >
            <IconCompass size={15} aria-hidden />
            <span>{appliedSeeded ? <Trans>Feels like your seeds</Trans> : <Trans>Because of what you collect</Trans>}</span>
            <span className="recommended-tab-count tnum">{formatNumber(totalSimilar)}</span>
          </button>
          <button
            type="button"
            role="tab"
            id={relatedTabId}
            aria-controls={relatedPanelId}
            aria-selected={activeSection === 'related'}
            tabIndex={activeSection === 'related' ? 0 : -1}
            data-active={activeSection === 'related' || undefined}
            className="series-tab"
            onClick={() => setSection('related')}
          >
            <IconAffiliate size={15} aria-hidden />
            <span>{appliedSeeded ? <Trans>Related to your seeds</Trans> : <Trans>Related to your library</Trans>}</span>
            <span className="recommended-tab-count tnum">{formatNumber(related.length)}</span>
          </button>
        </div>
      )}

      <div
        role={showSubTabs ? 'tabpanel' : undefined}
        id={showSubTabs ? (activeSection === 'related' ? relatedPanelId : similarPanelId) : undefined}
        aria-labelledby={showSubTabs ? (activeSection === 'related' ? relatedTabId : similarTabId) : undefined}
      >
      {activeSection === 'similar' ? (
        <CatalogueFilterBar
          slots={filterSlots}
          isCustomized={isCustomized}
          onReset={reset}
          saved={
            <PresetMenu
              compact
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
          }
          onSaveAsDefault={!isCustomized && !hasAnyDefault(savedDefaults) ? undefined : saveAsDefault}
          savingDefault={saveDefaults.isPending}
        />
      ) : (relationKinds.length > 1 || hasCatalogueFilters) && (
        <div className="recommended-related-bar">
          {relationKinds.length > 1 && (
            <div className="tag-chips" role="group" aria-label={t`Relation type`}>
              <button
                type="button"
                className="tag-chip filter-chip"
                data-interactive
                data-on={activeRelationKind === 'all' || undefined}
                aria-pressed={activeRelationKind === 'all'}
                onClick={() => setRelationKind('all')}
              >
                <span className="tag-chip-body">
                  <Trans>All</Trans>
                  <span className="recommended-related-count tnum">{formatNumber(related.length)}</span>
                </span>
              </button>
              {relationKinds.map((kind) => {
                const on = activeRelationKind === kind.value
                const kindLabel = renderLabel(RELATION_KIND_LABELS[kind.value] ?? OTHER_RELATION_LABEL)
                return (
                  <button
                    key={kind.value || '__other'}
                    type="button"
                    className="tag-chip filter-chip"
                    data-interactive
                    data-on={on || undefined}
                    aria-pressed={on}
                    onClick={() => setRelationKind(kind.value)}
                  >
                    <span className="tag-chip-body">
                      {kindLabel}
                      <span className="recommended-related-count tnum">{formatNumber(kind.count)}</span>
                    </span>
                  </button>
                )
              })}
            </div>
          )}
          {hasCatalogueFilters && (
            <span className="recommended-related-note">
              <Trans>Catalogue filters apply to recommendations, not direct relations.</Trans>
            </span>
          )}
        </div>
      )}

      {error && (
        <Callout tone="warn" role="alert">
          {errorText(error)}
        </Callout>
      )}
      {isFetching && !data && (
        <PosterSkeletons density={density} viewMode={viewMode} />
      )}

      <div className="filter-results" data-stale={isPlaceholderData || undefined} aria-busy={isPlaceholderData || undefined}>
        {data && related.length === 0 && similarCount === 0 && (
          <EmptyState
            title={appliedCustomized ? t`No matches` : t`Nothing to recommend yet`}
            description={appliedCustomized ? undefined : t`Add some series to your library first and Maki will suggest more like them.`}
            actionLabel={appliedCustomized ? undefined : t`Go to library`}
            actionTo={appliedCustomized ? undefined : '/library'}
          />
        )}

        {activeSection === 'similar' && data && similarCount === 0 && related.length > 0 && (
          <EmptyState title={t`No matches`} />
        )}

        {activeSection === 'similar' && similarCount > 0 && (
          <>
            {viewMode === 'grid' ? (
              <SimpleGrid cols={POSTER_COLS_BY_DENSITY[density]} spacing="md">
                {similar.map((item) => (
                  <EngineCard
                    key={item.providerId}
                    item={item}
                    inLibrarySeriesId={seriesIdFor(item)}
                    onOpen={setDetailItem}
                    seedCoverUrl={seedCover(item)}
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
                    reasonOverride={engineReason(item)?.text}
                  />
                ))}
              </Stack>
            )}
            {isFetchingNextPage && <NextPageSkeleton density={density} viewMode={viewMode} />}
            {isFetchNextPageError && (
              <div className="recommended-stream-end" role="alert">
                <span><Trans>Could not load more recommendations.</Trans></span>
                <Button variant="default" size="xs" onClick={() => void fetchNextPage()}>
                  <Trans>Try again</Trans>
                </Button>
              </div>
            )}
            {hasNextPage && !isFetchNextPageError && (
              <div ref={setSentinel} className="recommended-stream-sentinel" aria-hidden />
            )}
            {!hasNextPage && !isFetchingNextPage && (
              <div className="recommended-stream-end">
                <span>
                  <Plural value={similarCount} one="All # pick shown" other="All # picks shown" />
                </span>
                <Button
                  variant="subtle"
                  size="xs"
                  leftSection={<IconRefresh size={14} />}
                  loading={isFetching}
                  onClick={() => apply(true)}
                >
                  <Trans>Refresh</Trans>
                </Button>
              </div>
            )}
          </>
        )}

        {activeSection === 'related' && visibleRelated.length > 0 && (
          viewMode === 'grid' ? (
            <SimpleGrid cols={POSTER_COLS_BY_DENSITY[density]} spacing="md">
              {visibleRelated.map((item) => (
                <EngineCard
                  key={item.providerId}
                  item={item}
                  inLibrarySeriesId={seriesIdFor(item)}
                  onOpen={setDetailItem}
                  seedCoverUrl={seedCover(item)}
                />
              ))}
            </SimpleGrid>
          ) : (
            <Stack gap="xs">
              {visibleRelated.map((item) => (
                <RecommendationRow
                  key={item.providerId}
                  item={item}
                  inLibrarySeriesId={seriesIdFor(item)}
                  density={density}
                  onOpen={setDetailItem}
                  reasonOverride={engineReason(item)?.text}
                />
              ))}
            </Stack>
          )
        )}
      </div>
      </div>

      <DiscoverDetailModal
        item={detailItem}
        feedbackContext={{ surface: 'recommended' }}
        inLibrarySeriesId={detailItem ? seriesIdFor(detailItem) : null}
        rootFolders={rootFolders}
        onClose={() => setDetailItem(null)}
        onReroll={luckyItems.length > 1 ? rerollDetail : undefined}
      />
    </div>
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
  onOpenItem: OpenItem
  onClose: () => void
}) {
  const { t } = useLingui()
  const catalogue = useCatalogueFilters()
  const filterSlots = useCatalogueFilterSlots(catalogue.controls, { creators: true })
  const [applied, setApplied] = useState<RecommendationFilters>({})
  useLiveApply(catalogue.build, setApplied)
  // An open filter editor takes Escape first, so the modal behind it stays open.
  const [editorOpen, setEditorOpen] = useState(false)
  useModalHistory(rail != null, onClose)
  // Its own scope: the rails behind it are fixed-size rows, so this density is nobody else's.
  const prefs = useViewPrefs('discover-expand')
  const seedCover = useSeedCover()

  // Reset filters whenever a different rail is opened. A custom rail opens with its filter loaded
  // instead, since the filter is the whole point of the rail, and so does the follow rail, whose
  // filter is the people followed.
  const railKey = rail?.key
  const railTitle = rail?.title
  const railGenre = rail?.genre ?? null
  const expandSource = useMemo(
    () => (railKey ? railSource({ key: railKey, title: railTitle ?? '', genre: railGenre }) : OTHER_SOURCE),
    [railKey, railTitle, railGenre],
  )
  const presetFilters =
    railKey?.startsWith(CUSTOM_RAIL_PREFIX) || railKey === FOLLOWING_RAIL_KEY ? rail?.filters : null
  const resetAll = catalogue.reset
  const hydrateAll = catalogue.hydrate
  useEffect(() => {
    if (presetFilters) {
      hydrateAll(presetFilters)
      setApplied(normalizeFilters(presetFilters))
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
          excludeInteracted: rail.excludeInteracted,
          releasedWithinMonths: rail.releasedWithinMonths,
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
      closeOnEscape={!editorOpen}
      title={<ModalHeading title={rail?.title} />}
    >
      <CatalogueFilterBar
        slots={filterSlots}
        isCustomized={catalogue.isCustomized || Object.keys(applied).length > 0}
        onReset={() => {
          catalogue.reset()
          setApplied({})
        }}
        onOpenChange={setEditorOpen}
        count={
          !personalised && !cohort && rail ? (
            <FilterMatchCount filters={catalogue.build()} feed={rail.feed} genre={rail.genre} />
          ) : undefined
        }
        saved={
          <PresetMenu
            compact
            current={catalogue.build}
            onLoad={(f) => {
              catalogue.hydrate(f)
              setApplied(f)
            }}
          />
        }
      />

      {error && (
        <Callout tone="warn" role="alert">
          {errorText(error)}
        </Callout>
      )}

      {isFetching && !items && <PosterSkeletons density={prefs.density} viewMode={prefs.viewMode} />}

      {items && items.length === 0 && (
        <EmptyState
          title={t`No matches`}
        />
      )}

      {items && items.length > 0 && (
        <>
          <Group justify="space-between" mb="sm">
            <Text c="var(--ink-3)" size="sm">
              <Plural value={items.length} one="# title" other="# titles" />
            </Text>
            <Group gap="xs">
              <ViewPrefsControls prefs={prefs} />
            </Group>
          </Group>
          {/* The personalised rails are recommender output, so they keep the engine card and its
              reason footer, as on the rail and the Recommended tab. Catalogue and cohort rails
              share the Add page's results. */}
          {personalised ? (
            prefs.viewMode === 'grid' ? (
              <SimpleGrid cols={prefs.cols} spacing="md">
                {items.map((item) => (
                  <EngineCard
                    key={item.providerId}
                    item={item}
                    inLibrarySeriesId={seriesIdFor(item)}
                    onOpen={onOpenItem}
                    seedCoverUrl={seedCover(item)}
                    savedFrom={expandSource}
                  />
                ))}
              </SimpleGrid>
            ) : (
              <Stack gap="xs">
                {items.map((item) => (
                  <RecommendationRow
                    key={item.providerId}
                    item={item}
                    inLibrarySeriesId={seriesIdFor(item)}
                    density={prefs.density}
                    onOpen={onOpenItem}
                    savedFrom={expandSource}
                    reasonOverride={engineReason(item)?.text}
                  />
                ))}
              </Stack>
            )
          ) : (
            <Results items={items} prefs={prefs} seriesIdFor={seriesIdFor} onOpen={onOpenItem} savedFrom={expandSource} />
          )}
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
 * The catalogue feeds whose Show more is the Add page: each is a sort, plus the rail's own type or
 * genre. Null keeps the modal: Trending has no sort to match, and the personalised, cohort,
 * following and custom rails page endpoints of their own.
 */
function catalogueSeedFor(rail: DiscoverRail): CatalogueSeed | null {
  if (rail.seedIds?.length || rail.key.startsWith(CUSTOM_RAIL_PREFIX) || rail.key === FOLLOWING_RAIL_KEY) return null
  switch (rail.feed) {
    case 'Popular':
      return { sort: 'popular', filters: {} }
    case 'TopRated':
      return { sort: 'rating', filters: {} }
    case 'New':
      return { sort: 'newest', filters: {} }
    case 'PopularManhwa':
      return { sort: 'popular', filters: { types: ['manhwa'] } }
    case 'PopularManhua':
      return { sort: 'popular', filters: { types: ['manhua'] } }
    case 'GenreSpotlight':
      return rail.genre ? { sort: 'popular', filters: { genres: [rail.genre] } } : null
    default:
      return null
  }
}

/** Catalogue browse: Popular / New / Trending / … rails, independent of the library. */
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
  /** Layout edit mode: the sections stay on the page, framed and inert, in the draft's order. */
  editing: boolean
  onExitEditing: () => void
}) {
  const { t } = useLingui()
  const { data: ui, isError: uiFailed } = useUiSettings()
  const { data: customRails, isError: railsFailed } = useCustomRails()
  const discoverRails = useMemo(
    () => customRails?.filter((r) => r.placement === 'discover'),
    [customRails],
  )

  // Shipping order while the setting loads, so the page doesn't reflow once it arrives. While the
  // layout is being edited the page draws the draft instead.
  const railIds = useMemo(() => (discoverRails ?? []).map((r) => r.id), [discoverRails])
  const saved = useMemo(
    () => ui?.discoverLayout?.sections ?? reconcileLayout([], DISCOVER_LAYOUT_CONFIG, railIds),
    [ui, railIds],
  )
  const draft = useLayoutDraft({
    editing,
    saved: ui && discoverRails ? saved : undefined,
    config: DISCOVER_LAYOUT_CONFIG,
    railIds,
  })
  const layout = draft?.sections ?? saved
  const on = (key: DiscoverSectionKey) => layout.find((s) => s.key === key)?.enabled ?? false

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
  const { data: genreRails, isFetching: genresFetching } = useDiscoverGenres(
    refreshNonce,
    on('genres'),
  )
  const cohortRequest = useMemo(() => ({}), [])
  const { data: cohortRail, isFetching: cohortFetching, refetch: refetchCohort } = useDiscoverCohort(
    cohortRequest,
    on('cohort'),
  )
  // The cohort rail has no per-user server cache to bust, and its request body never changes
  // shape, so its query key never changes and bumping refreshNonce wouldn't refetch it the way it
  // does for the rails above. Refetch it directly instead; skip the initial nonce so this doesn't
  // double-fetch on mount.
  const mountedRef = useRef(false)
  useEffect(() => {
    if (!mountedRef.current) {
      mountedRef.current = true
      return
    }
    if (on('cohort')) void refetchCohort()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [refreshNonce])

  const { data: rootFolders } = useRootFolders()
  const navigate = useNavigate()
  const [detailItem, setDetailItem] = useState<RecommendationItem | null>(null)
  const [detailSource, setDetailSource] = useState<ShortlistSource>(OTHER_SOURCE)
  const openDetail = useCallback<OpenItem>((item, source) => {
    setDetailItem(item)
    setDetailSource(source ?? OTHER_SOURCE)
  }, [])
  // A sequel or spin-off notification links here with the new work's MangaBaka id.
  const [searchParams, setSearchParams] = useSearchParams()
  const openId = searchParams.get('open')
  useEffect(() => {
    if (!openId || !/^\d+$/.test(openId)) return
    setDetailItem(recommendationStub(openId))
    setDetailSource(OTHER_SOURCE)
  }, [openId])
  const closeDetail = () => {
    setDetailItem(null)
    if (openId) {
      const next = new URLSearchParams(searchParams)
      next.delete('open')
      setSearchParams(next, { replace: true })
    }
  }
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

  // Home's follow rail hands over the same way.
  const expandFollowing = (location.state as { expandFollowing?: DiscoverRail } | null)?.expandFollowing
  useEffect(() => {
    if (!expandFollowing) return
    setExpandedRail(expandFollowing)
    navigate(location.pathname, { replace: true, state: null })
  }, [expandFollowing, navigate, location.pathname])

  const showMore = (rail: DiscoverRail) => {
    const seed = catalogueSeedFor(rail)
    if (seed) navigate('/add', { state: { catalogueSeed: seed } })
    else setExpandedRail(rail)
  }

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

  // The catalogue query's failure and loading states. They live with the catalogue section rather
  // than at the top of the page, since the hero and the personalised rows come from other endpoints
  // and survive this one being down; with that section off they move to Trending instead.
  const catalogueStatus = error ? (
    <Callout tone="warn" role="alert" icon={IconAlertTriangle} title={t`Catalogue unavailable`}>
      <Stack gap="sm" align="flex-start">
        <Text size="sm">{errorText(error)}</Text>
        <Button
          size="xs"
          variant="default"
          leftSection={<IconRefresh size={16} />}
          loading={isFetching}
          onClick={onRefresh}
        >
          <Trans>Try again</Trans>
        </Button>
      </Stack>
    </Callout>
  ) : null

  const sections: Record<DiscoverSectionKey, React.ReactNode> = {
    hero:
      heroItems.length > 0 ? (
        <DiscoverHero items={heroItems} onOpen={(item) => openDetail(item, sectionSource('hero'))} onRecommend={recommendFrom} seriesIdFor={seriesIdFor} />
      ) : (isFetching && !rails) || (recentFetching && recentRail === undefined) ? (
        <DiscoverHeroSkeleton />
      ) : null,

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
              onClick={() => setExpandedRail(recentRail)}
            >
              <Trans>Show more</Trans>
            </Button>
          }
          chevron
        />
        {/* The covers of the series this was built from, with the server's prose subtitle as the
            fallback for a reader whose seeds no longer resolve to library rows. */}
        {recentRail.seedIds && recentRail.seedIds.length > 0 ? (
          <DiscoverSeedStrip seedIds={recentRail.seedIds} />
        ) : (
          railSubtitle(recentRail) && (
            <Text c="var(--ink-3)" size="sm" mb="sm">
              {railSubtitle(recentRail)}
            </Text>
          )
        )}
        <EngineRailRow items={recentRail.items} seriesIdFor={seriesIdFor} onOpen={openDetail} savedFrom={sectionSource('recent')} />
      </div>
    ) : recentFetching ? (
      <RailSkeleton engine title />
    ) : null,

    following: (
      <FollowingRail
        enabled={on('following')}
        limit={40}
        seriesIdFor={seriesIdFor}
        onOpen={openDetail}
        onShowMore={setExpandedRail}
      />
    ),

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
                  onClick={() => setExpandedRail(rail)}
                >
                  <Trans>Show more</Trans>
                </Button>
              }
              chevron
            />
            {rail.seedIds && rail.seedIds.length > 0 ? (
              <DiscoverSeedStrip seedIds={rail.seedIds} label={t`From your library`} />
            ) : (
              <Text c="var(--ink-3)" size="sm" mb="sm">{railSubtitle(rail)}</Text>
            )}
            <EngineRailRow items={rail.items} seriesIdFor={seriesIdFor} onOpen={openDetail} savedFrom={sectionSource('sideinterests')} />
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
              onClick={() => setExpandedRail(cohortRail)}
            >
              <Trans>Show more</Trans>
            </Button>
          }
          chevron
        />
        {cohortRail.subtitle && (
          <Text c="var(--ink-3)" size="sm" mb="sm">
            {cohortRail.subtitle}
          </Text>
        )}
        {/* Not an engine rail, despite being personalised: cohort items hydrate straight from the
            MangaBaka dump, so they carry no `coRead`/`matchedTags`/`becauseOfTitle` and every
            card's footer would read "Similar feel". The heading is the only grounds there is. */}
        <DiscoverRailRow items={cohortRail.items} seriesIdFor={seriesIdFor} onOpen={openDetail} savedFrom={sectionSource('cohort')} />
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
                  onClick={() => setExpandedRail(trendingRail)}
                >
                  <Trans>Show more</Trans>
                </Button>
              }
              chevron
            />
            {/* Ranks are the point of a trending row, so the row is numbered. The counter lives on a
                Discover-only wrapper: `.discover-rail-item` is shared with five other surfaces. */}
            <div className="discover-ranked">
              <DiscoverRailRow items={trendingRail.items} seriesIdFor={seriesIdFor} onOpen={openDetail} savedFrom={sectionSource('trending')} />
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
              leftSection={<IconAdjustmentsHorizontal size={16} />}
              onClick={() =>
                navigate('/add', {
                  state: { catalogueSeed: { sort: 'popular', filters: {} } satisfies CatalogueSeed },
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
              <DiscoverCatalogueSkeleton density={density.density} />
            </>
          ) : catalogueRails.length > 0 ? (
            <DiscoverCatalogue
              rails={catalogueRails}
              cols={density.cols}
              seriesIdFor={seriesIdFor}
              onOpen={openDetail}
              onShowMore={showMore}
            />
          ) : (
            <EmptyState
              title={t`Nothing to browse yet`}
              description={t`The catalogue rails need the local MangaBaka database (Settings → Library → Metadata database).`}
            />
          ))}
      </div>
    ),

    genres:
      genreRails && genreRails.length > 0 ? (
        <div>
          <SectionHeader icon={IconLayoutGrid} title={t`Every genre`} count={genreRails.length} />
          <DiscoverGenreWall rails={genreRails} onOpen={showMore} />
        </div>
      ) : genresFetching ? (
        <DiscoverGenreSkeleton />
      ) : null,
  }

  const renderSection = (key: string) => {
    if (!isRailKey(key)) return sections[key as DiscoverSectionKey]
    const rail = discoverRails?.find((r) => r.id === railIdOf(key))
    return rail ? (
      <CustomRailSection rail={rail} limit={40} onOpen={openDetail} onShowMoreCatalogue={setExpandedRail} />
    ) : null
  }

  // Asked for edit mode, but the saved layout or the rails are still loading.
  if (editing && !(draft && discoverRails)) {
    return <LayoutEditLoading failed={uiFailed || railsFailed} onExit={onExitEditing} />
  }

  // The sections are drawn in the same frames in and out of edit mode, so entering it never
  // rebuilds them; edit mode only adds the frames' own chrome and shrinks the sections.
  const body = (
    <div className="discover-density" data-density={density.density}>
      <LayoutEditSurface
        page="discover"
        placement="discover"
        registry={DISCOVER_SECTION_DEFS}
        editing={editing}
        draft={draft}
        sections={saved.filter((s) => s.enabled)}
        rails={discoverRails ?? []}
        railLimit={40}
        render={renderSection}
        summary={(key) =>
          key === 'sideinterests' && sideInterests ? t`${sideInterests.length} rows right now` : null
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

      {!editing && (
        <Group justify="center" mt="xl">
          <AddRailButton placement="discover" />
        </Group>
      )}

      {expandedRail && (
        <FeedExpandModal
          rail={expandedRail}
          seriesIdFor={seriesIdFor}
          onOpenItem={openDetail}
          onClose={() => setExpandedRail(null)}
        />
      )}

      <DiscoverDetailModal
        item={detailItem}
        feedbackContext={{ surface: 'browse' }}
        inLibrarySeriesId={detailItem ? seriesIdFor(detailItem) : null}
        rootFolders={rootFolders}
        onClose={closeDetail}
        savedFrom={detailSource}
      />
    </div>
  )

  return body
}

type DiscoverTab = 'browse' | 'recommended' | 'taste' | 'shortlist'
const TAB_PATHS: Record<DiscoverTab, string> = {
  browse: '/discover',
  recommended: '/discover/recommended',
  taste: '/discover/taste',
  shortlist: '/discover/shortlist',
}

/**
 * Discover shell: three URL-synced tabs - catalogue Browse (default), Recommended, and the
 * reader's own taste profile.
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
        : tab === 'shortlist'
          ? 'shortlist'
          : 'browse'
  const { data: shortlistCount } = useShortlistCount()

  // The rails are cached for an hour on both sides, so the only way back to a fresh catalogue is
  // this. It lives up here rather than in the tab so it can sit in the page header. The spinner
  // watches the tab's query rather than running its own, which would fetch the catalogue even with
  // every section that shows it switched off.
  const [refreshNonce, setRefreshNonce] = useState(0)
  const railsFetching = useIsFetching({ queryKey: ['discover-rails'] }) > 0
  const refreshRails = useCallback(() => setRefreshNonce((n) => n + 1), [])
  const { editing, enter: enterEditing, exit: exitEditing } = useLayoutEditMode(active === 'browse')

  // One view mode and density for Discover's two poster tabs, up here so the page header's menu
  // is the only control for it. Only one of the tabs is mounted at a time, so sharing the state is
  // safe.
  useState(migrateBrowseDensity)
  const viewPrefs = useViewPrefs('discover')
  const showTools = (active === 'browse' && !editing) || active === 'recommended'
  const [tabTools, setTabTools] = useState<HTMLDivElement | null>(null)

  return (
    <SurfaceFrame width="full" pageStyle="editorial">
      <PageHeader
        title={t`Discover`}
        actions={
          showTools ? (
            <Group gap="xs" wrap="wrap" justify="flex-end">
              <div ref={setTabTools} className="discover-tab-tools" />
              {active === 'browse' && (
                <Button
                  visibleFrom="sm"
                  variant="default"
                  leftSection={<IconRefresh size={16} />}
                  loading={railsFetching}
                  onClick={refreshRails}
                >
                  <Trans>Refresh picks</Trans>
                </Button>
              )}
              {active === 'browse' && (
                <Tooltip label={t`Refresh picks`} withArrow>
                  <ActionIcon
                    hiddenFrom="sm"
                    variant="default"
                    size="input-sm"
                    loading={railsFetching}
                    onClick={refreshRails}
                    aria-label={t`Refresh picks`}
                  >
                    <IconRefresh size={18} />
                  </ActionIcon>
                </Tooltip>
              )}
              <Menu position="bottom-end" withinPortal>
                <Menu.Target>
                  <ToolbarMenuButton />
                </Menu.Target>
                <Menu.Dropdown>
                  {active === 'browse' && (
                    <>
                      <Menu.Item leftSection={<IconLayoutDashboard size={16} />} onClick={enterEditing}>
                        <Trans>Edit layout</Trans>
                      </Menu.Item>
                      <Menu.Divider />
                    </>
                  )}
                  <DensityMenuItems value={viewPrefs.density} onChange={viewPrefs.setDensity} />
                </Menu.Dropdown>
              </Menu>
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
            <Trans>Browse</Trans>
          </Tabs.Tab>
          <Tabs.Tab value="recommended" disabled={editing}>
            <Trans>Recommended</Trans>
          </Tabs.Tab>
          <Tabs.Tab value="taste" disabled={editing}>
            <Trans>Your taste</Trans>
          </Tabs.Tab>
          <Tabs.Tab value="shortlist" disabled={editing}>
            <CountIndicator count={shortlistCount ?? 0} inline position="middle-end" offset={-14} withBorder={false}>
              <Trans>Shortlist</Trans>
            </CountIndicator>
          </Tabs.Tab>
        </Tabs.List>
      </Tabs>

      {active === 'recommended' ? (
        <RecommendedTab prefs={viewPrefs} toolsTarget={tabTools} />
      ) : active === 'taste' ? (
        <Suspense fallback={<DelayedLoader />}>
          <TasteTab />
        </Suspense>
      ) : active === 'shortlist' ? (
        <Suspense fallback={<DelayedLoader />}>
          <ShortlistTab />
        </Suspense>
      ) : (
        <DiscoverBrowseTab
          refreshNonce={refreshNonce}
          onRefresh={refreshRails}
          density={viewPrefs}
          editing={editing}
          onExitEditing={exitEditing}
        />
      )}
    </SurfaceFrame>
  )
}
