import { type ReactNode, useCallback, useMemo, useState } from 'react'
import {
  ActionIcon,
  Badge,
  Button,
  Center,
  Checkbox,
  Drawer,
  Group,
  Loader,
  Modal,
  MultiSelect,
  Paper,
  Radio,
  RangeSlider,
  SegmentedControl,
  Select,
  SimpleGrid,
  Stack,
  Text,
  TextInput,
  Tooltip,
} from '@mantine/core'
import {
  IconBookmark,
  IconCircleCheck,
  IconClock,
  IconDeviceFloppy,
  IconDownload,
  IconEye,
  IconFileText,
  IconFilter,
  IconFolderSymlink,
  IconLayoutGrid,
  IconLayoutList,
  IconLibrary,
  IconListCheck,
  IconPhoto,
  IconPlus,
  IconRefresh,
  IconSearch,
  IconBell,
  IconSettings,
  IconTag,
  IconTrash,
  IconWand,
  IconX,
} from '@tabler/icons-react'
import { notifications } from '@mantine/notifications'
import {
  SERIES_NOTIFICATION_HELP,
  SERIES_NOTIFICATION_OPTIONS,
  type SeriesNotificationMode,
} from '../components/ui/seriesNotifications'
import { useDebouncedValue } from '@mantine/hooks'
import { useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { api } from '../api/client'
import {
  allowedContentRatings,
  CONTENT_RATING_LABELS,
  missingCount,
  useAutoMatchSources,
  useBulkSetSeriesNotificationMode,
  useBulkTag,
  useDeleteSavedFilter,
  useLibraryStats,
  useRootFolders,
  useSavedFilters,
  useSaveFilter,
  useSeries,
  useSources,
  useTags,
} from '../api/hooks'
import { useReadTracking } from '../api/reader'
import { useAuth } from '../auth/AuthProvider'
import type { LibraryFilterSpec, SeriesDto } from '../api/types'
import { CoverCard } from '../components/ui/CoverCard'
import { SeriesRow } from '../components/ui/SeriesRow'
import { EmptyState } from '../components/ui/EmptyState'
import { PageHeader } from '../components/ui/PageHeader'
import { StatTile } from '../components/ui/StatTile'
import { useWindowedRows, WINDOW_MIN_ITEMS } from '../components/ui/useWindowedRows'
import { TagManagerModal } from '../components/TagManagerModal'

const SORTS = [
  { value: 'added', label: 'Recently added' },
  { value: 'title', label: 'Title A–Z' },
  { value: 'incomplete', label: 'Most missing' },
  { value: 'status', label: 'Status' },
]

type ViewMode = 'grid' | 'list'
type Density = 'compact' | 'default' | 'comfortable'

const LS_VIEW = 'library-view'
const LS_DENSITY = 'library-density'

const DENSITY_OPTIONS = [
  { value: 'compact', label: 'Compact' },
  { value: 'default', label: 'Default' },
  { value: 'comfortable', label: 'Comfortable' },
]

const GRID_COLS: Record<Density, Record<string, number>> = {
  compact: { base: 3, xs: 4, sm: 5, md: 6, lg: 8, xl: 10 },
  default: { base: 2, xs: 3, sm: 4, md: 5, lg: 6, xl: 8 },
  comfortable: { base: 2, xs: 2, sm: 3, md: 4, lg: 5, xl: 6 },
}

function readStored<T extends string>(key: string, valid: readonly T[], fallback: T): T {
  try {
    const v = localStorage.getItem(key)
    return valid.includes(v as T) ? (v as T) : fallback
  } catch { return fallback }
}

function writeStored(key: string, value: string) {
  try { localStorage.setItem(key, value) } catch { /* noop */ }
}

/**
 * How much of the series has been read, 0–100. Kavita is the only source of read progress, so a
 * series it has never reported (`readChapterCount === null`) counts as 0% rather than being
 * dropped: the whole library would otherwise vanish the moment the slider left 0.
 */
function readPercent(s: SeriesDto): number {
  const total = s.chapterCount || s.knownChapterCount || 0
  if (total <= 0) return 0
  return Math.min(100, Math.round(((s.readChapterCount ?? 0) / total) * 100))
}

/** `any` = OR (carries at least one), `all` = AND (carries every one). */
function matches<T>(wanted: T[], has: T[] | undefined, mode: string): boolean {
  const owned = has ?? []
  return mode === 'all' ? wanted.every((w) => owned.includes(w)) : wanted.some((w) => owned.includes(w))
}

/**
 * Values present across the library, most-used first, as MultiSelect options with counts. `label`
 * renames a value for display only — source keys ("mangadex") get their registry display name, but
 * the option value stays the key the series carries.
 */
function facetOptions(
  series: SeriesDto[] | undefined,
  pick: (s: SeriesDto) => string[] | undefined,
  label: (value: string) => string = (v) => v,
) {
  const counts = new Map<string, number>()
  for (const s of series ?? []) {
    for (const value of pick(s) ?? []) counts.set(value, (counts.get(value) ?? 0) + 1)
  }
  return [...counts.entries()]
    .sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]))
    .map(([value, count]) => ({ value, label: `${label(value)} (${count})` }))
}

const DEFAULT_SPEC: LibraryFilterSpec = {
  query: '',
  status: 'all',
  tagIds: [],
  tagMatch: 'any',
  monitored: 'all',
  completeness: 'all',
  sort: 'added',
  genres: [],
  genreMatch: 'any',
  metadataTags: [],
  metadataTagMatch: 'any',
  readMin: 0,
  readMax: 100,
  contentRatings: [],
  sources: [],
  sourceMatch: 'any',
  sourceState: 'all',
  fileSources: [],
  fileSourceMatch: 'any',
}

