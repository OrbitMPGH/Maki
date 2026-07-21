import { memo, useCallback, useEffect, useMemo, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
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
  TextInput,
  ThemeIcon,
  Title,
  Tooltip,
} from '@mantine/core'
import {
  IconAdjustmentsHorizontal,
  IconAffiliate,
  IconCheck,
  IconChevronRight,
  IconCompass,
  IconLayoutGrid,
  IconPlus,
  IconRefresh,
  IconSearch,
  IconSparkles,
  IconStar,
  IconX,
} from '@tabler/icons-react'
import { useDebouncedValue } from '@mantine/hooks'
import {
  useDiscover,
  useDiscoverFeed,
  useDiscoverGenres,
  useDiscoverSearch,
  useMetadataSearch,
  useRecommendations,
  useRecommendationTags,
  useRootFolders,
  useSeries,
  type DiscoverRail,
  type RecommendationFilters,
  type RecommendationItem,
  type RecommendationRequest,
} from '../api/hooks'
import { DiscoverDetailModal } from '../components/DiscoverDetailModal'
import { EmptyState } from '../components/ui/EmptyState'
import { PageHeader } from '../components/ui/PageHeader'

const YEAR_MIN = 1950
const YEAR_MAX = 2026
const TYPE_OPTIONS = ['manga', 'manhwa', 'manhua', 'oel', 'other']
const STATUS_OPTIONS = ['completed', 'releasing', 'hiatus', 'cancelled']
// Curated from the MangaBaka genre vocabulary (matching is case-insensitive, so casing variants
// like "Sci-Fi"/"Sci-fi" collapse to one option).
const GENRE_OPTIONS = [
  'Action', 'Adventure', 'Comedy', 'Drama', 'Ecchi', 'Fantasy', 'Harem', 'Historical', 'Horror',
  'Isekai', 'Josei', 'Martial Arts', 'Mecha', 'Mystery', 'Psychological', 'Romance', 'School Life',
  'Sci-Fi', 'Seinen', 'Shoujo', 'Shounen', 'Slice of Life', 'Sports', 'Supernatural', 'Thriller',
  'Tragedy', 'Boys Love', 'Girls Love',
]
const CHAPTER_MIN = 0
const CHAPTER_MAX = 500 // upper handle here means "500+" (no maximum)

function reasonFor(item: RecommendationItem): string {
  if (item.relationKind) {
    return `${item.relationKind} of ${item.relatedToTitle}`
  }
  const parts: string[] = []
  if (item.authorMatch) parts.push('same author')
  const because = [...item.matchedGenres, ...item.matchedTags].slice(0, 3)
  if (because.length > 0) parts.push(because.join(', '))
  // Semantic picks name the seed whose feel drove them; genre-only hits keep "Because:".
  if (item.becauseOfTitle) {
    const feel = `Feels like ${item.becauseOfTitle}`
    return parts.length > 0 ? `${feel} · ${parts.join(' · ')}` : feel
  }
  return parts.length > 0 ? `Because: ${parts.join(' · ')}` : 'Similar feel'
}

/** Poster-forward Discover card. Cover art is the hero; a bottom scrim carries the
 *  reason line, title and meta, and a corner control quick-opens (or navigates when owned).
 *
 *  Memoized because Discover mounts hundreds of these at once: without it, any state change on
 *  an ancestor (a keystroke in the search box, say) reconciles every card on the page. */
