import { useCallback, useEffect, useMemo, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import {
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
  Slider,
  Stack,
  Tabs,
  Text,
  ThemeIcon,
  Title,
} from '@mantine/core'
import {
  IconAdjustmentsHorizontal,
  IconAffiliate,
  IconChevronRight,
  IconCompass,
  IconDeviceFloppy,
  IconLayoutGrid,
  IconPlus,
  IconRefresh,
  IconSparkles,
} from '@tabler/icons-react'
import { useDebouncedValue } from '@mantine/hooks'
import { notifications } from '@mantine/notifications'
import {
  allowedContentRatings,
  CONTENT_RATING_LABELS,
  useDiscover,
  useDiscoverFeed,
  useDiscoverGenres,
  useDiscoverRecentActivity,
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
import { DiscoverDetailModal } from '../components/DiscoverDetailModal'
import { DiscoverRailRow, RecommendationCard, RecommendationRow } from '../components/ui/DiscoverRail'
import { CatalogueBrowser, PosterSkeletons as SharedPosterSkeletons } from '../components/CatalogueBrowser'
import { EmptyState } from '../components/ui/EmptyState'
import { PageHeader } from '../components/ui/PageHeader'
import { SectionHeader } from '../components/ui/SectionHeader'
import {
  DensityControl,
  POSTER_COLS_BY_DENSITY,
  ViewPrefsControls,
  useDensityPref,
  useViewPrefs,
  type Density,
} from '../components/ui/viewPrefs'

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

function PosterSkeletons({ count, density = 'default' }: { count: number; density?: Density }) {
  return <SharedPosterSkeletons count={count} density={density} />
}

/** The recommendation engine: Maki's library-driven "more like what you own" picks. */
function RecommendedTab() {
  const { data: library } = useSeries()
  const { data: rootFolders } = useRootFolders()
  const prefs = useViewPrefs('discover')
  const { viewMode, density } = prefs

  // --- customization controls ---
  const [customizeOpen, setCustomizeOpen] = useState(false)
  const [seedIds, setSeedIds] = useState<string[]>([])
  const [seedSearch, setSeedSearch] = useState('')
  const [debouncedSearch] = useDebouncedValue(seedSearch, 300)
  const { data: seedSearchResults } = useMetadataSearch(debouncedSearch)
  const [years, setYears] = useState<[number, number]>([YEAR_MIN, YEAR_MAX])
  const [types, setTypes] = useState<string[]>([])
  const [statuses, setStatuses] = useState<string[]>([])
  const [genres, setGenres] = useState<string[]>([])
  const [tags, setTags] = useState<string[]>([])
  const { data: tagOptions } = useRecommendationTags()
  const [chapters, setChapters] = useState<[number, number]>([CHAPTER_MIN, CHAPTER_MAX])
  const [minRating, setMinRating] = useState(0)
  const [obscurity, setObscurity] = useState(0)
  const [diversity, setDiversity] = useState(0)
  const [contentRatings, setContentRatings] = useState<string[]>([])
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
  const [labelCache, setLabelCache] = useState<Record<string, string>>({})
  useEffect(() => {
    setLabelCache((prev) => {
      const next = { ...prev }
      for (const s of library ?? []) {
        if (s.mangaBakaId != null) next[String(s.mangaBakaId)] = s.title
      }
      for (const r of seedSearchResults ?? []) next[r.providerId] = r.title
      return next
    })
  }, [library, seedSearchResults])
  const seedOptions = useMemo(
    () => Object.entries(labelCache).map(([value, label]) => ({ value, label })),
    [labelCache],
  )

  // The request actually driving the query; `nonce` forces a refetch on Apply/Refresh.
  const [applied, setApplied] = useState<RecommendationRequest & { nonce: number }>({ nonce: 0 })

  // --- saved defaults ---
  // The panel is seeded from the user's saved default exactly once, and the query stays disabled
  // until that has happened: enabling it earlier would fire an unfiltered request that the
  // hydration then immediately replaces with the filtered one. An error hydrates too, so a failed
  // read of the defaults degrades to "no default" rather than to a tab that never loads.
  const { data: savedDefaults, isSuccess: defaultsLoaded, isError: defaultsFailed } =
    useRecommendationDefaults()
  const saveDefaults = useSaveRecommendationDefaults()
  const [hydrated, setHydrated] = useState(false)
  useEffect(() => {
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
  }, [hydrated, defaultsLoaded, defaultsFailed, savedDefaults])

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
      <Group justify="flex-end" mb="md">
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
        <Card withBorder radius="md" padding="md" mb="md">
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
        <Group gap={6} mb="md">
          {activeFilterChips.map((chip) => (
            <Badge key={chip} variant="light" color="brand" size="sm" radius="sm">
              {chip}
            </Badge>
          ))}
        </Group>
      )}

      {error && (
        <Alert color="yellow" variant="light">
          {String(error)}
        </Alert>
      )}
      {isFetching && !data && (
        <>
          <Text c="dimmed" size="sm" mb="sm">
            Scanning the MangaBaka database for matches…
          </Text>
          <PosterSkeletons count={12} />
        </>
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
        />
      )}

      {similar.length > 0 && (
        <>
          <SectionHeader
            icon={IconSparkles}
            title={seedIds.length > 0 ? 'Feels like your seeds' : 'Because of what you collect'}
            count={similar.length}
          />
          {viewMode === 'grid' ? (
            <SimpleGrid cols={POSTER_COLS_BY_DENSITY[density]} spacing="md">
              {similar.map((item) => (
                <RecommendationCard
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
        </>
      )}

      {related.length > 0 && (
        <>
          <SectionHeader
            icon={IconAffiliate}
            title={seedIds.length > 0 ? 'Related to your seeds' : 'Related to your library'}
            count={related.length}
          />
          {viewMode === 'grid' ? (
            <SimpleGrid cols={POSTER_COLS_BY_DENSITY[density]} spacing="md">
              {related.map((item) => (
                <RecommendationCard
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

  const feedRequest =
    rail && !personalised
      ? { feed: rail.feed, genre: rail.genre, filters: applied, limit: 120 }
      : null
  const feedQuery = useDiscoverFeed(feedRequest)

  const recRequest = useMemo(
    () => ({ seedIds: seedIds ?? undefined, filters: applied }),
    [seedIds, applied],
  )
  const recQuery = useRecommendations(recRequest, personalised)
  // Relations lead here for the same reason they lead the rail itself: a sequel to something just
  // finished is the most actionable pick. They come from page 0 only — the pager walks `similar`.
  const recItems = useMemo(
    () =>
      recQuery.data
        ? [
            ...(recQuery.data.pages[0]?.related ?? []),
            ...recQuery.data.pages.flatMap((p) => p.similar),
          ]
        : undefined,
    [recQuery.data],
  )

  const items = personalised ? recItems : feedQuery.data
  const isFetching = personalised ? recQuery.isFetching : feedQuery.isFetching
  const error = personalised ? recQuery.error : feedQuery.error

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

      {isFetching && !items && <PosterSkeletons count={18} density={density} />}

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
 * Renders a set of catalogue rails (each its own horizontal-scroll row) with a Refresh button,
 * loading/empty/error states, and the shared detail modal. Owns the library lookup for "in
 * library" marking. Both the Browse and Genres tabs are this, fed by different hooks.
 */
function RailsView({
  rails,
  isFetching,
  error,
  onRefresh,
  loadingText,
  emptyTitle,
  emptyDescription,
}: {
  rails: DiscoverRail[] | undefined
  isFetching: boolean
  error: unknown
  onRefresh: () => void
  loadingText: string
  emptyTitle: string
  emptyDescription: string
}) {
  const { data: rootFolders } = useRootFolders()
  const [detailItem, setDetailItem] = useState<RecommendationItem | null>(null)
  const [expandedRail, setExpandedRail] = useState<DiscoverRail | null>(null)
  const seriesIdFor = useSeriesIdLookup()

  return (
    <>
      <Group justify="flex-end" mb="md">
        <Button
          variant="default"
          leftSection={<IconRefresh size={16} />}
          loading={isFetching}
          onClick={onRefresh}
        >
          Refresh
        </Button>
      </Group>

      {error && (
        <Alert color="yellow" variant="light">
          {String(error)}
        </Alert>
      )}

      {isFetching && !rails && (
        <>
          <Text c="dimmed" size="sm" mb="sm">
            {loadingText}
          </Text>
          <PosterSkeletons count={12} />
        </>
      )}

      {rails?.length === 0 && !error && (
        <EmptyState icon={IconCompass} title={emptyTitle} description={emptyDescription} />
      )}

      {rails?.map((rail) => (
        <div key={rail.key}>
          <SectionHeader
            icon={IconSparkles}
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
          {rail.subtitle && (
            <Text c="dimmed" size="sm" mb="sm">
              {rail.subtitle}
            </Text>
          )}
          <DiscoverRailRow items={rail.items} seriesIdFor={seriesIdFor} onOpen={setDetailItem} />
        </div>
      ))}

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
    </>
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
function DiscoverBrowseTab() {
  const [refreshNonce, setRefreshNonce] = useState(0)
  const { data: rails, isFetching, error } = useDiscover(refreshNonce)
  const { data: recentRail } = useDiscoverRecentActivity(refreshNonce)

  // The personalised rail leads, and only once the catalogue rails have arrived: handing RailsView
  // a one-element list while `rails` is still undefined would end its loading state early and leave
  // a single row hanging over a blank page. It is absent entirely for anyone with no reading
  // history yet, which is what the server answers with null for.
  const allRails = useMemo(
    () => (rails ? (recentRail ? [recentRail, ...rails] : rails) : undefined),
    [rails, recentRail],
  )

  // Every keystroke in the search box re-renders this component, and the rails below it are
  // hundreds of cards. Hold the subtree in a memo so React can skip it entirely unless the rails
  // themselves changed: without this, typing costs ~130ms a character.
  const refresh = useCallback(() => setRefreshNonce((n) => n + 1), [])
  const railsView = useMemo(
    () => (
      <RailsView
        rails={allRails}
        isFetching={isFetching}
        error={error}
        onRefresh={refresh}
        loadingText="Scanning the MangaBaka catalogue…"
        emptyTitle="Nothing to browse yet"
        emptyDescription="The catalogue rails need the local MangaBaka database (Settings → Metadata → local DB)."
      />
    ),
    [allRails, isFetching, error, refresh],
  )

  return (
    <CatalogueBrowser
      scope="discover"
      idle={railsView}
      placeholder={`Describe what you're after, a title, or author:"Junji Ito"`}
      hideSearch
    />
  )
}

/** Per-genre browse: one "Popular in {genre}" rail per genre. */
function DiscoverGenresTab() {
  const [refreshNonce, setRefreshNonce] = useState(0)
  const { data: rails, isFetching, error } = useDiscoverGenres(refreshNonce)
  return (
    <RailsView
      rails={rails}
      isFetching={isFetching}
      error={error}
      onRefresh={() => setRefreshNonce((n) => n + 1)}
      loadingText="Ranking each genre by popularity…"
      emptyTitle="No genre rails yet"
      emptyDescription="The genre rails need the local MangaBaka database (Settings → Metadata → local DB)."
    />
  )
}

type DiscoverTab = 'browse' | 'genres' | 'recommended'
const TAB_PATHS: Record<DiscoverTab, string> = {
  browse: '/discover',
  genres: '/discover/genres',
  recommended: '/discover/recommended',
}

/** Discover shell: three URL-synced tabs - catalogue Browse (default), per-Genre, and Recommended. */
export default function DiscoverPage() {
  const { tab } = useParams()
  const navigate = useNavigate()
  const active: DiscoverTab =
    tab === 'recommended' ? 'recommended' : tab === 'genres' ? 'genres' : 'browse'

  return (
    <>
      <PageHeader
        title="Discover"
        description="Browse the MangaBaka catalogue, or get personalised picks from your library's feel."
      />

      <Tabs
        value={active}
        onChange={(v) => navigate(TAB_PATHS[(v as DiscoverTab) ?? 'browse'])}
        mb="md"
      >
        <Tabs.List>
          <Tabs.Tab value="browse" leftSection={<IconCompass size={16} />}>
            Discover
          </Tabs.Tab>
          <Tabs.Tab value="genres" leftSection={<IconLayoutGrid size={16} />}>
            Genres
          </Tabs.Tab>
          <Tabs.Tab value="recommended" leftSection={<IconSparkles size={16} />}>
            Recommended
          </Tabs.Tab>
        </Tabs.List>
      </Tabs>

      {active === 'recommended' ? (
        <RecommendedTab />
      ) : active === 'genres' ? (
        <DiscoverGenresTab />
      ) : (
        <DiscoverBrowseTab />
      )}
    </>
  )
}