const SOURCE_STATES = [
  { value: 'all', label: 'Any' },
  { value: 'none', label: 'No sources linked' },
  { value: 'hasDisabled', label: 'Has a disabled source' },
  { value: 'noneEnabled', label: 'Linked, but nothing enabled' },
  { value: 'hasEnabled', label: 'At least one enabled' },
]

/** True when the series matches one of {@link SOURCE_STATES}. `all` is filtered out before this. */
function matchesSourceState(s: SeriesDto, state: string): boolean {
  const linked = s.sources ?? []
  const enabled = s.enabledSources ?? []
  switch (state) {
    case 'none':
      return linked.length === 0
    case 'hasDisabled':
      return linked.length > enabled.length
    case 'noneEnabled':
      return linked.length > 0 && enabled.length === 0
    case 'hasEnabled':
      return enabled.length > 0
    default:
      return true
  }
}

const MATCH_MODES = [
  { value: 'any', label: 'Any' },
  { value: 'all', label: 'All' },
]

export default function LibraryPage() {
  const [viewMode, setViewMode] = useState<ViewMode>(() => readStored(LS_VIEW, ['grid', 'list'], 'grid'))
  const [density, setDensity] = useState<Density>(() => readStored(LS_DENSITY, ['compact', 'default', 'comfortable'], 'default'))
  const { data: series, isLoading, error } = useSeries()
  const { me } = useAuth()
  const { data: rootFolders } = useRootFolders()
  const { data: tags } = useTags()
  const { data: savedFilters } = useSavedFilters()
  const { data: sourceInfos } = useSources()
  const saveFilter = useSaveFilter()
  const deleteSavedFilter = useDeleteSavedFilter()
  const bulkTag = useBulkTag()
  const bulkNotifications = useBulkSetSeriesNotificationMode()
  const autoMatch = useAutoMatchSources()
  const readTracking = useReadTracking()
  const stats = useLibraryStats()
  const queryClient = useQueryClient()

  const [query, setQuery] = useState('')
  // Re-filtering (and re-sorting) a few thousand series on every keystroke is what made typing
  // in here feel sticky: the input itself stays instant, the grid catches up a frame later.
  const [debouncedQuery] = useDebouncedValue(query, 200)
  const [sort, setSort] = useState('added')
  const [statusFilter, setStatusFilter] = useState('all')
  // Tag ids live as strings because that's what MultiSelect speaks.
  const [tagFilter, setTagFilter] = useState<string[]>([])
  const [tagMatch, setTagMatch] = useState('any')
  const [genreFilter, setGenreFilter] = useState<string[]>([])
  const [genreMatch, setGenreMatch] = useState('any')
  const [metaTagFilter, setMetaTagFilter] = useState<string[]>([])
  const [metaTagMatch, setMetaTagMatch] = useState('any')
  const [contentRatingFilter, setContentRatingFilter] = useState<string[]>([])
  const [readRange, setReadRange] = useState<[number, number]>([0, 100])
  const [monitoredFilter, setMonitoredFilter] = useState('all')
  const [completeness, setCompleteness] = useState('all')
  const [sourceFilter, setSourceFilter] = useState<string[]>([])
  const [sourceMatch, setSourceMatch] = useState('any')
  const [sourceState, setSourceState] = useState('all')
  const [fileSourceFilter, setFileSourceFilter] = useState<string[]>([])
  const [fileSourceMatch, setFileSourceMatch] = useState('any')
  const [activeFilterId, setActiveFilterId] = useState<number | null>(null)
  const [saveFilterOpen, setSaveFilterOpen] = useState(false)
  const [filterName, setFilterName] = useState('')
  const [filtersOpen, setFiltersOpen] = useState(false)
  const [tagManagerOpen, setTagManagerOpen] = useState(false)
  const [tagModalOpen, setTagModalOpen] = useState(false)
  const [tagsToAdd, setTagsToAdd] = useState<string[]>([])
  const [tagsToRemove, setTagsToRemove] = useState<string[]>([])

  const [selectMode, setSelectMode] = useState(false)
  const [selected, setSelected] = useState<Set<number>>(new Set())
  const [busy, setBusy] = useState<string | null>(null)
  const [deleteModalOpen, setDeleteModalOpen] = useState(false)
  const [deleteFiles, setDeleteFiles] = useState(false)
  const [autoMatchModalOpen, setAutoMatchModalOpen] = useState(false)
  const [monitorModalOpen, setMonitorModalOpen] = useState(false)
  const [monitorMode, setMonitorMode] = useState('All')
  const [notifyModalOpen, setNotifyModalOpen] = useState(false)
  const [notifyMode, setNotifyMode] = useState<SeriesNotificationMode>('Default')
  const [moveModalOpen, setMoveModalOpen] = useState(false)
  const [moveTarget, setMoveTarget] = useState<string | null>(null)
  const [moveFiles, setMoveFiles] = useState(true)

  const visible = useMemo(() => {
    let list = [...(series ?? [])]
    const q = debouncedQuery.trim().toLowerCase()
    if (q) {
      list = list.filter(
        (s) =>
          s.title.toLowerCase().includes(q) ||
          (s.originalTitle?.toLowerCase().includes(q) ?? false),
      )
    }
    if (statusFilter !== 'all') list = list.filter((s) => s.status === statusFilter)
    if (tagFilter.length > 0) {
      const wanted = tagFilter.map(Number)
      list = list.filter((s) => matches(wanted, s.tagIds, tagMatch))
    }
    if (genreFilter.length > 0) {
      list = list.filter((s) => matches(genreFilter, s.genres, genreMatch))
    }
    if (metaTagFilter.length > 0) {
      list = list.filter((s) => matches(metaTagFilter, s.metadataTags, metaTagMatch))
    }
    if (contentRatingFilter.length > 0) {
      // A series not yet refreshed since the column was added has no rating to check against —
      // excluded rather than assumed safe, since this filter exists to gate content.
      list = list.filter((s) => s.contentRating != null && contentRatingFilter.includes(s.contentRating))
    }
    if (monitoredFilter !== 'all') {
      list = list.filter((s) => s.monitored === (monitoredFilter === 'monitored'))
    }
    if (completeness !== 'all') {
      list = list.filter((s) => (completeness === 'behind' ? missingCount(s) > 0 : missingCount(s) <= 0))
    }
    if (sourceState !== 'all') list = list.filter((s) => matchesSourceState(s, sourceState))
    if (sourceFilter.length > 0) {
      list = list.filter((s) => matches(sourceFilter, s.sources, sourceMatch))
    }
    if (fileSourceFilter.length > 0) {
      list = list.filter((s) => matches(fileSourceFilter, s.fileSources, fileSourceMatch))
    }
    if (readRange[0] > 0 || readRange[1] < 100) {
      list = list.filter((s) => {
        const pct = readPercent(s)
        return pct >= readRange[0] && pct <= readRange[1]
      })
    }
    list.sort((a, b) => {
      switch (sort) {
        case 'title':
          return a.sortTitle.localeCompare(b.sortTitle)
        case 'incomplete':
          return missingCount(b) - missingCount(a)
        case 'status':
          return a.status.localeCompare(b.status)
        default:
          return new Date(b.added).getTime() - new Date(a.added).getTime()
      }
    })
    return list
  }, [
    series, debouncedQuery, statusFilter, tagFilter, tagMatch, genreFilter, genreMatch,
    metaTagFilter, metaTagMatch, monitoredFilter, completeness, readRange, sort, contentRatingFilter,
    sourceFilter, sourceMatch, sourceState, fileSourceFilter, fileSourceMatch,
  ])

  const statusOptions = useMemo(() => {
    const set = new Set((series ?? []).map((s) => s.status))
    return ['all', ...[...set].sort()]
  }, [series])

  const tagOptions = useMemo(
    () => (tags ?? []).map((t) => ({ value: String(t.id), label: `${t.label} (${t.seriesCount})` })),
    [tags],
  )

  const genreOptions = useMemo(() => facetOptions(series, (s) => s.genres), [series])
  const metaTagOptions = useMemo(() => facetOptions(series, (s) => s.metadataTags), [series])

  // Faceted off the library rather than the source registry, so a source that was dropped from the
  // build still appears while series and files are pointing at it — falling back to the raw key.
  const sourceLabel = useCallback(
    (name: string) => (sourceInfos ?? []).find((s) => s.name === name)?.displayName ?? name,
    [sourceInfos],
  )
  const sourceOptions = useMemo(
    () => facetOptions(series, (s) => s.sources, sourceLabel),
    [series, sourceLabel],
  )
  const fileSourceOptions = useMemo(
    () => facetOptions(series, (s) => s.fileSources, sourceLabel),
    [series, sourceLabel],
  )
  // Gated by the signed-in user's own ceiling: picking a rating they can't see would just come
  // back empty, and the option shouldn't be offered in the first place.
  const contentRatingOptions = useMemo(
    () =>
      allowedContentRatings(me?.maxContentRating).map((value) => ({
        value,
        label: CONTENT_RATING_LABELS[value],
      })),
    [me?.maxContentRating],
  )

  const currentSpec = (): LibraryFilterSpec => ({
    query,
    status: statusFilter,
    tagIds: tagFilter.map(Number),
    tagMatch,
    monitored: monitoredFilter,
    completeness,
    sort,
    genres: genreFilter,
    genreMatch,
    metadataTags: metaTagFilter,
    metadataTagMatch: metaTagMatch,
    readMin: readRange[0],
    readMax: readRange[1],
    contentRatings: contentRatingFilter,
    sources: sourceFilter,
    sourceMatch,
    sourceState,
    fileSources: fileSourceFilter,
    fileSourceMatch,
  })

  const applySpec = (spec: LibraryFilterSpec, id: number | null) => {
    // Spread over the defaults so a preset saved by an older build (no genres, no read range)
    // still applies cleanly instead of writing undefined into the filter state.
    const merged = { ...DEFAULT_SPEC, ...spec }
    setQuery(merged.query ?? '')
    setStatusFilter(merged.status)
    setTagFilter((merged.tagIds ?? []).map(String))
    setTagMatch(merged.tagMatch)
    setGenreFilter(merged.genres ?? [])
    setGenreMatch(merged.genreMatch)
    setMetaTagFilter(merged.metadataTags ?? [])
    setMetaTagMatch(merged.metadataTagMatch)
    setReadRange([merged.readMin, merged.readMax])
    // Clamped in case a preset was saved before the user's ceiling was lowered.
    const allowed: string[] = allowedContentRatings(me?.maxContentRating)
    setContentRatingFilter((merged.contentRatings ?? []).filter((r) => allowed.includes(r)))
    setMonitoredFilter(merged.monitored)
    setCompleteness(merged.completeness)
    setSourceFilter(merged.sources ?? [])
    setSourceMatch(merged.sourceMatch)
    setSourceState(merged.sourceState)
    setFileSourceFilter(merged.fileSources ?? [])
    setFileSourceMatch(merged.fileSourceMatch)
    setSort(merged.sort)
    setActiveFilterId(id)
  }

  /** Everything except the search box: what the "Filters" button badges. */
  const activeFilterCount =
    (statusFilter !== 'all' ? 1 : 0) +
    (tagFilter.length > 0 ? 1 : 0) +
    (genreFilter.length > 0 ? 1 : 0) +
    (metaTagFilter.length > 0 ? 1 : 0) +
    (monitoredFilter !== 'all' ? 1 : 0) +
    (completeness !== 'all' ? 1 : 0) +
    (readRange[0] > 0 || readRange[1] < 100 ? 1 : 0) +
    (contentRatingFilter.length > 0 ? 1 : 0) +
    (sourceFilter.length > 0 ? 1 : 0) +
    (sourceState !== 'all' ? 1 : 0) +
    (fileSourceFilter.length > 0 ? 1 : 0)

  const filtersActive = query.trim() !== '' || activeFilterCount > 0

  // Stable across renders so the memoized CoverCards aren't invalidated by a fresh closure,
  // which is why the card takes the id as an argument rather than closing over it.
  const toggle = useCallback(
    (id: number) =>
      setSelected((s) => {
        const next = new Set(s)
        if (next.has(id)) next.delete(id)
        else next.add(id)
        return next
      }),
    [],
  )

  const exitSelectMode = () => {
    setSelectMode(false)
    setSelected(new Set())
  }

  /** Runs an action against every selected series sequentially with a live progress notification. */
  const runBulk = async (action: string, fn: (id: number) => Promise<unknown>) => {
    const ids = [...selected]
    setBusy(action)
    notifications.show({
      id: 'bulk-action',
      loading: true,
      message: `${action}: 0/${ids.length}`,
      autoClose: false,
      withCloseButton: false,
    })
    let ok = 0
    const errors: string[] = []
    for (const id of ids) {
      try {
        await fn(id)
        ok++
      } catch (err) {
        errors.push(String(err))
      }
      notifications.update({
        id: 'bulk-action',
        loading: true,
        message: `${action}: ${ok + errors.length}/${ids.length}`,
        autoClose: false,
        withCloseButton: false,
      })
    }
    notifications.update({
      id: 'bulk-action',
      loading: false,
      color: errors.length ? 'yellow' : 'green',
      message: `${action}: ${ok}/${ids.length} succeeded${errors.length ? ` - first error: ${errors[0]}` : ''}`,
      autoClose: 8000,
      withCloseButton: true,
    })
    setBusy(null)
    void queryClient.invalidateQueries({ queryKey: ['series'] })
    void queryClient.invalidateQueries({ queryKey: ['chapters'] })
  }

  /**
   * A multi-value facet plus its AND/OR switch. The switch only appears once two values are
   * picked: with one selected, "any" and "all" mean the same thing and it's just noise.
   */
  const facetFilter = ({
    label,
    description,
    data,
    value,
    onChange,
    mode,
    onModeChange,
  }: {
    label: string
    description?: string
    data: { value: string; label: string }[]
    value: string[]
    onChange: (v: string[]) => void
    mode: string
    onModeChange: (v: string) => void
  }) => (
    <div>
      <Group justify="space-between" align="center" mb={4} wrap="nowrap">
        <Text size="sm" fw={500}>
          {label}
        </Text>
        {value.length > 1 && (
          <SegmentedControl size="xs" value={mode} onChange={onModeChange} data={MATCH_MODES} />
        )}
      </Group>
      {description && (
        <Text size="xs" c="dimmed" mb={4}>
          {description}
        </Text>
      )}
      <MultiSelect
        data={data}
        value={value}
        onChange={onChange}
        placeholder={value.length === 0 ? (data.length > 0 ? 'Any' : 'None available') : undefined}
        disabled={data.length === 0}
        searchable
        clearable
        // The metadata-tag list runs to a few thousand entries on a big library; rendering them
        // all makes opening the dropdown visibly janky, and search narrows it anyway.
        limit={100}
        comboboxProps={{ withinPortal: true }}
      />
    </div>
  )

  const bulkBtn = (label: string, icon: ReactNode, run: () => void, color?: string) => (
    <Button
      size="xs"
      variant="light"
      color={color}
      leftSection={icon}
      disabled={selected.size === 0 || (busy !== null && busy !== label)}
      loading={busy === label}
      onClick={run}
    >
      {label}
    </Button>
  )

  // Against the *filtered* set, not the whole library: "select all" under an active filter that
  // silently grabbed hidden series would make every bulk action a foot-gun.
  const allSelected = selected.size > 0 && selected.size === visible.length

  // One hook serves both views: only one of the two wrappers is mounted at a time, and the ref
  // re-subscribes when the other takes over.
  const windowed = useWindowedRows(visible.length, visible.length >= WINDOW_MIN_ITEMS)

  return (
    <>
      <PageHeader
        title="Library"
        description="Every series Maki watches: cover art, download progress and status at a glance."
        actions={
          series && series.length > 0 && !selectMode ? (
            <>
              <Button.Group>
                <Button
                  variant={viewMode === 'grid' ? 'filled' : 'default'}
                  size="sm"
                  onClick={() => {
                    setViewMode('grid')
                    writeStored(LS_VIEW, 'grid')
                  }}
                  aria-label="Grid view"
                >
                  <IconLayoutGrid size={16} />
                </Button>
                <Button
                  variant={viewMode === 'list' ? 'filled' : 'default'}
                  size="sm"
                  onClick={() => {
                    setViewMode('list')
                    writeStored(LS_VIEW, 'list')
                  }}
                  aria-label="List view"
                >
                  <IconLayoutList size={16} />
                </Button>
              </Button.Group>
              <SegmentedControl
                size="sm"
                value={density}
                onChange={(v) => {
                  setDensity(v as Density)
                  writeStored(LS_DENSITY, v)
                }}
                data={DENSITY_OPTIONS}
              />
              <Button
                variant="default"
                leftSection={<IconListCheck size={16} />}
                onClick={() => setSelectMode(true)}
              >
                Select
              </Button>
              <Button component={Link} to="/add" leftSection={<IconPlus size={16} />}>
                Add series
              </Button>
            </>
          ) : undefined
        }
      />

      {series && series.length > 0 && (
        <SimpleGrid cols={{ base: 2, sm: stats.inQueue > 0 ? 5 : 4 }} spacing="sm" mb="lg">
          <StatTile label="Series" value={stats.total} icon={IconLibrary} accent="brand" />
          <StatTile label="Monitored" value={stats.monitored} icon={IconEye} accent="info" />
          <StatTile label="On disk" value={stats.downloaded} icon={IconCircleCheck} accent="ok" />
          <StatTile label="Missing" value={stats.missing} icon={IconDownload} accent="warn" />
          {stats.inQueue > 0 && (
            <StatTile label="In queue" value={stats.inQueue} icon={IconClock} accent="brand" />
          )}
        </SimpleGrid>
      )}

      {/* Toolbar / selection bar */}
      {series && series.length > 0 &&
        (selectMode ? (
          <Paper withBorder p="xs" mb="lg" radius="lg">
            <Group justify="space-between" wrap="wrap" gap="xs">
              <Group gap="xs">
                <Text size="sm" c="dimmed" className="tnum">
                  {selected.size} selected
                </Text>
                <Button
                  size="xs"
                  variant="subtle"
                  onClick={() =>
                    setSelected(allSelected ? new Set() : new Set(visible.map((s) => s.id)))
                  }
                >
                  {allSelected ? 'Clear all' : filtersActive ? 'Select filtered' : 'Select all'}
                </Button>
                <Text size="xs" c="dimmed" className="tnum">
                  {filtersActive
                    ? `${visible.length.toLocaleString()} of ${stats.total.toLocaleString()} series match`
                    : `${stats.total.toLocaleString()} series`}
                </Text>
              </Group>
              <Group gap="xs">
                {bulkBtn('Search missing', <IconSearch size={15} />, () =>
                  runBulk('Search missing', (id) =>
                    api(`/series/${id}/searchmissing`, { method: 'POST' }),
                  ),
                )}
                {bulkBtn('Refresh', <IconRefresh size={15} />, () =>
                  runBulk('Refresh', (id) => api(`/series/${id}/refresh`, { method: 'POST' })),
                )}
                {bulkBtn('Auto-match', <IconWand size={15} />, () => setAutoMatchModalOpen(true))}
                {bulkBtn('Metadata', <IconPhoto size={15} />, () =>
                  runBulk('Metadata', (id) =>
                    api(`/series/${id}/refreshmetadata`, { method: 'POST' }),
                  ),
                )}
                {bulkBtn('ComicInfo', <IconFileText size={15} />, () =>
                  runBulk('ComicInfo', (id) =>
                    api(`/series/${id}/updatecomicinfo`, { method: 'POST' }),
                  ),
                )}
                {bulkBtn('Tags', <IconTag size={15} />, () => {
                  setTagsToAdd([])
                  setTagsToRemove([])
                  setTagModalOpen(true)
                })}
                {bulkBtn('Monitoring', <IconEye size={15} />, () => setMonitorModalOpen(true))}
                {bulkBtn('Notifications', <IconBell size={15} />, () => setNotifyModalOpen(true))}
                {bulkBtn('Move', <IconFolderSymlink size={15} />, () => {
                  setMoveTarget(null)
                  setMoveFiles(true)
                  setMoveModalOpen(true)
                })}
                {bulkBtn('Delete', <IconTrash size={15} />, () => setDeleteModalOpen(true), 'red')}
                <Button
                  size="xs"
                  variant="default"
                  leftSection={<IconX size={15} />}
                  disabled={busy !== null}
                  onClick={exitSelectMode}
                >
                  Done
                </Button>
              </Group>
            </Group>
          </Paper>
        ) : (
          <Stack mb="lg" gap="sm">
            <Group gap="sm" wrap="wrap">
              <TextInput
                placeholder="Filter library…"
                leftSection={<IconSearch size={16} />}
                value={query}
                onChange={(e) => setQuery(e.currentTarget.value)}
                style={{ flex: '1 1 240px' }}
              />
              <Button
                variant={activeFilterCount > 0 ? 'light' : 'default'}
                leftSection={<IconFilter size={16} />}
                rightSection={
                  activeFilterCount > 0 ? (
                    <Badge size="xs" circle variant="filled">
                      {activeFilterCount}
                    </Badge>
                  ) : undefined
                }
                onClick={() => setFiltersOpen(true)}
              >
                Filters
              </Button>
              <Select
                data={SORTS}
                value={sort}
                onChange={(v) => setSort(v ?? 'added')}
                w={170}
                comboboxProps={{ withinPortal: true }}
              />
              <Text size="sm" c="dimmed" className="tnum">
                {filtersActive
                  ? `${visible.length.toLocaleString()} of ${stats.total.toLocaleString()} series match`
                  : `${stats.total.toLocaleString()} series`}
              </Text>
            </Group>

            <Group gap="xs" wrap="wrap">
              {(savedFilters ?? []).map((f) => (
                <Badge
                  key={f.id}
                  variant={activeFilterId === f.id ? 'filled' : 'light'}
                  color={activeFilterId === f.id ? 'brand' : 'gray'}
                  leftSection={<IconBookmark size={11} />}
                  rightSection={
                    <IconX
                      size={11}
                      style={{ cursor: 'pointer' }}
                      onClick={(e) => {
                        e.stopPropagation()
                        deleteSavedFilter.mutate(f.id)
                        if (activeFilterId === f.id) setActiveFilterId(null)
                      }}
                    />
                  }
                  style={{ cursor: 'pointer' }}
                  onClick={() => applySpec(f.spec, f.id)}
                >
                  {f.name}
                </Badge>
              ))}
              {filtersActive && (
                <Button
                  size="compact-xs"
                  variant="subtle"
                  leftSection={<IconDeviceFloppy size={14} />}
                  onClick={() => {
                    const active = (savedFilters ?? []).find((f) => f.id === activeFilterId)
                    setFilterName(active?.name ?? '')
                    setSaveFilterOpen(true)
                  }}
                >
                  Save filter
                </Button>
              )}
              {filtersActive && (
                <Button
                  size="compact-xs"
                  variant="subtle"
                  color="gray"
                  leftSection={<IconX size={14} />}
                  onClick={() => applySpec(DEFAULT_SPEC, null)}
                >
                  Clear
                </Button>
              )}
              <Tooltip label="Manage tags" withArrow>
                <ActionIcon
                  variant="subtle"
                  color="gray"
                  onClick={() => setTagManagerOpen(true)}
                  aria-label="Manage tags"
                >
                  <IconSettings size={16} />
                </ActionIcon>
              </Tooltip>
            </Group>
          </Stack>
        ))}

      <Drawer
        opened={filtersOpen}
        onClose={() => setFiltersOpen(false)}
        position="right"
        size="sm"
        title="Filters"
      >
        <Stack gap="sm" pb="xl">
          <Text size="sm" c="dimmed">
            {visible.length} of {series?.length ?? 0} series shown. Changes apply straight to the grid
            behind this panel.
          </Text>
          <Select
            label="Status"
            data={statusOptions.map((s) => ({
              value: s,
              label: s === 'all' ? 'All statuses' : s,
            }))}
            value={statusFilter}
            onChange={(v) => setStatusFilter(v ?? 'all')}
            comboboxProps={{ withinPortal: true }}
          />
          {facetFilter({
            label: 'Your tags',
            data: tagOptions,
            value: tagFilter,
            onChange: setTagFilter,
            mode: tagMatch,
            onModeChange: setTagMatch,
          })}
          {facetFilter({
            label: 'Genres',
            data: genreOptions,
            value: genreFilter,
            onChange: setGenreFilter,
            mode: genreMatch,
            onModeChange: setGenreMatch,
          })}
          {facetFilter({
            label: 'Metadata tags',
            description: 'From the metadata provider, not your own tags',
            data: metaTagOptions,
            value: metaTagFilter,
            onChange: setMetaTagFilter,
            mode: metaTagMatch,
            onModeChange: setMetaTagMatch,
          })}
          <MultiSelect
            label="Content rating"
            placeholder={contentRatingFilter.length ? undefined : 'Any'}
            data={contentRatingOptions}
            value={contentRatingFilter}
            onChange={setContentRatingFilter}
            clearable
            comboboxProps={{ withinPortal: true }}
          />
          <Select
            label="Monitoring"
            data={[
              { value: 'all', label: 'Any' },
              { value: 'monitored', label: 'Monitored' },
              { value: 'unmonitored', label: 'Unmonitored' },
            ]}
            value={monitoredFilter}
            onChange={(v) => setMonitoredFilter(v ?? 'all')}
            comboboxProps={{ withinPortal: true }}
          />
          <Select
            label="Completeness"
            data={[
              { value: 'all', label: 'Any' },
              { value: 'behind', label: 'Behind (missing chapters)' },
              { value: 'complete', label: 'Complete' },
            ]}
            value={completeness}
            onChange={(v) => setCompleteness(v ?? 'all')}
            comboboxProps={{ withinPortal: true }}
          />
          <Select
            label="Source state"
            description="Counts both switches: the per-series link and the global source toggle"
            data={SOURCE_STATES}
            value={sourceState}
            onChange={(v) => setSourceState(v ?? 'all')}
            comboboxProps={{ withinPortal: true }}
          />
          {facetFilter({
            label: 'Sources',
            description: 'Linked to the series, enabled or not',
            data: sourceOptions,
            value: sourceFilter,
            onChange: setSourceFilter,
            mode: sourceMatch,
            onModeChange: setSourceMatch,
          })}
          {facetFilter({
            label: 'Downloaded from',
            description: 'Where the files on disk came from, which can outlive the link',
            data: fileSourceOptions,
            value: fileSourceFilter,
            onChange: setFileSourceFilter,
            mode: fileSourceMatch,
            onModeChange: setFileSourceMatch,
          })}
          {readTracking && (
            <div>
              <Text size="sm" fw={500} mb={2}>
                Read
              </Text>
              <Text size="xs" c="dimmed" mb="md">
                Share of the series you've read. Leave at 0–100% to ignore.
              </Text>
              <RangeSlider
                min={0}
                max={100}
                step={5}
                minRange={0}
                value={readRange}
                onChange={setReadRange}
                marks={[
                  { value: 0, label: '0%' },
                  { value: 50, label: '50%' },
                  { value: 100, label: '100%' },
                ]}
                mb="lg"
              />
            </div>
          )}
          {activeFilterCount > 0 && (
            <Button variant="default" leftSection={<IconX size={15} />} onClick={() => applySpec(DEFAULT_SPEC, null)}>
              Clear all filters
            </Button>
          )}
        </Stack>
      </Drawer>

      <TagManagerModal opened={tagManagerOpen} onClose={() => setTagManagerOpen(false)} />

      <Modal
        opened={saveFilterOpen}
        onClose={() => setSaveFilterOpen(false)}
        title="Save this filter"
      >
        <Stack gap="md">
          <Text size="sm" c="dimmed">
            Saves the current search, sort and every filter in the panel as a named preset. Reusing
            the name of the active preset overwrites it.
          </Text>
          <TextInput
            label="Name"
            placeholder="e.g. Ongoing & behind"
            value={filterName}
            onChange={(e) => setFilterName(e.currentTarget.value)}
            data-autofocus
          />
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setSaveFilterOpen(false)}>
              Cancel
            </Button>
            <Button
              disabled={!filterName.trim()}
              loading={saveFilter.isPending}
              onClick={() => {
                const active = (savedFilters ?? []).find((f) => f.id === activeFilterId)
                const overwrite = active && active.name === filterName.trim()
                saveFilter.mutate(
                  { id: overwrite ? active.id : undefined, name: filterName.trim(), spec: currentSpec() },
                  {
                    onSuccess: (saved) => {
                      setActiveFilterId(saved.id)
                      setSaveFilterOpen(false)
                    },
                    onError: (err) =>
                      notifications.show({ color: 'red', message: `Failed to save filter: ${String(err)}` }),
                  },
                )
              }}
            >
              Save
            </Button>
          </Group>
        </Stack>
      </Modal>

      <Modal
        opened={tagModalOpen}
        onClose={() => setTagModalOpen(false)}
        title={`Tag ${selected.size} series`}
      >
        <Stack gap="md">
          <Text size="sm" c="dimmed">
            Adds and removes run in one pass over the selection. Create new tags from "Manage tags".
          </Text>
          <MultiSelect
            label="Add"
            data={tagOptions}
            value={tagsToAdd}
            onChange={setTagsToAdd}
            searchable
            clearable
            comboboxProps={{ withinPortal: true }}
          />
          <MultiSelect
            label="Remove"
            data={tagOptions}
            value={tagsToRemove}
            onChange={setTagsToRemove}
            searchable
            clearable
            comboboxProps={{ withinPortal: true }}
          />
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setTagModalOpen(false)}>
              Cancel
            </Button>
            <Button
              disabled={tagsToAdd.length === 0 && tagsToRemove.length === 0}
              loading={bulkTag.isPending}
              onClick={() =>
                bulkTag.mutate(
                  {
                    seriesIds: [...selected],
                    add: tagsToAdd.map(Number),
                    remove: tagsToRemove.map(Number),
                  },
                  {
                    onSuccess: ({ updated }) => {
                      setTagModalOpen(false)
                      notifications.show({ color: 'green', message: `Tagged ${updated} series` })
                    },
                    onError: (err) =>
                      notifications.show({ color: 'red', message: `Failed to tag: ${String(err)}` }),
                  },
                )
              }
            >
              Apply
            </Button>
          </Group>
        </Stack>
      </Modal>

      <Modal
        opened={autoMatchModalOpen}
        onClose={() => setAutoMatchModalOpen(false)}
        title={`Auto-match sources for ${selected.size} series`}
      >
        <Text size="sm" mb="md">
          Every source that isn't linked yet is searched again for each series, which is worth doing
          when a source has picked a title up since you added it. Sources already linked are left
          exactly as they are, so this only ever adds.
        </Text>
        <Text size="sm" c="dimmed" mb="lg">
          Matching runs in the background, one series at a time, to keep the request rate at the
          sites sane. A large selection can take a while.
        </Text>
        <Group justify="flex-end">
          <Button variant="default" onClick={() => setAutoMatchModalOpen(false)}>
            Cancel
          </Button>
          <Button
            leftSection={<IconWand size={16} />}
            loading={autoMatch.isPending}
            onClick={() =>
              autoMatch.mutate([...selected], {
                onSuccess: (r) => {
                  setAutoMatchModalOpen(false)
                  exitSelectMode()
                  notifications.show({
                    color: r.queued > 0 ? 'green' : undefined,
                    message:
                      r.queued > 0
                        ? `Auto-matching ${r.queued} series in the background.`
                        : 'Those series are already being matched.',
                  })
                },
              })
            }
          >
            Auto-match
          </Button>
        </Group>
      </Modal>

      <Modal
        opened={deleteModalOpen}
        onClose={() => setDeleteModalOpen(false)}
        title={`Delete ${selected.size} series?`}
      >
        <Text size="sm" mb="md">
          The selected series will be removed from Maki and stop being monitored.
        </Text>
        <Checkbox
          label="Also delete the folders and files on disk"
          checked={deleteFiles}
          onChange={(e) => setDeleteFiles(e.currentTarget.checked)}
          mb="lg"
        />
        <Group justify="flex-end">
          <Button variant="default" onClick={() => setDeleteModalOpen(false)}>
            Cancel
          </Button>
          <Button
            color="red"
            leftSection={<IconTrash size={16} />}
            onClick={() => {
              setDeleteModalOpen(false)
              void runBulk('Delete', (id) =>
                api(`/series/${id}?deleteFiles=${deleteFiles}`, { method: 'DELETE' }),
              ).then(exitSelectMode)
            }}
          >
            Delete
          </Button>
        </Group>
      </Modal>

      <Modal
        opened={monitorModalOpen}
        onClose={() => setMonitorModalOpen(false)}
        title={`Set monitoring for ${selected.size} series`}
      >
        <Text size="sm" mb="md">
          Applies to every existing chapter and to chapters released later. "Main" skips specials
          (decimal chapters like 10.5).
        </Text>
        <SegmentedControl
          fullWidth
          value={monitorMode}
          onChange={setMonitorMode}
          data={[
            { value: 'All', label: 'All chapters' },
            { value: 'MainOnly', label: 'Main (no specials)' },
            { value: 'None', label: 'None' },
          ]}
          mb="lg"
        />
        <Group justify="flex-end">
          <Button variant="default" onClick={() => setMonitorModalOpen(false)}>
            Cancel
          </Button>
          <Button
            onClick={() => {
              setMonitorModalOpen(false)
              void runBulk('Set monitoring', (id) =>
                api(`/series/${id}/monitormode`, {
                  method: 'POST',
                  body: JSON.stringify({ mode: monitorMode }),
                }),
              )
            }}
          >
            Apply
          </Button>
        </Group>
      </Modal>

      <Modal
        opened={notifyModalOpen}
        onClose={() => setNotifyModalOpen(false)}
        title={`Set notifications for ${selected.size} series`}
      >
        <Text size="sm" mb="md">
          Yours alone - this changes what lands in your bell, not anybody else's.
        </Text>
        <SegmentedControl
          fullWidth
          value={notifyMode}
          onChange={(v) => setNotifyMode(v as SeriesNotificationMode)}
          data={SERIES_NOTIFICATION_OPTIONS}
          mb="xs"
        />
        <Text size="xs" c="dimmed" mb="lg">
          {SERIES_NOTIFICATION_HELP[notifyMode]}
        </Text>
        <Group justify="flex-end">
          <Button variant="default" onClick={() => setNotifyModalOpen(false)}>
            Cancel
          </Button>
          <Button
            loading={bulkNotifications.isPending}
            onClick={() =>
              bulkNotifications.mutate(
                { seriesIds: [...selected], mode: notifyMode },
                {
                  onSuccess: ({ updated }) => {
                    setNotifyModalOpen(false)
                    notifications.show({
                      color: 'green',
                      message: `Notifications updated for ${updated} series`,
                    })
                  },
                  onError: (err) =>
                    notifications.show({
                      color: 'red',
                      message: `Failed to update notifications: ${String(err)}`,
                    }),
                },
              )
            }
          >
            Apply
          </Button>
        </Group>
      </Modal>

      <Modal
        opened={moveModalOpen}
        onClose={() => setMoveModalOpen(false)}
        title={`Move ${selected.size} series`}
      >
        <Stack gap="md">
          <Text size="sm" c="dimmed">
            Re-triggers a Kavita scan of both locations either way. Series already in the
            destination root folder are skipped. A file move is blocked for any series with an
            active download.
          </Text>
          <Select
            label="Destination root folder"
            placeholder="Pick a root folder"
            data={(rootFolders ?? []).map((f) => ({ value: String(f.id), label: f.path }))}
            value={moveTarget}
            onChange={setMoveTarget}
            comboboxProps={{ withinPortal: true }}
          />
          <Radio.Group
            label="Files"
            value={moveFiles ? 'move' : 'already-moved'}
            onChange={(v) => setMoveFiles(v === 'move')}
          >
            <Stack gap={6} mt={6}>
              <Radio value="move" label="Move the files on disk to the new root folder" />
              <Radio
                value="already-moved"
                label="Just point the series at the new root folder, I already moved the files"
              />
            </Stack>
          </Radio.Group>
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setMoveModalOpen(false)}>
              Cancel
            </Button>
            <Button
              disabled={!moveTarget}
              onClick={() => {
                if (!moveTarget) return
                setMoveModalOpen(false)
                void runBulk('Move', (id) =>
                  api(`/series/${id}/move`, {
                    method: 'POST',
                    body: JSON.stringify({ rootFolderId: Number(moveTarget), moveFiles }),
                  }),
                )
              }}
            >
              Move
            </Button>
          </Group>
        </Stack>
      </Modal>

      {isLoading && (
        <Center py={80}>
          <Loader />
        </Center>
      )}
      {error && (
        <Text c="red" ta="center" py="xl">
          Failed to load library: {String(error)}
        </Text>
      )}
      {series && series.length === 0 && (
        <EmptyState
          icon={IconLibrary}
          title="Your library is empty"
          description="Search MangaBaka and add your first series. Maki will monitor for new chapters and download them automatically."
          actionLabel="Add a series"
          actionTo="/add"
        />
      )}
      {series && series.length > 0 && visible.length === 0 && (
        <EmptyState
          icon={IconSearch}
          title="No matches"
          description="No series match the current filter. Try clearing the search or status filter."
        />
      )}
      {/* Both views render a slice, not the whole filtered set, once the library is big enough to
          be worth it (see useWindowedRows for the threshold and what it costs). Bulk selection is
          unaffected: "select filtered" works off `visible`, never off what is mounted. */}
      {visible.length > 0 && viewMode === 'grid' && (
        <div ref={windowed.outerRef} style={{ paddingTop: windowed.padTop, paddingBottom: windowed.padBottom }}>
          <SimpleGrid ref={windowed.innerRef} cols={GRID_COLS[density]} spacing="md">
            {visible.slice(windowed.start, windowed.end).map((s) => (
              <CoverCard
                key={s.id}
                series={s}
                selectMode={selectMode}
                selected={selected.has(s.id)}
                readTracking={readTracking}
                onToggle={toggle}
              />
            ))}
          </SimpleGrid>
        </div>
      )}
      {visible.length > 0 && viewMode === 'list' && (
        <div ref={windowed.outerRef} style={{ paddingTop: windowed.padTop, paddingBottom: windowed.padBottom }}>
          <Stack ref={windowed.innerRef} gap="xs">
            {visible.slice(windowed.start, windowed.end).map((s) => (
              <SeriesRow
                key={s.id}
                series={s}
                selectMode={selectMode}
                selected={selected.has(s.id)}
                readTracking={readTracking}
                density={density}
                onToggle={toggle}
              />
            ))}
          </Stack>
        </div>
      )}
    </>
  )
}
