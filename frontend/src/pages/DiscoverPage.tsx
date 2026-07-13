import { useEffect, useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import {
  ActionIcon,
  Alert,
  Badge,
  Button,
  Card,
  Collapse,
  Group,
  MultiSelect,
  RangeSlider,
  SimpleGrid,
  Skeleton,
  Slider,
  Stack,
  Text,
  ThemeIcon,
  Title,
  Tooltip,
} from '@mantine/core'
import {
  IconAdjustmentsHorizontal,
  IconAffiliate,
  IconCheck,
  IconPlus,
  IconRefresh,
  IconSparkles,
  IconStar,
} from '@tabler/icons-react'
import { useDebouncedValue } from '@mantine/hooks'
import {
  useMetadataSearch,
  useRecommendations,
  useRootFolders,
  useSeries,
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
 *  reason line, title and meta, and a corner control quick-opens (or navigates when owned). */
function RecommendationCard({
  item,
  inLibrarySeriesId,
  onOpen,
}: {
  item: RecommendationItem
  /** Library series id if already owned (shows a persistent "in library" check); null otherwise. */
  inLibrarySeriesId: number | null
  onOpen: (item: RecommendationItem) => void
}) {
  const navigate = useNavigate()
  const owned = inLibrarySeriesId != null
  const reason = reasonFor(item)

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
}

const POSTER_COLS = { base: 2, xs: 3, sm: 4, md: 5, xl: 6 }

function SectionHeader({
  icon: Icon,
  title,
  count,
}: {
  icon: typeof IconSparkles
  title: string
  count: number
}) {
  return (
    <Group gap="xs" mb="sm" mt="xl">
      <ThemeIcon variant="light" color="brand" size="md" radius="md">
        <Icon size={16} />
      </ThemeIcon>
      <Title order={4}>{title}</Title>
      <Badge variant="light" color="gray" size="sm">
        {count}
      </Badge>
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

export default function DiscoverPage() {
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
  const { data, isFetching, error } = useRecommendations(applied)

  const apply = (refresh = false) => {
    const filters: RecommendationFilters = {}
    if (years[0] > YEAR_MIN) filters.yearMin = years[0]
    if (years[1] < YEAR_MAX) filters.yearMax = years[1]
    if (types.length) filters.types = types
    if (statuses.length) filters.statuses = statuses
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
    if (obscurity !== 0) chips.push(obscurity > 0 ? 'hidden gems' : 'mainstream')
    for (const t of types) chips.push(t)
    for (const s of statuses) chips.push(s)
    return chips
  }, [seedIds, years, minRating, obscurity, types, statuses])

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
      <PageHeader
        title="Discover"
        description="Personalised picks from your library's feel — powered by semantic matching over the MangaBaka catalogue."
        actions={
          <>
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
          </>
        }
      />

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

            <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="lg">
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

      {data && data.related.length === 0 && data.similar.length === 0 && (
        <EmptyState
          icon={IconSparkles}
          title={isCustomized ? 'No matches' : 'Nothing to recommend yet'}
          description={
            isCustomized
              ? 'No matches for these seeds and filters — try loosening them.'
              : 'Add some series to your library first and Mangarr will suggest more like them.'
          }
          actionLabel={isCustomized ? undefined : 'Go to library'}
          actionTo={isCustomized ? undefined : '/'}
        />
      )}

      {data && data.related.length > 0 && (
        <>
          <SectionHeader
            icon={IconAffiliate}
            title={seedIds.length > 0 ? 'Related to your seeds' : 'Related to your library'}
            count={data.related.length}
          />
          <SimpleGrid cols={POSTER_COLS} spacing="md">
            {data.related.map((item) => (
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

      {data && data.similar.length > 0 && (
        <>
          <SectionHeader
            icon={IconSparkles}
            title={seedIds.length > 0 ? 'Feels like your seeds' : 'Because of what you collect'}
            count={data.similar.length}
          />
          <SimpleGrid cols={POSTER_COLS} spacing="md">
            {data.similar.map((item) => (
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
