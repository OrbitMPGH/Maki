import { type ReactNode, useCallback, useEffect, useMemo, useState } from 'react'
import { usePageState } from '../lib/pageState'
import {
  ActionIcon,
  Badge,
  Button,
  Checkbox,
  Drawer,
  Group,
  Indicator,
  Modal,
  MultiSelect,
  NumberInput,
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
  IconDeviceFloppy,
  IconEye,
  IconFileText,
  IconFilter,
  IconFolderSymlink,
  IconLayoutGrid,
  IconLayoutList,
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
  useSeriesNotificationHelp,
  useSeriesNotificationOptions,
  type SeriesNotificationMode,
} from '../components/ui/seriesNotifications'
import { useDebouncedValue } from '@mantine/hooks'
import { useQueryClient } from '@tanstack/react-query'
import { Link, useNavigate } from 'react-router-dom'
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
  useTags,
} from '../api/hooks'
import { useReadTracking } from '../api/reader'
import { useAuth } from '../auth/AuthProvider'
import { useLabel } from '../i18n-context'
import { useSourceLabel } from '../sourceLabels'
import { useLingui } from '@lingui/react'
import { Trans, Plural, useLingui as useLinguiMacro } from '@lingui/react/macro'
import { msg, plural, t as now } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import type { LibraryFilterSpec, SeriesDto } from '../api/types'
import { PosterSkeletons } from '../components/CatalogueBrowser'
import { CoverCard } from '../components/ui/CoverCard'
import { SeriesRow } from '../components/ui/SeriesRow'
import { EmptyState } from '../components/ui/EmptyState'
import { PageHeader } from '../components/ui/PageHeader'
import { Panel } from '../components/ui/Panel'
import { FigureStrip } from '../components/ui/FigureStrip'
import { TagChip } from '../components/ui/TagChip'
import { SurfaceFrame } from '../components/ui/SurfaceFrame'
import { LuckyButton } from '../components/LuckyButton'
import { isUnfinished } from '../lib/lucky'
import { useWindowedRows, WINDOW_MIN_ITEMS } from '../components/ui/useWindowedRows'
import { TagManagerModal } from '../components/TagManagerModal'
import { POSTER_COLS_BY_DENSITY, useDensityOptions } from '../components/ui/viewPrefs'
import { formatNumber } from '../format'

const SORT_VALUES = ['added', 'title', 'incomplete', 'status'] as const

const SORT_LABELS: Record<string, MessageDescriptor> = {
  added: msg`Recently added`,
  title: msg`Title A–Z`,
  incomplete: msg`Most missing`,
  status: msg`Status`,
}

type ViewMode = 'grid' | 'list'
type Density = 'compact' | 'default' | 'comfortable'

/** Where the library's filters are remembered between visits, see usePageState. */
const MEM = 'library'

const LS_VIEW = 'library-view'
const LS_DENSITY = 'library-density'

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
  const total = s.wantedChapterCount || s.knownChapterCount || 0
  if (total <= 0) return 0
  return Math.min(100, Math.round(((s.readChapterCount ?? 0) / total) * 100))
}

/**
 * An empty (or half-typed, e.g. a lone "-") NumberInput means "unbounded", not zero: the boxes
 * default to empty, and a 0 lower bound would silently filter nothing while looking like a bound.
 */
function toBound(v: string | number): number | null {
  const n = typeof v === 'number' ? v : Number(v)
  return v === '' || Number.isNaN(n) ? null : n
}

/**
 * The number the chapter-count filter compares against: what's on disk, or the same total the
 * cards show (wanted chapters, falling back to every known chapter when nothing is wanted).
 */
function chapterCount(s: SeriesDto, mode: string): number {
  return mode === 'total' ? s.wantedChapterCount || s.knownChapterCount || 0 : s.chapterFileCount
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
  chapterMin: null,
  chapterMax: null,
  chapterMode: 'downloaded',
  contentRatings: [],
  sources: [],
  sourceMatch: 'any',
  sourceState: 'all',
  fileSources: [],
  fileSourceMatch: 'any',
}

const SOURCE_STATE_VALUES = ['all', 'none', 'hasDisabled', 'noneEnabled', 'hasEnabled'] as const

const SOURCE_STATE_LABELS: Record<string, MessageDescriptor> = {
  all: msg`Any`,
  none: msg`No sources linked`,
  hasDisabled: msg`Has a disabled source`,
  noneEnabled: msg`Linked, but nothing enabled`,
  hasEnabled: msg`At least one enabled`,
}

/** True when the series matches one of {@link SOURCE_STATE_VALUES}. `all` is filtered out before this. */
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

const CHAPTER_MODE_VALUES = ['downloaded', 'total'] as const

const CHAPTER_MODE_LABELS: Record<string, MessageDescriptor> = {
  downloaded: msg`Downloaded`,
  total: msg`Total`,
}

const MATCH_MODE_VALUES = ['any', 'all'] as const

/**
 * What the bulk-progress toast calls each action. Keyed by the busy-state key, which is an English
 * identifier compared with `===` and must never be translated; this is the reader-facing half.
 */