export const RecommendationCard = memo(function RecommendationCard({
  item,
  inLibrarySeriesId,
  onOpen,
  reasonOverride,
}: {
  item: RecommendationItem
  /** Library series id if already owned (shows a persistent "in library" check); null otherwise. */
  inLibrarySeriesId: number | null
  onOpen: (item: RecommendationItem) => void
  /** Overrides the reason line: a string replaces it, `null` hides it. Omit for the default. */
  reasonOverride?: string | null
}) {
  const navigate = useNavigate()
  const owned = inLibrarySeriesId != null
  const reason = reasonOverride !== undefined ? reasonOverride : reasonFor(item)

  return (
    <div
      className="cover-card discover-card"
      role="button"
      tabIndex={0}
      aria-label={item.title}
      onClick={() => onOpen(item)}
      onKeyDown={(e) => {
        if (e.key === 'Enter' || e.key === ' ') {
          e.preventDefault()
          onOpen(item)
        }
      }}
    >
      <div className="cover-poster">
        {item.coverUrl ? (
          <img src={item.coverUrl} alt={item.title} loading="lazy" />
        ) : (
          <div className="cover-placeholder">{item.title}</div>
        )}
        <div className="cover-scrim" />

        {item.rating != null && (
          <Badge
            size="sm"
            variant="filled"
            color="dark.9"
            leftSection={<IconStar size={10} style={{ color: '#f5c518' }} />}
            style={{ position: 'absolute', top: 8, left: 8, backdropFilter: 'blur(4px)' }}
          >
            {(item.rating / 10).toFixed(1)}
          </Badge>
        )}

        {owned ? (
          <Tooltip label="In library — open" withArrow>
            <ActionIcon
              className="discover-corner"
              variant="filled"
              color="teal"
              radius="xl"
              size="md"
              aria-label="View in library"
              onClick={(e) => {
                e.stopPropagation()
                navigate(`/series/${inLibrarySeriesId}`)
              }}
            >
              <IconCheck size={16} />
            </ActionIcon>
          </Tooltip>
        ) : (
          <Tooltip label="View & add" withArrow>
            <ActionIcon
              className="discover-corner"
              data-add="true"
              variant="filled"
              color="brand"
              radius="xl"
              size="md"
              aria-label="View and add"
              onClick={(e) => {
                e.stopPropagation()
                onOpen(item)
              }}
            >
              <IconPlus size={16} />
            </ActionIcon>
          </Tooltip>
        )}

        <div className="discover-meta">
          {reason && (
            <span className="discover-reason" title={reason}>
              {reason}
            </span>
          )}
          <Text fw={650} size="sm" c="white" lineClamp={2} lh={1.2} title={item.title}>
            {item.title}
          </Text>
          <Group gap={5} mt={5} wrap="nowrap">
            {item.year && (
              <Text size="xs" c="gray.4" className="tnum">
                {item.year}
              </Text>
            )}
            <Text size="xs" c="gray.4" tt="capitalize" lineClamp={1}>
              · {item.status}
            </Text>
            {item.totalChapters && (
              <Text size="xs" c="gray.4" style={{ whiteSpace: 'nowrap' }}>
                · {item.totalChapters} ch
              </Text>
            )}
          </Group>
        </div>
      </div>
    </div>
  )
})

const POSTER_COLS = { base: 2, xs: 3, sm: 4, md: 5, xl: 6 }

/** Maps a catalogue item to the library series id that owns it, or null. */
function useSeriesIdLookup() {
  const { data: library } = useSeries()
  const seriesIdByMangaBaka = useMemo(() => {
    const map = new Map<number, number>()
    for (const s of library ?? []) {
      if (s.mangaBakaId != null) map.set(s.mangaBakaId, s.id)
    }
    return map
  }, [library])
  return (item: RecommendationItem) =>
    seriesIdByMangaBaka.get(Number(item.providerId)) ?? null
}

function SectionHeader({
  icon: Icon,
  title,
  count,
  action,
}: {
  icon: typeof IconSparkles
  title: string
  count: number
  action?: React.ReactNode
}) {
  return (
    <Group gap="xs" mb="sm" mt="xl" wrap="nowrap">
      <ThemeIcon variant="light" color="brand" size="md" radius="md">
        <Icon size={16} />
      </ThemeIcon>
      <Title order={4}>{title}</Title>
      <Badge variant="light" color="gray" size="sm">
        {count}
      </Badge>
      {action && <div style={{ marginLeft: 'auto' }}>{action}</div>}
    </Group>
  )
}

function PosterSkeletons({ count }: { count: number }) {
  return (
    <SimpleGrid cols={POSTER_COLS} spacing="md">
      {Array.from({ length: count }, (_, i) => (
        <Skeleton key={i} radius="lg" style={{ aspectRatio: '2 / 3' }} />
      ))}
    </SimpleGrid>
  )
}

