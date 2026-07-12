import { useEffect, useMemo, useState } from 'react'
import {
  Alert,
  Badge,
  Button,
  Card,
  Center,
  Collapse,
  Group,
  Image,
  Loader,
  Modal,
  MultiSelect,
  RangeSlider,
  Select,
  SimpleGrid,
  Slider,
  Stack,
  Switch,
  Text,
  Title,
} from '@mantine/core'
import { useDebouncedValue } from '@mantine/hooks'
import { notifications } from '@mantine/notifications'
import {
  useAddSeries,
  useMetadataSearch,
  useRecommendations,
  useRootFolders,
  useSeries,
  type RecommendationFilters,
  type RecommendationItem,
  type RecommendationRequest,
} from '../api/hooks'
import { MetadataLinks } from '../components/MetadataLinks'

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
  const because = [...item.matchedGenres, ...item.matchedTags].slice(0, 4)
  if (because.length > 0) parts.push(because.join(', '))
  return parts.length > 0 ? `Because: ${parts.join(' · ')}` : 'Similar feel'
}

function RecommendationCard({
  item,
  inLibrary,
  onAdd,
}: {
  item: RecommendationItem
  inLibrary: boolean
  onAdd: (item: RecommendationItem) => void
}) {
  return (
    <Card withBorder radius="md" padding="sm">
      <Group wrap="nowrap" align="flex-start">
        {item.coverUrl && (
          <Image src={item.coverUrl} w={70} h={105} radius="sm" fit="cover" alt="" />
        )}
        <Stack gap={4} style={{ flex: 1, minWidth: 0 }}>
          <Group gap="xs" wrap="nowrap">
            <Text fw={600} size="sm" lineClamp={1} style={{ flex: 1 }}>
              {item.title}
            </Text>
            {item.rating != null && (
              <Badge size="xs" variant="light" color="yellow">
                ★ {(item.rating / 10).toFixed(1)}
              </Badge>
            )}
          </Group>
          <Group gap="xs">
            {item.year && (
              <Text size="xs" c="dimmed">
                {item.year}
              </Text>
            )}
            <Badge size="xs" variant="light">
              {item.status}
            </Badge>
            {item.totalChapters && (
              <Text size="xs" c="dimmed">
                {item.totalChapters} ch
              </Text>
            )}
          </Group>
          <Text size="xs" c="indigo.4" lineClamp={1}>
            {reasonFor(item)}
          </Text>
          <Text size="xs" c="dimmed" lineClamp={2}>
            {item.description}
          </Text>
          <Group gap="xs" justify="space-between">
            {inLibrary ? (
              <Badge size="sm" variant="light" color="green">
                In library
              </Badge>
            ) : (
              <Button size="compact-xs" variant="light" onClick={() => onAdd(item)}>
                Add
              </Button>
            )}
            <MetadataLinks
              links={[{ site: 'mangabaka', url: `https://mangabaka.org/${item.providerId}` }]}
              compact
            />
          </Group>
        </Stack>
      </Group>
    </Card>
  )
}

export default function DiscoverPage() {
  const { data: library } = useSeries()
  const { data: rootFolders } = useRootFolders()
  const addSeries = useAddSeries()

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

  // --- add modal ---
  const [selected, setSelected] = useState<RecommendationItem | null>(null)
  const [rootFolderId, setRootFolderId] = useState<string | null>(null)
  const [monitored, setMonitored] = useState(true)

  const libraryIds = new Set(
    (library ?? []).map((s) => s.mangaBakaId).filter((id): id is number => id != null),
  )

  const openAdd = (item: RecommendationItem) => {
    setSelected(item)
    if (rootFolders && rootFolders.length > 0 && !rootFolderId) {
      setRootFolderId(String(rootFolders[0].id))
    }
  }

  const submit = () => {
    if (!selected || !rootFolderId) return
    addSeries.mutate(
      {
        metadataProviderId: selected.providerId,
        rootFolderId: Number(rootFolderId),
        monitored,
        monitorNewItems: monitored ? 'All' : 'None',
      },
      {
        onSuccess: () => {
          notifications.show({ message: `Added ${selected.title}`, color: 'green' })
          setSelected(null)
        },
        onError: (err) => notifications.show({ message: String(err), color: 'red' }),
      },
    )
  }

  return (
    <>
      <Group justify="space-between" mb="md">
        <Title order={2}>Discover</Title>
        <Group gap="xs">
          <Button
            variant={isCustomized ? 'light' : 'default'}
            size="xs"
            onClick={() => setCustomizeOpen((o) => !o)}
          >
            {isCustomized ? 'Customized' : 'Customize'}
          </Button>
          <Button variant="default" size="xs" loading={isFetching} onClick={() => apply(true)}>
            Refresh
          </Button>
        </Group>
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

      {error && (
        <Alert color="yellow" variant="light">
          {String(error)}
        </Alert>
      )}
      {isFetching && !data && (
        <Center py="xl">
          <Loader />
          <Text ml="sm" c="dimmed" size="sm">
            Scanning the MangaBaka database for matches…
          </Text>
        </Center>
      )}

      {data && data.related.length === 0 && data.similar.length === 0 && (
        <Text c="dimmed">
          {isCustomized
            ? 'No matches for these seeds and filters — try loosening them.'
            : 'Nothing to recommend yet — add some series to the library first.'}
        </Text>
      )}

      {data && data.related.length > 0 && (
        <>
          <Title order={4} mb="sm">
            {seedIds.length > 0 ? 'Related to your seeds' : 'Related to your library'}
          </Title>
          <SimpleGrid cols={{ base: 1, md: 2, xl: 3 }} mb="lg">
            {data.related.map((item) => (
              <RecommendationCard
                key={item.providerId}
                item={item}
                inLibrary={libraryIds.has(Number(item.providerId))}
                onAdd={openAdd}
              />
            ))}
          </SimpleGrid>
        </>
      )}

      {data && data.similar.length > 0 && (
        <>
          <Title order={4} mb="sm">
            {seedIds.length > 0 ? 'Feels like your seeds' : 'Because of what you collect'}
          </Title>
          <SimpleGrid cols={{ base: 1, md: 2, xl: 3 }}>
            {data.similar.map((item) => (
              <RecommendationCard
                key={item.providerId}
                item={item}
                inLibrary={libraryIds.has(Number(item.providerId))}
                onAdd={openAdd}
              />
            ))}
          </SimpleGrid>
        </>
      )}

      <Modal
        opened={selected !== null}
        onClose={() => setSelected(null)}
        title={`Add "${selected?.title}"`}
      >
        <Stack>
          {rootFolders && rootFolders.length === 0 && (
            <Text c="orange" size="sm">
              No root folders configured. Add one in Settings first.
            </Text>
          )}
          <Select
            label="Root folder"
            data={rootFolders?.map((f) => ({ value: String(f.id), label: f.path })) ?? []}
            value={rootFolderId}
            onChange={setRootFolderId}
            required
          />
          <Switch
            label="Monitor new chapters"
            checked={monitored}
            onChange={(e) => setMonitored(e.currentTarget.checked)}
          />
          <Button onClick={submit} loading={addSeries.isPending} disabled={!rootFolderId}>
            Add series
          </Button>
        </Stack>
      </Modal>
    </>
  )
}