const BULK_ACTION_LABELS: Record<string, MessageDescriptor> = {
  'Search missing': msg`Search missing`,
  Refresh: msg`Refresh`,
  Metadata: msg`Metadata`,
  // ComicInfo is the file format's name and is deliberately absent: the fallback shows the key.
  Delete: msg`Delete`,
  'Set monitoring': msg`Set monitoring`,
  Move: msg`Move`,
}

const MATCH_MODE_LABELS: Record<string, MessageDescriptor> = {
  any: msg`Any`,
  all: msg`All`,
}

export default function LibraryPage() {
  const renderLabel = useLabel()
  const { i18n, _ } = useLingui()
  const { t } = useLinguiMacro()
  const notificationOptions = useSeriesNotificationOptions()
  const densityOptions = useDensityOptions()
  const [viewMode, setViewMode] = useState<ViewMode>(() => readStored(LS_VIEW, ['grid', 'list'], 'grid'))
  const [density, setDensity] = useState<Density>(() => readStored(LS_DENSITY, ['compact', 'default', 'comfortable'], 'default'))
  const { data: series, isLoading, error, refetch: refetchSeries, isRefetching: isRefetchingSeries } = useSeries()
  const { me } = useAuth()
  const { data: rootFolders } = useRootFolders()
  const { data: tags } = useTags()
  const { data: savedFilters } = useSavedFilters()
  const sourceLabel = useSourceLabel()
  const saveFilter = useSaveFilter()
  const deleteSavedFilter = useDeleteSavedFilter()
  const bulkTag = useBulkTag()
  const bulkNotifications = useBulkSetSeriesNotificationMode()
  const autoMatch = useAutoMatchSources()
  const readTracking = useReadTracking()
  const stats = useLibraryStats()
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const luckyPool = useMemo(
    () =>
      (series ?? [])
        .filter(isUnfinished)
        .map((s) => ({ key: String(s.id), title: s.displayTitle, coverUrl: s.coverUrl })),
    [series],
  )

  // Everything down to `filtersOpen` is remembered for the tab session, so opening a series from
  // the grid and coming back lands on the same view rather than on an unfiltered library. Selection
  // and the modals below are deliberately left out: those are half-finished actions, not a view.
  const [query, setQuery] = usePageState(`${MEM}:query`, '')
  // Re-filtering (and re-sorting) a few thousand series on every keystroke is what made typing
  // in here feel sticky: the input itself stays instant, the grid catches up a frame later.
  const [debouncedQuery] = useDebouncedValue(query, 200)
  const [sort, setSort] = usePageState(`${MEM}:sort`, 'added')
  const [statusFilter, setStatusFilter] = usePageState(`${MEM}:status`, 'all')
  // Tag ids live as strings because that's what MultiSelect speaks.
  const [tagFilter, setTagFilter] = usePageState<string[]>(`${MEM}:tags`, [])
  const [tagMatch, setTagMatch] = usePageState(`${MEM}:tag-match`, 'any')
  const [genreFilter, setGenreFilter] = usePageState<string[]>(`${MEM}:genres`, [])
  const [genreMatch, setGenreMatch] = usePageState(`${MEM}:genre-match`, 'any')
  const [metaTagFilter, setMetaTagFilter] = usePageState<string[]>(`${MEM}:meta-tags`, [])
  const [metaTagMatch, setMetaTagMatch] = usePageState(`${MEM}:meta-tag-match`, 'any')
  const [contentRatingFilter, setContentRatingFilter] = usePageState<string[]>(`${MEM}:content-ratings`, [])
  const [readRange, setReadRange] = usePageState<[number, number]>(`${MEM}:read-range`, [0, 100])
  // Null ends, not 0/Infinity: an empty box has to mean "unbounded", and a min of 0 is a real
  // (if inert) bound the user can type.
  const [chapterMin, setChapterMin] = usePageState<number | null>(`${MEM}:chapter-min`, null)
  const [chapterMax, setChapterMax] = usePageState<number | null>(`${MEM}:chapter-max`, null)
  const [chapterMode, setChapterMode] = usePageState(`${MEM}:chapter-mode`, 'downloaded')
  const [monitoredFilter, setMonitoredFilter] = usePageState(`${MEM}:monitored`, 'all')
  const [completeness, setCompleteness] = usePageState(`${MEM}:completeness`, 'all')
  const [sourceFilter, setSourceFilter] = usePageState<string[]>(`${MEM}:sources`, [])
  const [sourceMatch, setSourceMatch] = usePageState(`${MEM}:source-match`, 'any')
  const [sourceState, setSourceState] = usePageState(`${MEM}:source-state`, 'all')
  const [fileSourceFilter, setFileSourceFilter] = usePageState<string[]>(`${MEM}:file-sources`, [])
  const [fileSourceMatch, setFileSourceMatch] = usePageState(`${MEM}:file-source-match`, 'any')
  const [activeFilterId, setActiveFilterId] = usePageState<number | null>(`${MEM}:saved-filter`, null)
  const [saveFilterOpen, setSaveFilterOpen] = useState(false)
  const [filterName, setFilterName] = useState('')
  const [filtersOpen, setFiltersOpen] = usePageState(`${MEM}:filters-open`, false)
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
  const notificationHelp = useSeriesNotificationHelp(notifyMode)
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
    if (chapterMin != null || chapterMax != null) {
      list = list.filter((s) => {
        const n = chapterCount(s, chapterMode)
        return (chapterMin == null || n >= chapterMin) && (chapterMax == null || n <= chapterMax)
      })
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
    chapterMin, chapterMax, chapterMode,
  ])

  const statusOptions = useMemo(() => {
    const set = new Set((series ?? []).map((s) => s.status))
    return ['all', ...[...set].sort()]
  }, [series])

  const tagOptions = useMemo(
    () => (tags ?? []).map((t) => ({ value: String(t.id), label: `${t.label} (${t.seriesCount})` })),
    [tags],
  )

  // A tag deleted or renamed elsewhere leaves an orphaned pill; drop stale ids once tags load.
  useEffect(() => {
    if (tags == null) return
    const known = new Set(tagOptions.map((o) => o.value))
    setTagFilter((current) => {
      const next = current.filter((id) => known.has(id))
      return next.length === current.length ? current : next
    })
  }, [tags, tagOptions, setTagFilter])

  const genreOptions = useMemo(() => facetOptions(series, (s) => s.genres), [series])
  const metaTagOptions = useMemo(() => facetOptions(series, (s) => s.metadataTags), [series])

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
        label: renderLabel(CONTENT_RATING_LABELS[value]),
      })),
    [me?.maxContentRating, renderLabel, i18n.locale],
  )

  const sortOptions = useMemo(
    () => SORT_VALUES.map((value) => ({ value, label: _(SORT_LABELS[value]) })),
    [_, i18n.locale],
  )
  const sourceStateOptions = useMemo(
    () => SOURCE_STATE_VALUES.map((value) => ({ value, label: _(SOURCE_STATE_LABELS[value]) })),
    [_, i18n.locale],
  )
  const chapterModeOptions = useMemo(
    () => CHAPTER_MODE_VALUES.map((value) => ({ value, label: _(CHAPTER_MODE_LABELS[value]) })),
    [_, i18n.locale],
  )
  const matchModeOptions = useMemo(
    () => MATCH_MODE_VALUES.map((value) => ({ value, label: _(MATCH_MODE_LABELS[value]) })),
    [_, i18n.locale],
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
    chapterMin,
    chapterMax,
    chapterMode,
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
    setChapterMin(merged.chapterMin ?? null)
    setChapterMax(merged.chapterMax ?? null)
    setChapterMode(merged.chapterMode)
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
    (chapterMin != null || chapterMax != null ? 1 : 0) +
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
    const total = ids.length
    // `action` is the busy-state key and stays English. What the toast shows is its label, or the
    // key itself if the table has no entry, so a missing label degrades to English rather than
    // to nothing.
    const name = BULK_ACTION_LABELS[action] ? _(BULK_ACTION_LABELS[action]) : action
    setBusy(action)
    notifications.show({
      id: 'bulk-action',
      loading: true,
      message: `${name}: 0/${total}`,
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
      const done = ok + errors.length
      notifications.update({
        id: 'bulk-action',
        loading: true,
        message: `${name}: ${done}/${total}`,
        autoClose: false,
        withCloseButton: false,
      })
    }
    const firstError = errors[0]
    notifications.update({
      id: 'bulk-action',
      loading: false,
      color: errors.length ? 'var(--warn)' : 'var(--ok)',
      // The fixed wording is translated; `firstError` carries a raw exception message untouched.
      message: errors.length
        ? now`${name}: ${ok}/${total} succeeded, first error: ${firstError}`
        : now`${name}: ${ok}/${total} succeeded`,
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
          <SegmentedControl size="xs" value={mode} onChange={onModeChange} data={matchModeOptions} />
        )}
      </Group>
      {description && (
        <Text size="xs" c="var(--ink-3)" mb={4}>
          {description}
        </Text>
      )}
      <MultiSelect
        data={data}
        value={value}
        onChange={onChange}
        placeholder={value.length === 0 ? (data.length > 0 ? t`Any` : t`None available`) : undefined}
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

  // `key` is compared against `busy` (see runBulk) and must stay an untranslated identifier;
  // `label` is what actually renders, so it's the only part that gets translated.
  const bulkBtn = (key: string, label: ReactNode, icon: ReactNode, run: () => void, color?: string) => (
    <Button
      size="xs"
      variant="light"
      color={color}
      leftSection={icon}
      disabled={selected.size === 0 || (busy !== null && busy !== key)}
      loading={busy === key}
      onClick={run}
    >
      {label}
    </Button>
  )

  // Against the *filtered* set, not the whole library: "select all" under an active filter that
  // silently grabbed hidden series would make every bulk action a foot-gun.
  const allSelected = selected.size > 0 && selected.size === visible.length
  const selectedCount = selected.size

  // One hook serves both views: only one of the two wrappers is mounted at a time, and the ref
  // re-subscribes when the other takes over.
  const windowed = useWindowedRows(visible.length, visible.length >= WINDOW_MIN_ITEMS)

  // Hoisted so the "X of Y series" messages below get named placeholders instead of `{0}`/`{1}`.
  const visibleCount = formatNumber(visible.length)
  const totalCount = formatNumber(stats.total)
  const totalSeries = stats.total
  const shownCount = visible.length
  const totalSeriesShown = series?.length ?? 0
  const loadErrorMessage = error ? (error instanceof Error ? error.message : String(error)) : null
  // Kept mounted through loading and error too, so the grid lands where it will stay and an error isn't a dead end.
  const showChrome = isLoading || error != null || (series != null && series.length > 0)

  return (
    <SurfaceFrame width="full" pageStyle="editorial">
      <PageHeader
        title={t`Library`}
        description={t`Every series Maki watches: cover art, download progress and status at a glance.`}
        actions={
          showChrome && !selectMode ? (
            <>
              <Button.Group>
                <Button
                  variant={viewMode === 'grid' ? 'filled' : 'default'}
                  size="sm"
                  onClick={() => {
                    setViewMode('grid')
                    writeStored(LS_VIEW, 'grid')
                  }}
                  aria-label={t`Grid view`}
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
                  aria-label={t`List view`}
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
                data={densityOptions}
              />
              <Button
                variant="default"
                leftSection={<IconListCheck size={16} />}
                onClick={() => setSelectMode(true)}
              >
                <Trans>Select</Trans>
              </Button>
              <Button component={Link} to="/add" leftSection={<IconPlus size={16} />}>
                <Trans>Add series</Trans>
              </Button>
            </>
          ) : undefined
        }
      />

      {showChrome && (
        <Panel p={0} className="library-index layer-sunken">
          <FigureStrip
            flush
            loading={isLoading}
            className="library-index-metrics"
            figures={[
              { label: t`Series`, value: stats.total },
              { label: t`Monitored`, value: stats.monitored },
              { label: t`On disk`, value: stats.downloaded, tone: 'ok' },
              { label: t`Missing`, value: stats.missing, tone: 'warn' },
              ...(stats.inQueue > 0 ? [{ label: t`In queue`, value: stats.inQueue, tone: 'info' as const }] : []),
            ]}
          />

          {selectMode ? (
            <Group className="library-selection-bar" justify="space-between" wrap="wrap" gap="xs">
              <Group gap="xs">
                {/* On a phone the action row scrolls sideways, which would bury Done at its far end. */}
                <ActionIcon
                  hiddenFrom="sm"
                  variant="default"
                  disabled={busy !== null}
                  onClick={exitSelectMode}
                  aria-label={t`Done`}
                >
                  <IconX size={15} />
                </ActionIcon>
                <Text size="sm" c="var(--ink-3)" className="tnum">
                  <Plural value={selectedCount} one="# selected" other="# selected" />
                </Text>
                <Button
                  size="xs"
                  variant="subtle"
                  onClick={() =>
                    setSelected(allSelected ? new Set() : new Set(visible.map((s) => s.id)))
                  }
                >
                  {allSelected ? (
                    <Trans>Clear all</Trans>
                  ) : filtersActive ? (
                    <Trans>Select filtered</Trans>
                  ) : (
                    <Trans>Select all</Trans>
                  )}
                </Button>
                <Text size="xs" c="var(--ink-3)" className="tnum">
                  {filtersActive ? (
                    <Trans>
                      {visibleCount} of {totalCount} series match
                    </Trans>
                  ) : (
                    <Plural value={totalSeries} one="# series" other="# series" />
                  )}
                </Text>
              </Group>
              <Group gap="xs">
                {bulkBtn('Search missing', <Trans>Search missing</Trans>, <IconSearch size={15} />, () =>
                  runBulk('Search missing', (id) =>
                    api(`/series/${id}/searchmissing`, { method: 'POST' }),
                  ),
                )}
                {bulkBtn('Refresh', <Trans>Refresh</Trans>, <IconRefresh size={15} />, () =>
                  runBulk('Refresh', (id) => api(`/series/${id}/refresh`, { method: 'POST' })),
                )}
                {bulkBtn('Auto-match', <Trans>Auto-match</Trans>, <IconWand size={15} />, () =>
                  setAutoMatchModalOpen(true),
                )}
                {bulkBtn('Metadata', <Trans>Metadata</Trans>, <IconPhoto size={15} />, () =>
                  runBulk('Metadata', (id) =>
                    api(`/series/${id}/refreshmetadata`, { method: 'POST' }),
                  ),
                )}
                {/* "ComicInfo" is the ComicInfo.xml format name, not translated (see rule 5). */}
                {bulkBtn('ComicInfo', 'ComicInfo', <IconFileText size={15} />, () =>
                  runBulk('ComicInfo', (id) =>
                    api(`/series/${id}/updatecomicinfo`, { method: 'POST' }),
                  ),
                )}
                {bulkBtn('Tags', <Trans>Tags</Trans>, <IconTag size={15} />, () => {
                  setTagsToAdd([])
                  setTagsToRemove([])
                  setTagModalOpen(true)
                })}
                {bulkBtn('Monitoring', <Trans>Monitoring</Trans>, <IconEye size={15} />, () =>
                  setMonitorModalOpen(true),
                )}
                {bulkBtn('Notifications', <Trans>Notifications</Trans>, <IconBell size={15} />, () =>
                  setNotifyModalOpen(true),
                )}
                {bulkBtn('Move', <Trans>Move</Trans>, <IconFolderSymlink size={15} />, () => {
                  setMoveTarget(null)
                  setMoveFiles(true)
                  setMoveModalOpen(true)
                })}
                {bulkBtn('Delete', <Trans>Delete</Trans>, <IconTrash size={15} />, () => setDeleteModalOpen(true), 'red')}
                <Button
                  visibleFrom="sm"
                  size="xs"
                  variant="default"
                  leftSection={<IconX size={15} />}
                  disabled={busy !== null}
                  onClick={exitSelectMode}
                >
                  <Trans>Done</Trans>
                </Button>
              </Group>
            </Group>
          ) : (
            <Stack className="library-toolbar" gap="sm">
              <Group className="library-toolbar-row" gap="sm" wrap="wrap">
                <TextInput
                  className="library-search"
                  placeholder={t`Filter library…`}
                  leftSection={<IconSearch size={16} />}
                  value={query}
                  onChange={(e) => setQuery(e.currentTarget.value)}
                />
                <Button
                  visibleFrom="sm"
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
                  <Trans>Filters</Trans>
                </Button>
                {/* On a phone the bar is one sticky line, so the button gives up its label. */}
                <Indicator
                  hiddenFrom="sm"
                  label={activeFilterCount}
                  size={16}
                  disabled={activeFilterCount === 0}
                  className="library-filter-icon"
                >
                  <ActionIcon
                    size={36}
                    variant={activeFilterCount > 0 ? 'light' : 'default'}
                    onClick={() => setFiltersOpen(true)}
                    aria-label={t`Filters`}
                  >
                    <IconFilter size={16} />
                  </ActionIcon>
                </Indicator>
                <Select
                  className="library-sort"
                  data={sortOptions}
                  value={sort}
                  onChange={(v) => setSort(v ?? 'added')}
                  comboboxProps={{ withinPortal: true }}
                />
                <LuckyButton
                  candidates={luckyPool}
                  onPick={(id) => navigate(`/series/${id}`, { state: { lucky: true } })}
                />
                <Text size="sm" c="var(--ink-3)" className="tnum" visibleFrom="sm" hidden={isLoading}>
                  {filtersActive ? (
                    <Trans>
                      {visibleCount} of {totalCount} series match
                    </Trans>
                  ) : (
                    <Plural value={totalSeries} one="# series" other="# series" />
                  )}
                </Text>
              </Group>

              <Group
                className="library-saved-filters"
                gap="xs"
                wrap="wrap"
                data-tools-only={((savedFilters ?? []).length === 0 && !filtersActive) || undefined}
              >
                {(savedFilters ?? []).map((f) => (
                <Group key={f.id} gap={2}>
                  <TagChip
                    active={activeFilterId === f.id}
                    onClick={() => applySpec(f.spec, f.id)}
                  >
                    <IconBookmark size={11} />
                    {f.name}
                  </TagChip>
                  <ActionIcon
                    size="xs"
                    variant="subtle"
                    color="var(--ink-4)"
                    aria-label={t`Delete saved filter`}
                    onClick={() => {
                      deleteSavedFilter.mutate(f.id)
                      if (activeFilterId === f.id) setActiveFilterId(null)
                    }}
                  >
                    <IconX size={11} />
                  </ActionIcon>
                </Group>
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
                  <Trans>Save filter</Trans>
                </Button>
              )}
              {filtersActive && (
                <Button
                  size="compact-xs"
                  variant="subtle"
                  color="var(--neutral)"
                  leftSection={<IconX size={14} />}
                  onClick={() => applySpec(DEFAULT_SPEC, null)}
                >
                  <Trans>Clear</Trans>
                </Button>
              )}
              <Tooltip label={t`Manage tags`} withArrow>
                <ActionIcon
                  variant="subtle"
                  color="var(--neutral)"
                  onClick={() => setTagManagerOpen(true)}
                  aria-label={t`Manage tags`}
                >
                  <IconSettings size={16} />
                </ActionIcon>
              </Tooltip>
              </Group>
            </Stack>
          )}
        </Panel>
      )}

      <Drawer
        opened={filtersOpen}
        onClose={() => setFiltersOpen(false)}
        position="right"
        size="sm"
        title={t`Filters`}
      >
        <Stack gap="sm" pb="xl">
          <Text size="sm" c="var(--ink-3)">
            <Trans>
              {shownCount} of {totalSeriesShown} series shown.
            </Trans>{' '}
            <Trans>Changes apply straight to the grid behind this panel.</Trans>
          </Text>
          <Select
            label={t`Status`}
            data={statusOptions.map((s) => ({
              value: s,
              label: s === 'all' ? t`All statuses` : s,
            }))}
            value={statusFilter}
            onChange={(v) => setStatusFilter(v ?? 'all')}
            comboboxProps={{ withinPortal: true }}
          />
          {facetFilter({
            label: t`Your tags`,
            data: tagOptions,
            value: tagFilter,
            onChange: setTagFilter,
            mode: tagMatch,
            onModeChange: setTagMatch,
          })}
          <Button
            variant="subtle"
            size="compact-sm"
            color="var(--neutral)"
            leftSection={<IconSettings size={14} />}
            style={{ alignSelf: 'flex-start' }}
            onClick={() => {
              setFiltersOpen(false)
              setTagManagerOpen(true)
            }}
          >
            <Trans>Manage tags</Trans>
          </Button>
          {facetFilter({
            label: t`Genres`,
            data: genreOptions,
            value: genreFilter,
            onChange: setGenreFilter,
            mode: genreMatch,
            onModeChange: setGenreMatch,
          })}
          {facetFilter({
            label: t`Tags`,
            description: t`From the metadata provider, not your own tags`,
            data: metaTagOptions,
            value: metaTagFilter,
            onChange: setMetaTagFilter,
            mode: metaTagMatch,
            onModeChange: setMetaTagMatch,
          })}
          <MultiSelect
            label={t`Content rating`}
            placeholder={contentRatingFilter.length ? undefined : t`Any`}
            data={contentRatingOptions}
            value={contentRatingFilter}
            onChange={setContentRatingFilter}
            clearable
            comboboxProps={{ withinPortal: true }}
          />
          <Select
            label={t`Monitoring`}
            data={[
              { value: 'all', label: t`Any` },
              { value: 'monitored', label: t`Monitored` },
              { value: 'unmonitored', label: t`Unmonitored` },
            ]}
            value={monitoredFilter}
            onChange={(v) => setMonitoredFilter(v ?? 'all')}
            comboboxProps={{ withinPortal: true }}
          />
          <Select
            label={t`Completeness`}
            data={[
              { value: 'all', label: t`Any` },
              { value: 'behind', label: t`Behind (missing chapters)` },
              { value: 'complete', label: t`Complete` },
            ]}
            value={completeness}
            onChange={(v) => setCompleteness(v ?? 'all')}
            comboboxProps={{ withinPortal: true }}
          />
          <div>
            <Text size="sm" fw={500} mb={2}>
              <Trans>Chapters</Trans>
            </Text>
            <Text size="xs" c="var(--ink-3)" mb="xs">
              <Trans>Leave both boxes empty to ignore.</Trans>
            </Text>
            <SegmentedControl
              size="xs"
              fullWidth
              value={chapterMode}
              onChange={setChapterMode}
              data={chapterModeOptions}
              mb="xs"
            />
            <Group grow gap="xs" align="flex-start">
              <NumberInput
                aria-label={t`Minimum chapters`}
                placeholder={t`Min`}
                min={0}
                allowDecimal={false}
                value={chapterMin ?? ''}
                onChange={(v) => setChapterMin(toBound(v))}
              />
              <NumberInput
                aria-label={t`Maximum chapters`}
                placeholder={t`Max`}
                min={0}
                allowDecimal={false}
                value={chapterMax ?? ''}
                onChange={(v) => setChapterMax(toBound(v))}
              />
            </Group>
          </div>
          <Select
            label={t`Source state`}
            description={t`Counts both switches: the per-series link and the global source toggle`}
            data={sourceStateOptions}
            value={sourceState}
            onChange={(v) => setSourceState(v ?? 'all')}
            comboboxProps={{ withinPortal: true }}
          />
          {facetFilter({
            label: t`Sources`,
            description: t`Linked to the series, enabled or not`,
            data: sourceOptions,
            value: sourceFilter,
            onChange: setSourceFilter,
            mode: sourceMatch,
            onModeChange: setSourceMatch,
          })}
          {facetFilter({
            label: t`Downloaded from`,
            description: t`Where the files on disk came from, which can outlive the link`,
            data: fileSourceOptions,
            value: fileSourceFilter,
            onChange: setFileSourceFilter,
            mode: fileSourceMatch,
            onModeChange: setFileSourceMatch,
          })}
          {readTracking && (
            <div>
              <Text size="sm" fw={500} mb={2}>
                <Trans>Read</Trans>
              </Text>
              <Text size="xs" c="var(--ink-3)" mb="md">
                <Trans>Share of the series you've read.</Trans> <Trans>Leave at 0–100% to ignore.</Trans>
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
              <Trans>Clear all filters</Trans>
            </Button>
          )}
        </Stack>
      </Drawer>

      <TagManagerModal opened={tagManagerOpen} onClose={() => setTagManagerOpen(false)} />

      <Modal
        opened={saveFilterOpen}
        onClose={() => setSaveFilterOpen(false)}
        title={t`Save this filter`}
      >
        <Stack gap="md">
          <Text size="sm" c="var(--ink-3)">
            <Trans>Saves the current search, sort and every filter in the panel as a named preset.</Trans>{' '}
            <Trans>Reusing the name of the active preset overwrites it.</Trans>
          </Text>
          <TextInput
            label={t`Name`}
            placeholder={t`e.g. Ongoing & behind`}
            value={filterName}
            onChange={(e) => setFilterName(e.currentTarget.value)}
            data-autofocus
          />
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setSaveFilterOpen(false)}>
              <Trans>Cancel</Trans>
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
                    onError: (err) => {
                      const detail = err instanceof Error ? err.message : String(err)
                      notifications.show({ color: 'var(--danger)', message: now`Failed to save filter: ${detail}` })
                    },
                  },
                )
              }}
            >
              <Trans>Save</Trans>
            </Button>
          </Group>
        </Stack>
      </Modal>

      <Modal
        opened={tagModalOpen}
        onClose={() => setTagModalOpen(false)}
        title={t`Tag ${selectedCount} series`}
      >
        <Stack gap="md">
          <Text size="sm" c="var(--ink-3)">
            <Trans>Adds and removes run in one pass over the selection.</Trans>{' '}
            <Trans>Create new tags from "Manage tags".</Trans>
          </Text>
          <MultiSelect
            label={t`Add`}
            data={tagOptions}
            value={tagsToAdd}
            onChange={setTagsToAdd}
            searchable
            clearable
            comboboxProps={{ withinPortal: true }}
          />
          <MultiSelect
            label={t`Remove`}
            data={tagOptions}
            value={tagsToRemove}
            onChange={setTagsToRemove}
            searchable
            clearable
            comboboxProps={{ withinPortal: true }}
          />
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setTagModalOpen(false)}>
              <Trans>Cancel</Trans>
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
                      notifications.show({
                        color: 'var(--ok)',
                        message: plural(updated, { one: 'Tagged # series', other: 'Tagged # series' }),
                      })
                    },
                    onError: (err) => {
                      const detail = err instanceof Error ? err.message : String(err)
                      notifications.show({ color: 'var(--danger)', message: now`Failed to tag: ${detail}` })
                    },
                  },
                )
              }
            >
              <Trans>Apply</Trans>
            </Button>
          </Group>
        </Stack>
      </Modal>

      <Modal
        opened={autoMatchModalOpen}
        onClose={() => setAutoMatchModalOpen(false)}
        title={t`Auto-match sources for ${selectedCount} series`}
      >
        <Text size="sm" mb="md">
          <Trans>
            Every source that isn't linked yet is searched again for each series, which is worth doing
            when a source has picked a title up since you added it.
          </Trans>{' '}
          <Trans>Sources already linked are left exactly as they are, so this only ever adds.</Trans>
        </Text>
        <Text size="sm" c="var(--ink-3)" mb="lg">
          <Trans>
            Matching runs in the background, one series at a time, to keep the request rate at the
            sites sane.
          </Trans>{' '}
          <Trans>A large selection can take a while.</Trans>
        </Text>
        <Group justify="flex-end">
          <Button variant="default" onClick={() => setAutoMatchModalOpen(false)}>
            <Trans>Cancel</Trans>
          </Button>
          <Button
            leftSection={<IconWand size={16} />}
            loading={autoMatch.isPending}
            onClick={() =>
              autoMatch.mutate([...selected], {
                onSuccess: ({ queued }) => {
                  setAutoMatchModalOpen(false)
                  exitSelectMode()
                  notifications.show({
                    color: queued > 0 ? 'var(--ok)' : undefined,
                    message:
                      queued > 0
                        ? plural(queued, {
                            one: 'Auto-matching # series in the background.',
                            other: 'Auto-matching # series in the background.',
                          })
                        : now`Those series are already being matched.`,
                  })
                },
              })
            }
          >
            <Trans>Auto-match</Trans>
          </Button>
        </Group>
      </Modal>

      <Modal
        opened={deleteModalOpen}
        onClose={() => setDeleteModalOpen(false)}
        title={t`Delete ${selectedCount} series?`}
      >
        <Text size="sm" mb="md">
          <Trans>The selected series will be removed from Maki and stop being monitored.</Trans>
        </Text>
        <Checkbox
          label={t`Also delete the folders and files on disk`}
          checked={deleteFiles}
          onChange={(e) => setDeleteFiles(e.currentTarget.checked)}
          mb="lg"
        />
        <Group justify="flex-end">
          <Button variant="default" onClick={() => setDeleteModalOpen(false)}>
            <Trans>Cancel</Trans>
          </Button>
          <Button
            color="var(--danger-fill)"
            leftSection={<IconTrash size={16} />}
            onClick={() => {
              setDeleteModalOpen(false)
              void runBulk('Delete', (id) =>
                api(`/series/${id}?deleteFiles=${deleteFiles}`, { method: 'DELETE' }),
              ).then(exitSelectMode)
            }}
          >
            <Trans>Delete</Trans>
          </Button>
        </Group>
      </Modal>

      <Modal
        opened={monitorModalOpen}
        onClose={() => setMonitorModalOpen(false)}
        title={t`Set monitoring for ${selectedCount} series`}
      >
        <Text size="sm" mb="md">
          <Trans>Applies to chapters released later.</Trans>{' '}
          <Trans>Chapters already listed keep whatever you set on them.</Trans>{' '}
          <Trans>
            "Main" skips specials (decimal chapters like 10.5); "Smart" downloads a few at a time as you
            read.
          </Trans>
        </Text>
        <SegmentedControl
          fullWidth
          value={monitorMode}
          onChange={setMonitorMode}
          data={[
            { value: 'All', label: t`All` },
            { value: 'Smart', label: t`Smart` },
            { value: 'MainOnly', label: t`Main only` },
            { value: 'None', label: t`None` },
          ]}
          mb="lg"
        />
        <Group justify="flex-end">
          <Button variant="default" onClick={() => setMonitorModalOpen(false)}>
            <Trans>Cancel</Trans>
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
            <Trans>Apply</Trans>
          </Button>
        </Group>
      </Modal>

      <Modal
        opened={notifyModalOpen}
        onClose={() => setNotifyModalOpen(false)}
        title={t`Set notifications for ${selectedCount} series`}
      >
        <Text size="sm" mb="md">
          <Trans>Yours alone - this changes what lands in your bell, not anybody else's.</Trans>
        </Text>
        <SegmentedControl
          fullWidth
          value={notifyMode}
          onChange={(v) => setNotifyMode(v as SeriesNotificationMode)}
          data={notificationOptions}
          mb="xs"
        />
        <Text size="xs" c="var(--ink-3)" mb="lg">
          {notificationHelp}
        </Text>
        <Group justify="flex-end">
          <Button variant="default" onClick={() => setNotifyModalOpen(false)}>
            <Trans>Cancel</Trans>
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
                      color: 'var(--ok)',
                      message: plural(updated, {
                        one: 'Notifications updated for # series',
                        other: 'Notifications updated for # series',
                      }),
                    })
                  },
                  onError: (err) => {
                    const detail = err instanceof Error ? err.message : String(err)
                    notifications.show({
                      color: 'var(--danger)',
                      message: now`Failed to update notifications: ${detail}`,
                    })
                  },
                },
              )
            }
          >
            <Trans>Apply</Trans>
          </Button>
        </Group>
      </Modal>

      <Modal
        opened={moveModalOpen}
        onClose={() => setMoveModalOpen(false)}
        title={t`Move ${selectedCount} series`}
      >
        <Stack gap="md">
          <Text size="sm" c="var(--ink-3)">
            <Trans>Re-triggers a Kavita scan of both locations either way.</Trans>{' '}
            <Trans>Series already in the destination root folder are skipped.</Trans>{' '}
            <Trans>A file move is blocked for any series with an active download.</Trans>
          </Text>
          <Select
            label={t`Destination root folder`}
            placeholder={t`Pick a root folder`}
            data={(rootFolders ?? []).map((f) => ({ value: String(f.id), label: f.path }))}
            value={moveTarget}
            onChange={setMoveTarget}
            comboboxProps={{ withinPortal: true }}
          />
          <Radio.Group
            label={t`Files`}
            value={moveFiles ? 'move' : 'already-moved'}
            onChange={(v) => setMoveFiles(v === 'move')}
          >
            <Stack gap={6} mt={6}>
              <Radio value="move" label={t`Move the files on disk to the new root folder`} />
              <Radio
                value="already-moved"
                label={t`Just point the series at the new root folder, I already moved the files`}
              />
            </Stack>
          </Radio.Group>
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setMoveModalOpen(false)}>
              <Trans>Cancel</Trans>
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
              <Trans>Move</Trans>
            </Button>
          </Group>
        </Stack>
      </Modal>

      {isLoading && <PosterSkeletons density={density} viewMode={viewMode} />}
      {error && (
        <EmptyState
          title={t`Failed to load library`}
          description={loadErrorMessage}
          actionLabel={isRefetchingSeries ? t`Retrying…` : t`Retry`}
          onAction={() => void refetchSeries()}
        />
      )}
      {series && series.length === 0 && (
        <EmptyState
          title={t`Your library is empty`}
          description={t`Search MangaBaka and add your first series. Maki will monitor for new chapters and download them automatically.`}
          actionLabel={t`Add a series`}
          actionTo="/add"
        />
      )}
      {series && series.length > 0 && visible.length === 0 && (
        <EmptyState
          title={t`No matches`}
          description={t`No series match the current filter. Try clearing the search or status filter.`}
          actionLabel={t`Clear all filters`}
          onAction={() => applySpec(DEFAULT_SPEC, null)}
        />
      )}
      {/* Both views render a slice, not the whole filtered set, once the library is big enough to
          be worth it (see useWindowedRows for the threshold and what it costs). Bulk selection is
          unaffected: "select filtered" works off `visible`, never off what is mounted. */}
      {visible.length > 0 && viewMode === 'grid' && (
        <div ref={windowed.outerRef} style={{ paddingTop: windowed.padTop, paddingBottom: windowed.padBottom }}>
          <SimpleGrid ref={windowed.innerRef} cols={POSTER_COLS_BY_DENSITY[density]} spacing="md">
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
    </SurfaceFrame>
  )
}