/** The recommendation engine — Maki's library-driven "more like what you own" picks. */
function RecommendedTab() {
  const { data: library } = useSeries()
  const { data: rootFolders } = useRootFolders()

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
  const { data, isFetching, error, fetchNextPage, hasNextPage, isFetchingNextPage } =
    useRecommendations(applied)
  const related = data?.pages[0]?.related ?? []
  const similar = data?.pages.flatMap((p) => p.similar) ?? []

  const apply = (refresh = false) => {
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
    setApplied((prev) => ({
      seedIds: seedIds.length ? seedIds.map(Number) : undefined,
      filters: Object.keys(filters).length ? filters : undefined,
      obscurity: obscurity !== 0 ? obscurity : undefined,
      refresh,
      nonce: prev.nonce + 1,
    }))
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
    obscurity !== 0

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
    for (const g of genres) chips.push(g)
    for (const t of tags) chips.push(t)
    for (const t of types) chips.push(t)
    for (const s of statuses) chips.push(s)
    return chips
  }, [seedIds, years, minRating, chapters, obscurity, genres, tags, types, statuses])

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
            </SimpleGrid>

            <Group justify="flex-end">
              <Button variant="subtle" size="xs" onClick={reset} disabled={!isCustomized}>
                Reset
              </Button>
              <Button size="xs" onClick={() => apply(false)}>
                Apply
              </Button>
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
              ? 'No matches for these seeds and filters — try loosening them.'
              : 'Add some series to your library first and Maki will suggest more like them.'
          }
          actionLabel={isCustomized ? undefined : 'Go to library'}
          actionTo={isCustomized ? undefined : '/'}
        />
      )}

      {similar.length > 0 && (
        <>
          <SectionHeader
            icon={IconSparkles}
            title={seedIds.length > 0 ? 'Feels like your seeds' : 'Because of what you collect'}
            count={similar.length}
          />
          <SimpleGrid cols={POSTER_COLS} spacing="md">
            {similar.map((item) => (
              <RecommendationCard
                key={item.providerId}
                item={item}
                inLibrarySeriesId={seriesIdFor(item)}
                onOpen={setDetailItem}
              />
            ))}
          </SimpleGrid>
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
          <SimpleGrid cols={POSTER_COLS} spacing="md">
            {related.map((item) => (
              <RecommendationCard
                key={item.providerId}
                item={item}
                inLibrarySeriesId={seriesIdFor(item)}
                onOpen={setDetailItem}
              />
            ))}
          </SimpleGrid>
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

/** A single horizontal-scroll rail of poster cards. */
export function DiscoverRailRow({
  items,
  seriesIdFor,
  onOpen,
}: {
  items: RecommendationItem[]
  seriesIdFor: (item: RecommendationItem) => number | null
  onOpen: (item: RecommendationItem) => void
}) {
  return (
    <div className="discover-rail">
      {items.map((item) => (
        <div key={item.providerId} className="discover-rail-item">
          <RecommendationCard
            item={item}
            inLibrarySeriesId={seriesIdFor(item)}
            onOpen={onOpen}
            reasonOverride={null}
          />
        </div>
      ))}
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
  onOpenItem: (item: RecommendationItem) => void
  onClose: () => void
}) {
  const [genres, setGenres] = useState<string[]>([])
  const [statuses, setStatuses] = useState<string[]>([])
  const [types, setTypes] = useState<string[]>([])
  const [years, setYears] = useState<[number, number]>([YEAR_MIN, YEAR_MAX])
  const [minRating, setMinRating] = useState(0)
  const [chapters, setChapters] = useState<[number, number]>([CHAPTER_MIN, CHAPTER_MAX])
  const [applied, setApplied] = useState<RecommendationFilters>({})

  // Reset filters whenever a different rail is opened.
  const railKey = rail?.key
  useEffect(() => {
    setGenres([])
    setStatuses([])
    setTypes([])
    setYears([YEAR_MIN, YEAR_MAX])
    setMinRating(0)
    setChapters([CHAPTER_MIN, CHAPTER_MAX])
    setApplied({})
  }, [railKey])

  const request = rail ? { feed: rail.feed, genre: rail.genre, filters: applied, limit: 120 } : null
  const { data: items, isFetching, error } = useDiscoverFeed(request)

  const isCustomized =
    genres.length > 0 ||
    statuses.length > 0 ||
    types.length > 0 ||
    years[0] > YEAR_MIN ||
    years[1] < YEAR_MAX ||
    minRating > 0 ||
    chapters[0] > CHAPTER_MIN ||
    chapters[1] < CHAPTER_MAX

  const apply = () => {
    const f: RecommendationFilters = {}
    if (years[0] > YEAR_MIN) f.yearMin = years[0]
    if (years[1] < YEAR_MAX) f.yearMax = years[1]
    if (types.length) f.types = types
    if (statuses.length) f.statuses = statuses
    if (genres.length) f.genres = genres
    if (chapters[0] > CHAPTER_MIN) f.minChapters = chapters[0]
    if (chapters[1] < CHAPTER_MAX) f.maxChapters = chapters[1]
    if (minRating > 0) f.minRating = minRating * 10 // slider 0–10, dump rating 0–100
    setApplied(f)
  }

  const reset = () => {
    setGenres([])
    setStatuses([])
    setTypes([])
    setYears([YEAR_MIN, YEAR_MAX])
    setMinRating(0)
    setChapters([CHAPTER_MIN, CHAPTER_MAX])
    setApplied({})
  }

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
          <SimpleGrid cols={{ base: 1, sm: 2, lg: 3 }} spacing="lg">
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
                Chapters: {chapters[0]}–
                {chapters[1] >= CHAPTER_MAX ? `${CHAPTER_MAX}+` : chapters[1]}
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
          <Group justify="flex-end">
            <Button variant="subtle" size="xs" onClick={reset} disabled={!isCustomized}>
              Reset
            </Button>
            <Button size="xs" onClick={apply}>
              Apply
            </Button>
          </Group>
        </Stack>
      </Card>

      {error && (
        <Alert color="yellow" variant="light">
          {String(error)}
        </Alert>
      )}

      {isFetching && !items && <PosterSkeletons count={18} />}

      {items && items.length === 0 && (
        <EmptyState
          icon={IconCompass}
          title="No matches"
          description="No titles match these filters — try loosening them."
        />
      )}

      {items && items.length > 0 && (
        <>
          <Text c="dimmed" size="sm" mb="sm">
            {items.length} title{items.length === 1 ? '' : 's'}
          </Text>
          <SimpleGrid cols={POSTER_COLS} spacing="md">
            {items.map((item) => (
              <RecommendationCard
                key={item.providerId}
                item={item}
                inLibrarySeriesId={seriesIdFor(item)}
                onOpen={onOpenItem}
                reasonOverride={null}
              />
            ))}
          </SimpleGrid>
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
 * Results for a free-text query, matched on meaning by the embedding index. Falls back to plain
 * title matching when that index hasn't been built — `mode` says which answered, and the UI is
 * explicit about it so a description that returns title-ish results isn't mystifying.
 */
const SearchResults = memo(function SearchResults({ query }: { query: string }) {
  const { data, isFetching, error } = useDiscoverSearch({ query, limit: 60 })
  const { data: rootFolders } = useRootFolders()
  const seriesIdFor = useSeriesIdLookup()
  const [detailItem, setDetailItem] = useState<RecommendationItem | null>(null)
  const items = data?.items ?? []

  return (
    <>
      {error && (
        <Alert color="yellow" variant="light" mb="md">
          {String(error)}
        </Alert>
      )}

      {isFetching && !data && (
        <>
          <Text c="dimmed" size="sm" mb="sm">
            Matching your description against the catalogue…
          </Text>
          <PosterSkeletons count={12} />
        </>
      )}

      {data && items.length === 0 && (
        <EmptyState
          icon={IconSearch}
          title="No matches"
          description="Nothing close enough. Try describing the story differently, or use fewer words."
        />
      )}

      {items.length > 0 && (
        <>
          <Group gap="xs" mb="sm" mt="md">
            <Text c="dimmed" size="sm">
              {items.length} match{items.length === 1 ? '' : 'es'}
            </Text>
            {data?.mode === 'title' && (
              <Badge variant="light" color="gray" size="sm">
                title match only — build the recommendation index for search by meaning
              </Badge>
            )}
          </Group>
          <SimpleGrid cols={POSTER_COLS} spacing="md">
            {items.map((item) => (
              <RecommendationCard
                key={item.providerId}
                item={item}
                inLibrarySeriesId={seriesIdFor(item)}
                onOpen={setDetailItem}
                reasonOverride={null}
              />
            ))}
          </SimpleGrid>
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
})

/**
 * Catalogue browse — Popular / New / Trending / … rails, independent of the library. The search
 * box takes over the tab while it has a query: rails are for wandering, search is for looking.
 */
function DiscoverBrowseTab() {
  const [refreshNonce, setRefreshNonce] = useState(0)
  const [query, setQuery] = useState('')
  const [debouncedQuery] = useDebouncedValue(query, 400)
  const { data: rails, isFetching, error } = useDiscover(refreshNonce)
  const searching = debouncedQuery.trim().length >= 3

  // Every keystroke re-renders this component, and the rails below it are hundreds of cards. Hold
  // the subtree in a memo so React can skip it entirely unless the rails themselves changed —
  // without this, typing costs ~130ms a character.
  const refresh = useCallback(() => setRefreshNonce((n) => n + 1), [])
  const railsView = useMemo(
    () => (
      <RailsView
        rails={rails}
        isFetching={isFetching}
        error={error}
        onRefresh={refresh}
        loadingText="Scanning the MangaBaka catalogue…"
        emptyTitle="Nothing to browse yet"
        emptyDescription="The catalogue rails need the local MangaBaka database (Settings → Metadata → local DB)."
      />
    ),
    [rails, isFetching, error, refresh],
  )

  return (
    <>
      <TextInput
        value={query}
        onChange={(e) => setQuery(e.currentTarget.value)}
        placeholder="Describe what you're after — “revenge story with a cooking twist”"
        leftSection={<IconSearch size={16} />}
        rightSection={
          query ? (
            <ActionIcon variant="subtle" color="gray" aria-label="Clear search" onClick={() => setQuery('')}>
              <IconX size={16} />
            </ActionIcon>
          ) : null
        }
        size="md"
        mb="md"
      />

      {searching ? <SearchResults query={debouncedQuery.trim()} /> : railsView}
    </>
  )
}

/** Per-genre browse — one "Popular in {genre}" rail per genre. */
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

/** Discover shell: three URL-synced tabs — catalogue Browse (default), per-Genre, and Recommended. */
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
