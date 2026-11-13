import { SearchInput } from '../components/ui/SearchInput'
import { errorText } from '../api/errorText'
import { type FormEvent, Fragment, type ReactNode, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { hasPageState, usePageState } from '../lib/pageState'
import { paramsToSpec, specToParams, VIEW_PARAM, viewFromParams } from '../lib/libraryUrl'
import { onPageScroll } from '../lib/pageScroll'
import { shownRating } from '../lib/rating'
import { fuzzyFilter, titleIndex } from '../lib/fuzzySearch'
import { useStableActions } from '../lib/useStableActions'
import {
  ActionIcon,
  Badge,
  Box,
  Button,
  Checkbox,
  Divider,
  Group,
  Indicator,
  Menu,
  Modal,
  MultiSelect,
  Radio,
  SegmentedControl,
  Select,
  SimpleGrid,
  Stack,
  Text,
  TextInput,
  Tooltip,
} from '@mantine/core'
import {
  IconArchive,
  IconArchiveOff,
  IconBook,
  IconBookmark,
  IconCheck,
  IconChevronDown,
  IconDeviceFloppy,
  IconDeviceMobileCheck,
  IconEye,
  IconEyeOff,
  IconRanking,
  IconFileOff,
  IconFileText,
  IconFilter,
  IconFolderDown,
  IconFolderSymlink,
  IconListCheck,
  IconPencil,
  IconPhoto,
  IconPhotoCog,
  IconPlus,
  IconRefresh,
  IconScan,
  IconSearch,
  IconSettings,
  IconSortAscending,
  IconSortDescending,
  IconTable,
  IconListTree,
  IconBell,
  IconChevronLeft,
  IconChevronRight,
  IconColumns,
  IconArrowUp,
  IconArrowDown,
  IconStar,
  IconTag,
  IconTrash,
  IconTrashX,
  IconWand,
  IconX,
} from '../lib/icons'
import { OverflowButton } from '../components/ui/OverflowButton'
import { useIncognitoOptions } from '../components/ui/incognito'
import { INCOGNITO_HELP } from '../components/series/SeriesActionsMenu'
import { PAGE_CONVERSION_STOPS } from '../api/pageConversion'
import { STOP_HINTS, STOP_LABELS } from '../components/pageconversion/labels'
import { notifications } from '../lib/toast'
import {
  useSeriesNotificationHelp,
  useSeriesNotificationOptions,
  type SeriesNotificationMode,
} from '../components/ui/seriesNotifications'
import { useDebouncedValue } from '@mantine/hooks'
import { useQueryClient } from '@tanstack/react-query'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import { api } from '../api/client'
import { runSeriesBulk, type SeriesBulkRequest } from '../api/seriesBulk'
import {
  allowedContentRatings,
  CONTENT_RATING_LABELS,
  missingCount,
  useAutoMatchSources,
  useBulkArchiveSeries,
  useBulkSetReadingStatus,
  useBulkSetSeriesNotificationMode,
  useBulkSetUpgradeProfile,
  useBulkTag,
  useDeleteSavedFilter,
  useReorderSavedFilters,
  useSetDefaultSavedFilter,
  useDeleteSeries,
  useLibraryStats,
  useRemoveArchivedFiles,
  useRestoreSeries,
  useRootFolders,
  useSavedFilters,
  useSaveFilter,
  useRestoreSavedFilter,
  useSeries,
  useRateSeries,
  useTags,
} from '../api/hooks'
import { offersPermanentDelete } from '../api/recycleBin'
import { affectedKeys } from '../api/recommendationFeedback'
import { useUpgradeProfiles } from '../api/upgrades'
import { useReadTracking } from '../api/reader'
import { useAuth } from '../auth/AuthProvider'
import { useLabel } from '../i18n-context'
import { useSourceLabel } from '../sourceLabels'
import { useLingui } from '@lingui/react'
import { Trans, Plural, useLingui as useLinguiMacro } from '@lingui/react/macro'
import { msg, plural, t as now } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import type { LibraryFilterSpec, RestoreSeriesResponse, SavedFilterDto, SeriesDto } from '../api/types'
import { PosterSkeletons } from '../components/CatalogueBrowser'
import { TYPE_LABELS, TYPE_OPTIONS } from '../components/CatalogueFilters'
import { CoverCard, type OfflineAvailability } from '../components/ui/CoverCard'
import { ModalFooter, ModalHeading } from '../components/ui/ModalShell'
import { SeriesRow } from '../components/ui/SeriesRow'
import { EmptyState } from '../components/ui/EmptyState'
import { DrillChoices } from '../components/ui/DrillPanel'
import {
  LibraryFilterPanel,
  type LibraryFilterOptions,
  type LibraryFilterSetters,
  type LibraryFilterValues,
} from '../components/library/LibraryFilterPanel'
import { PageHeader } from '../components/ui/PageHeader'
import { Panel } from '../components/ui/Panel'
import { FigureStrip } from '../components/ui/FigureStrip'
import { TagChip } from '../components/ui/TagChip'
import { SurfaceFrame } from '../components/ui/SurfaceFrame'
import { LuckyButton } from '../components/LuckyButton'
import { isUnfinished } from '../lib/lucky'
import { WINDOW_MIN_ITEMS } from '../components/ui/useWindowedRows'
import { TagManagerModal } from '../components/TagManagerModal'
import { RemoveSeriesDialog } from '../components/series/RemoveSeriesDialog'
import { ArchiveSeriesDialog } from '../components/series/ArchiveSeriesDialog'
import { RestoreSeriesDialog } from '../components/series/RestoreSeriesDialog'
import { RemoveArchivedFilesDialog } from '../components/series/RemoveArchivedFilesDialog'
import { ArchivedSeriesCard, type ArchivedCardAction } from '../components/series/ArchivedSeriesCard'
import { ArchivedList } from '../components/library/ArchivedList'
import {
  LibraryTableBody,
  LibraryTableFrame,
  LibraryTableHeader,
  LibraryTableRow,
} from '../components/library/LibraryTable'
import {
  GROUP_BY_LABELS,
  GROUP_BY_VALUES,
  groupSeries,
  STATUS_ORDER,
  useCollapsedGroups,
  type GroupBy,
} from '../components/library/libraryGroups'
import { LibraryGroupHeader, WindowedItems } from '../components/library/LibraryGroupHeader'
import {
  DEFAULT_LIBRARY_COLUMNS,
  LIBRARY_COLUMN_LABELS,
  LIBRARY_COLUMNS,
  readLibraryColumns,
  writeLibraryColumns,
  type LibraryColumn,
} from '../components/library/libraryColumns'
import { showBulkRestoreQueued, showRestoreResult } from '../components/series/restoreToast'
import {
  DensityMenuItems,
  LIBRARY_POSTER_COLS_BY_DENSITY,
  readStored,
  ToolbarMenuButton,
  ViewModeToggle,
  writeStored,
} from '../components/ui/viewPrefs'
import {
  READING_STATUSES,
  readingStatusVisual,
  seriesProgressVisual,
  seriesStatusVisual,
  type ReadingStatus,
} from '../components/ui/status'
import { formatNumber } from '../format'
import { useOfflineAvailableSeriesIds, useOfflineSeriesIds } from '../pwa/offlineChapters'
import { useConnectivity } from '../lib/connectivity'

const SORT_VALUES = [
  'added',
  'title',
  'incomplete',
  'status',
  'read',
  'rating',
  'downloaded',
  'year',
  'score',
  'unread',
  'released',
  'chapters',
  'author',
  'archived',
] as const

type LibraryScope = 'library' | 'archived'

/** Which sorts each scope offers. The first is the scope's default. */
const SCOPE_SORTS: Record<LibraryScope, readonly string[]> = {
  library: SORT_VALUES.filter((v) => v !== 'archived'),
  archived: ['archived', 'title', 'rating', 'read', 'year', 'score', 'chapters', 'author'],
}

const SORT_LABELS: Record<string, MessageDescriptor> = {
  added: msg`Recently added`,
  title: msg`Title`,
  incomplete: msg`Most missing`,
  status: msg`Status`,
  read: msg`Recently read`,
  rating: msg`My rating`,
  downloaded: msg`Last downloaded`,
  year: msg`Year`,
  score: msg`Community score`,
  unread: msg`Most unread`,
  released: msg`Latest chapter released`,
  chapters: msg`Chapter count`,
  author: msg`Author`,
  archived: msg({ message: 'Recently archived', context: 'series' }),
}

type SortDir = 'asc' | 'desc'

/** The direction each key starts in: names read A to Z, dates and scores read biggest first. */
const SORT_DEFAULT_DIR: Record<string, SortDir> = {
  added: 'desc',
  title: 'asc',
  incomplete: 'desc',
  status: 'asc',
  read: 'desc',
  rating: 'desc',
  downloaded: 'desc',
  year: 'desc',
  score: 'desc',
  unread: 'desc',
  released: 'desc',
  chapters: 'desc',
  author: 'asc',
  archived: 'desc',
}

function toTime(iso: string | null | undefined): number | null {
  if (!iso) return null
  const t = Date.parse(iso)
  return Number.isNaN(t) ? null : t
}

function statusRank(status: string): number {
  const i = STATUS_ORDER.indexOf(status)
  return i === -1 ? STATUS_ORDER.length : i
}

/**
 * Orders two series by `sort` in `dir`. A series with no value for a nullable key (never read,
 * unrated, no files) sorts after every series that has one, whichever way the list is facing.
 */
function compareSeries(sort: string, dir: SortDir): (a: SeriesDto, b: SeriesDto) => number {
  const sign = dir === 'asc' ? 1 : -1
  const nullable = (pick: (s: SeriesDto) => number | null) => (a: SeriesDto, b: SeriesDto) => {
    const x = pick(a)
    const y = pick(b)
    if (x == null || y == null) return x == null ? (y == null ? 0 : 1) : -1
    return (x - y) * sign
  }
  const nullableText = (pick: (s: SeriesDto) => string | null) => (a: SeriesDto, b: SeriesDto) => {
    const x = pick(a)
    const y = pick(b)
    if (x == null || y == null) return x == null ? (y == null ? 0 : 1) : -1
    return x.localeCompare(y) * sign
  }
  switch (sort) {
    case 'title':
      return (a, b) => titleSortKey(a).localeCompare(titleSortKey(b)) * sign
    case 'incomplete':
      return (a, b) => (missingCount(a) - missingCount(b)) * sign
    case 'status':
      return (a, b) => (statusRank(a.status) - statusRank(b.status)) * sign
    case 'read':
      return nullable((s) => toTime(s.lastReadAt))
    case 'rating':
      return nullable(shownRating)
    case 'downloaded':
      return nullable((s) => toTime(s.lastDownloadedAt))
    case 'year':
      return nullable((s) => s.year)
    case 'score':
      return nullable((s) => s.communityRating ?? null)
    case 'unread':
      return (a, b) => (unreadCount(a) - unreadCount(b)) * sign
    case 'released':
      return nullable((s) => toTime(s.latestChapterAt))
    case 'chapters':
      return (a, b) => (chapterCount(a, 'total') - chapterCount(b, 'total')) * sign
    case 'author':
      return nullableText(authorOf)
    case 'archived':
      return nullable((s) => toTime(s.archivedAt))
    default:
      return (a, b) => (new Date(a.added).getTime() - new Date(b.added).getTime()) * sign
  }
}

/** Downloaded chapters the caller has not finished yet. */
function unreadCount(s: SeriesDto): number {
  return Math.max(0, s.chapterFileCount - (s.readChapterCount ?? 0))
}

/** Every name credited for story or art, split the way the series page splits them. */
function creditsOf(s: SeriesDto): string[] {
  const names = new Set<string>()
  for (const credit of [s.authorStory, s.authorArt]) {
    for (const name of (credit ?? '').split(',')) {
      const trimmed = name.trim()
      if (trimmed !== '') names.add(trimmed)
    }
  }
  return [...names]
}

const OVERRIDE_VALUES = ['readCleanup', 'pageConversion'] as const

const OVERRIDE_LABELS: Record<string, MessageDescriptor> = {
  readCleanup: msg`Read file cleanup`,
  pageConversion: msg`Page conversion`,
}

const DAY_MS = 86_400_000

function authorOf(s: SeriesDto): string | null {
  const name = (s.authorStory || s.authorArt || '').trim()
  return name === '' ? null : name.toLowerCase()
}

/** A series with every file gone that had some: restoring it is a choice about downloading them again. */
function needsRedownloadChoice(s: SeriesDto | undefined): boolean {
  return s != null && s.chapterFileCount === 0 && s.removedChapterCount > 0
}

type ViewMode = 'grid' | 'list' | 'table'
const VIEW_MODES: readonly ViewMode[] = ['grid', 'list', 'table']
type Density = 'compact' | 'default' | 'comfortable'

/**
 * Where the library's filters are remembered between visits, see usePageState. The Archived view
 * keeps its own set, so its sort and filters never leak into the library or back.
 */
const MEM_BY_SCOPE: Record<LibraryScope, string> = { library: 'library', archived: 'library:archived' }

const LS_VIEW = 'library-view'
const LS_DENSITY = 'library-density'

function titleSortKey(s: SeriesDto): string {
  if (s.displayTitle === s.title) return s.sortTitle
  const lowered = s.displayTitle.toLowerCase()
  const article = ['the ', 'a ', 'an '].find((a) => lowered.startsWith(a))
  return article ? lowered.slice(article.length) : lowered
}

/**
 * The percentage the cover's read ring shows, so the filter agrees with it. A series with no read
 * progress counts as 0% rather than being dropped: the whole library would otherwise vanish the
 * moment the slider left 0.
 */
function readPercent(s: SeriesDto, readTracking: boolean): number {
  return Math.round(seriesProgressVisual(s, readTracking).readPct ?? 0)
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
    .map(([value, count]) => ({ value, name: label(value), label: `${label(value)} (${count})` }))
}

const DEFAULT_SPEC: LibraryFilterSpec = {
  query: '',
  status: 'all',
  types: [],
  tagIds: [],
  tagMatch: 'any',
  monitored: 'all',
  completeness: 'all',
  sort: 'added',
  sortDir: '',
  excludeTagIds: [],
  excludeGenres: [],
  untaggedOnly: false,
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
  qualityProfile: 'all',
  ratingState: 'all',
  ratingMin: 1,
  ratingMax: 10,
  communityRatingMin: 0,
  communityRatingMax: 10,
  readingStatuses: [],
  yearMin: null,
  yearMax: null,
  credits: [],
  publishers: [],
  rootFolderIds: [],
  incognito: 'all',
  muted: 'all',
  anime: 'all',
  numberingClash: 'all',
  overrides: [],
  addedWithinDays: null,
  groupBy: 'none',
}

/** The areas the active filters are counted in; their sum is what the Filters button badges. */
type FilterGroup = 'series' | 'tags' | 'ratings' | 'monitoring'

/** Quality profile filter value for series with no pin of their own. */
const DEFAULT_PROFILE_FILTER = 'default'

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

const RATING_STATE_VALUES = ['all', 'rated', 'unrated'] as const

const RATING_STATE_LABELS: Record<string, MessageDescriptor> = {
  all: msg`Any`,
  rated: msg`Rated`,
  unrated: msg`Unrated`,
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
  Remove: msg`Remove`,
  'Set monitoring': msg`Set monitoring`,
  'Quality profile': msg`Quality profile`,
  Move: msg`Move`,
  Rescan: msg`Rescan files`,
  Rename: msg`Rename files`,
  'Mark read': msg`Mark read`,
  'Mark unread': msg`Mark unread`,
  Incognito: msg`Incognito`,
  'Read file cleanup': msg`Clean up read files`,
  'Page conversion': msg`Page conversion`,
  Archive: msg({ message: 'Archive', context: 'series' }),
  Restore: msg({ message: 'Restore', context: 'series' }),
  'Remove files': msg`Remove files`,
  Delete: msg`Delete`,
}

/** Per-series settings the selection can set in one go, each with its own endpoint and busy key. */
type BulkSetting = 'Incognito' | 'Read file cleanup' | 'Page conversion'

const BULK_SETTING_ENDPOINTS: Record<BulkSetting, string> = {
  Incognito: 'incognito',
  'Read file cleanup': 'readcleanup',
  'Page conversion': 'pageconversion',
}

const BULK_SETTING_FIRST: Record<BulkSetting, string> = {
  Incognito: 'Off',
  'Read file cleanup': 'Default',
  'Page conversion': 'Default',
}

const MATCH_MODE_LABELS: Record<string, MessageDescriptor> = {
  any: msg`Any`,
  all: msg`All`,
}

// Keyed by scope: the two routes render the same component in the same place, and selection, open
// dialogs and the filter state must not carry from one view into the other.
export default function LibraryPage({ scope = 'library' }: { scope?: LibraryScope }) {
  return <LibraryBody key={scope} scope={scope} />
}

function LibraryBody({ scope }: { scope: LibraryScope }) {
  const archivedScope = scope === 'archived'
  const MEM = MEM_BY_SCOPE[scope]
  const renderLabel = useLabel()
  const { i18n, _ } = useLingui()
  const { t } = useLinguiMacro()
  const notificationOptions = useSeriesNotificationOptions()
  const [searchParams, setSearchParams] = useSearchParams()
  // A link's view wins over the one this browser remembers, without replacing what it remembers.
  const [viewMode, setViewMode] = useState<ViewMode>(
    () => viewFromParams(searchParams, VIEW_MODES) ?? readStored(LS_VIEW, VIEW_MODES, 'grid'),
  )
  // The table has no room for the archived view's restore and delete actions, so it shows the list.
  const shownView: ViewMode = archivedScope && viewMode === 'table' ? 'list' : viewMode
  const chooseView = (mode: ViewMode) => {
    setViewMode(mode)
    writeStored(LS_VIEW, mode)
  }
  const [tableColumns, setTableColumns] = useState<LibraryColumn[]>(readLibraryColumns)
  const [columnsOpen, setColumnsOpen] = useState(false)
  const changeColumns = (next: LibraryColumn[]) => {
    setTableColumns(next)
    writeLibraryColumns(next)
  }
  const [density, setDensity] = useState<Density>(() => readStored(LS_DENSITY, ['compact', 'default', 'comfortable'], 'default'))
  const { data: allSeries, isLoading, error, refetch: refetchSeries, isRefetching: isRefetchingSeries } = useSeries()
  // useSeries answers with every series; each view keeps only its own half.
  const series = useMemo(
    () => allSeries?.filter((s) => (s.archivedAt != null) === archivedScope),
    [allSeries, archivedScope],
  )
  const archivedCount = useMemo(
    () => (allSeries ?? []).reduce((n, s) => (s.archivedAt != null ? n + 1 : n), 0),
    [allSeries],
  )
  const { me, can } = useAuth()
  const { data: rootFolders } = useRootFolders()
  const { data: tags } = useTags()
  const { data: savedFilters } = useSavedFilters()
  const sourceLabel = useSourceLabel()
  const saveFilter = useSaveFilter()
  const deleteSavedFilter = useDeleteSavedFilter()
  const restoreSavedFilter = useRestoreSavedFilter()
  const reorderSavedFilters = useReorderSavedFilters()
  const setDefaultSavedFilter = useSetDefaultSavedFilter()
  const bulkTag = useBulkTag()
  const bulkNotifications = useBulkSetSeriesNotificationMode()
  const bulkReadingStatus = useBulkSetReadingStatus()
  const bulkUpgradeProfile = useBulkSetUpgradeProfile()
  const { data: upgradeProfiles } = useUpgradeProfiles()
  const autoMatch = useAutoMatchSources()
  const readTracking = useReadTracking()
  const stats = useLibraryStats()
  const bulkArchive = useBulkArchiveSeries()
  const restoreSeries = useRestoreSeries()
  const removeArchivedFiles = useRemoveArchivedFiles()
  const deleteSeries = useDeleteSeries()
  const rateSeries = useRateSeries()
  const canArchive = can('ArchiveSeries')
  const canDelete = can('DeleteSeries')
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
  const scopeSorts = SCOPE_SORTS[scope]
  const defaultSort = scopeSorts[0]
  const [storedSort, setSort] = usePageState(`${MEM}:sort`, defaultSort)
  // A saved filter or a cleared spec can carry a sort this view does not offer.
  const sort = scopeSorts.includes(storedSort) ? storedSort : defaultSort
  // Empty means the key's own default, so switching key never leaves a stale direction behind.
  const [sortDirOverride, setSortDirOverride] = usePageState<SortDir | ''>(`${MEM}:sort-dir`, '')
  const sortDir: SortDir = sortDirOverride || SORT_DEFAULT_DIR[sort] || 'desc'
  const [statusFilter, setStatusFilter] = usePageState(`${MEM}:status`, 'all')
  const [typeFilter, setTypeFilter] = usePageState<string[]>(`${MEM}:types`, [])
  // Tag ids live as strings because that's what MultiSelect speaks.
  const [tagFilter, setTagFilter] = usePageState<string[]>(`${MEM}:tags`, [])
  const [tagMatch, setTagMatch] = usePageState(`${MEM}:tag-match`, 'any')
  const [excludeTagFilter, setExcludeTagFilter] = usePageState<string[]>(`${MEM}:exclude-tags`, [])
  const [untaggedOnly, setUntaggedOnly] = usePageState(`${MEM}:untagged`, false)
  const [genreFilter, setGenreFilter] = usePageState<string[]>(`${MEM}:genres`, [])
  const [excludeGenreFilter, setExcludeGenreFilter] = usePageState<string[]>(`${MEM}:exclude-genres`, [])
  const [genreMatch, setGenreMatch] = usePageState(`${MEM}:genre-match`, 'any')
  const [metaTagFilter, setMetaTagFilter] = usePageState<string[]>(`${MEM}:meta-tags`, [])
  const [metaTagMatch, setMetaTagMatch] = usePageState(`${MEM}:meta-tag-match`, 'any')
  const [contentRatingFilter, setContentRatingFilter] = usePageState<string[]>(`${MEM}:content-ratings`, [])
  const [readingStatusFilter, setReadingStatusFilter] = usePageState<string[]>(`${MEM}:reading-statuses`, [])
  const [readRange, setReadRange] = usePageState<[number, number]>(`${MEM}:read-range`, [0, 100])
  const [ratingState, setRatingState] = usePageState(`${MEM}:rating-state`, 'all')
  const [ratingRange, setRatingRange] = usePageState<[number, number]>(`${MEM}:rating-range`, [1, 10])
  const [communityRange, setCommunityRange] = usePageState<[number, number]>(`${MEM}:community-rating-range`, [0, 10])
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
  const [qualityProfileFilter, setQualityProfileFilter] = usePageState(`${MEM}:quality-profile`, 'all')
  const [yearMin, setYearMin] = usePageState<number | null>(`${MEM}:year-min`, null)
  const [yearMax, setYearMax] = usePageState<number | null>(`${MEM}:year-max`, null)
  const [creditFilter, setCreditFilter] = usePageState<string[]>(`${MEM}:credits`, [])
  const [publisherFilter, setPublisherFilter] = usePageState<string[]>(`${MEM}:publishers`, [])
  const [rootFolderFilter, setRootFolderFilter] = usePageState<string[]>(`${MEM}:root-folders`, [])
  const [incognitoFilter, setIncognitoFilter] = usePageState(`${MEM}:incognito`, 'all')
  const [mutedFilter, setMutedFilter] = usePageState(`${MEM}:muted`, 'all')
  const [animeFilter, setAnimeFilter] = usePageState(`${MEM}:anime`, 'all')
  const [clashFilter, setClashFilter] = usePageState(`${MEM}:numbering-clash`, 'all')
  const [overrideFilter, setOverrideFilter] = usePageState<string[]>(`${MEM}:overrides`, [])
  const [addedWithin, setAddedWithin] = usePageState<number | null>(`${MEM}:added-within`, null)
  const [storedGroupBy, setGroupBy] = usePageState<string>(`${MEM}:group-by`, 'none')
  const groupBy: GroupBy = (GROUP_BY_VALUES as readonly string[]).includes(storedGroupBy)
    ? (storedGroupBy as GroupBy)
    : 'none'
  const collapsedGroups = useCollapsedGroups(groupBy)
  const [activeFilterId, setActiveFilterId] = usePageState<number | null>(`${MEM}:saved-filter`, null)
  const [saveFilterOpen, setSaveFilterOpen] = useState(false)
  const [renameTarget, setRenameTarget] = useState<SavedFilterDto | null>(null)
  const [renameValue, setRenameValue] = useState('')
  // Read before usePageState's effects store anything: a tab that has never opened this view has
  // nothing remembered, which is when the default preset applies.
  // A link's own view wins over what this tab remembered, and over the default preset.
  const [urlSpec] = useState(() => paramsToSpec(searchParams, SCOPE_SORTS[scope]))
  const [urlHydrated, setUrlHydrated] = useState(urlSpec === null)
  const [freshView] = useState(() => urlSpec === null && scope === 'library' && !hasPageState(`${MEM}:sort`))
  const defaultApplied = useRef(false)
  const [filterName, setFilterName] = useState('')
  const [filtersOpen, setFiltersOpen] = usePageState(`${MEM}:filters-open`, false)
  // Local to this device, so it is not part of a saved filter.
  const [onDeviceOnly, setOnDeviceOnly] = usePageState(`${MEM}:on-device`, false)
  const offlineSeries = useOfflineSeriesIds()
  const onDevice = onDeviceOnly && offlineSeries.size > 0
  // Offline, what this device can open leads and the rest is dimmed; online nothing changes.
  const isOffline = useConnectivity() === 'offline'
  const availableOffline = useOfflineAvailableSeriesIds(isOffline)
  const offlineOf = (id: number): OfflineAvailability | undefined =>
    !isOffline ? undefined : availableOffline.has(id) ? 'saved' : 'unavailable'
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
  const [profileModalOpen, setProfileModalOpen] = useState(false)
  const [bulkProfile, setBulkProfile] = useState<string>(DEFAULT_PROFILE_FILTER)
  const [monitorMode, setMonitorMode] = useState('All')
  const [notifyModalOpen, setNotifyModalOpen] = useState(false)
  const [readingStatusModalOpen, setReadingStatusModalOpen] = useState(false)
  const [bulkReadingStatusValue, setBulkReadingStatusValue] = useState<ReadingStatus>('Reading')
  const [notifyMode, setNotifyMode] = useState<SeriesNotificationMode>('Default')
  const notificationHelp = useSeriesNotificationHelp(notifyMode)
  const [moveModalOpen, setMoveModalOpen] = useState(false)
  const [moveTarget, setMoveTarget] = useState<string | null>(null)
  const [moveFiles, setMoveFiles] = useState(true)
  const [renameModalOpen, setRenameModalOpen] = useState(false)
  const [readStateModalOpen, setReadStateModalOpen] = useState(false)
  const [settingModal, setSettingModal] = useState<BulkSetting | null>(null)
  const [settingValue, setSettingValue] = useState('')
  const incognitoOptions = useIncognitoOptions()
  const [archiveModalOpen, setArchiveModalOpen] = useState(false)
  const [archiveRemoveFiles, setArchiveRemoveFiles] = useState(false)
  // Ids the recycle bin refused during a bulk archive with files; the dialog then offers to delete them for good.
  const [archiveBinRefused, setArchiveBinRefused] = useState<number[]>([])
  // Restore and file removal ask first in some cases; `series` null means the selection, otherwise one card.
  const [restoreAsk, setRestoreAsk] = useState<{ series: SeriesDto | null; removed: number } | null>(null)
  const [removeFilesAsk, setRemoveFilesAsk] = useState<{ series: SeriesDto | null; binRefused: boolean } | null>(null)
  const [deleteOne, setDeleteOne] = useState<{ series: SeriesDto; binRefused: boolean } | null>(null)
  const [deleteOneFiles, setDeleteOneFiles] = useState(false)

  const titlesOf = useMemo(
    () =>
      titleIndex(series ?? [], (s) => [s.title, s.displayTitle, s.originalTitle, ...(s.altTitleNames ?? [])]),
    [series],
  )

  // Home rails built from a saved filter run this same chain on the server
  // (src/Maki.Api/Services/LibraryFilterEvaluator.cs). Change one, change the other.
  const visible = useMemo(() => {
    let list = [...(series ?? [])]
    if (onDevice) list = list.filter((s) => offlineSeries.has(s.id))
    if (statusFilter !== 'all') list = list.filter((s) => s.status === statusFilter)
    if (typeFilter.length > 0) list = list.filter((s) => s.type != null && typeFilter.includes(s.type))
    if (readingStatusFilter.length > 0) list = list.filter((s) => readingStatusFilter.includes(s.readingStatus))
    if (untaggedOnly) {
      list = list.filter((s) => (s.tagIds ?? []).length === 0)
    } else {
      if (tagFilter.length > 0) {
        const wanted = tagFilter.map(Number)
        list = list.filter((s) => matches(wanted, s.tagIds, tagMatch))
      }
      if (excludeTagFilter.length > 0) {
        const unwanted = excludeTagFilter.map(Number)
        list = list.filter((s) => !unwanted.some((id) => (s.tagIds ?? []).includes(id)))
      }
    }
    if (genreFilter.length > 0) {
      list = list.filter((s) => matches(genreFilter, s.genres, genreMatch))
    }
    if (excludeGenreFilter.length > 0) {
      list = list.filter((s) => !excludeGenreFilter.some((g) => (s.genres ?? []).includes(g)))
    }
    if (metaTagFilter.length > 0) {
      list = list.filter((s) => matches(metaTagFilter, s.metadataTags, metaTagMatch))
    }
    if (contentRatingFilter.length > 0) {
      // A series not yet refreshed since the column was added has no rating to check against —
      // excluded rather than assumed safe, since this filter exists to gate content.
      list = list.filter((s) => s.contentRating != null && contentRatingFilter.includes(s.contentRating))
    }
    if (ratingState === 'unrated') {
      list = list.filter((s) => shownRating(s) == null)
    } else if (ratingState === 'rated') {
      list = list.filter((s) => {
        const rating = shownRating(s)
        return rating != null && rating >= ratingRange[0] && rating <= ratingRange[1]
      })
    }
    if (communityRange[0] > 0 || communityRange[1] < 10) {
      list = list.filter(
        (s) => s.communityRating != null && s.communityRating >= communityRange[0] && s.communityRating <= communityRange[1],
      )
    }
    if (monitoredFilter !== 'all') {
      list = list.filter((s) => s.monitored === (monitoredFilter === 'monitored'))
    }
    if (qualityProfileFilter !== 'all') {
      list = list.filter((s) =>
        qualityProfileFilter === DEFAULT_PROFILE_FILTER
          ? s.upgradeProfileId == null
          : String(s.upgradeProfileId) === qualityProfileFilter,
      )
    }
    if (completeness !== 'all') {
      list = list.filter((s) => (completeness === 'behind' ? missingCount(s) > 0 : missingCount(s) <= 0))
    }
    if (yearMin != null || yearMax != null) {
      list = list.filter(
        (s) => s.year != null && (yearMin == null || s.year >= yearMin) && (yearMax == null || s.year <= yearMax),
      )
    }
    if (creditFilter.length > 0) list = list.filter((s) => creditsOf(s).some((name) => creditFilter.includes(name)))
    if (publisherFilter.length > 0) {
      list = list.filter((s) => s.publisher != null && publisherFilter.includes(s.publisher))
    }
    if (rootFolderFilter.length > 0) list = list.filter((s) => rootFolderFilter.includes(String(s.rootFolderId)))
    if (incognitoFilter !== 'all') list = list.filter((s) => (s.incognito !== 'Off') === (incognitoFilter === 'on'))
    if (mutedFilter !== 'all') list = list.filter((s) => (s.notificationMode === 'Muted') === (mutedFilter === 'muted'))
    if (animeFilter !== 'all') list = list.filter((s) => s.hasAnime === (animeFilter === 'has'))
    if (clashFilter !== 'all') list = list.filter((s) => (s.numberingClash != null) === (clashFilter === 'clash'))
    if (overrideFilter.length > 0) {
      list = list.filter(
        (s) =>
          (overrideFilter.includes('readCleanup') && s.readFileCleanup !== 'Default') ||
          (overrideFilter.includes('pageConversion') && s.pageConversion !== 'Default'),
      )
    }
    if (addedWithin != null) {
      const cutoff = Date.now() - addedWithin * DAY_MS
      list = list.filter((s) => (toTime(s.added) ?? 0) >= cutoff)
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
        const pct = readPercent(s, readTracking)
        return pct >= readRange[0] && pct <= readRange[1]
      })
    }
    list.sort(compareSeries(sort, sortDir))
    // After the other filters, so a typo fallback only kicks in when nothing in the filtered view matches.
    list = fuzzyFilter(list, debouncedQuery, titlesOf)
    if (isOffline) {
      // A stable partition, so the chosen sort still orders each half.
      list = [...list.filter((s) => availableOffline.has(s.id)), ...list.filter((s) => !availableOffline.has(s.id))]
    }
    return list
  }, [
    series, titlesOf, debouncedQuery, statusFilter, typeFilter, tagFilter, tagMatch, genreFilter, genreMatch,
    excludeTagFilter, excludeGenreFilter, untaggedOnly,
    metaTagFilter, metaTagMatch, monitoredFilter, completeness, readRange, readTracking, sort, sortDir, contentRatingFilter, readingStatusFilter,
    sourceFilter, sourceMatch, sourceState, fileSourceFilter, fileSourceMatch,
    chapterMin, chapterMax, chapterMode, qualityProfileFilter, ratingState, ratingRange, communityRange,
    yearMin, yearMax, creditFilter, publisherFilter, rootFolderFilter, incognitoFilter, mutedFilter, animeFilter,
    clashFilter, overrideFilter, addedWithin,
    onDevice, offlineSeries, isOffline, availableOffline,
  ])

  // Sort runs first, so each group keeps it.
  const groups = useMemo(
    () => groupSeries(visible, groupBy, { tags: tags ?? [], rootFolders: rootFolders ?? [] }),
    [visible, groupBy, tags, rootFolders],
  )

  // Hidden when nothing carries a score (no dump, or no MangaBaka links), unless a saved filter
  // narrowed it anyway: a counted filter the user can't see or reset on its own would be a trap.
  const hasCommunityRatings =
    (series ?? []).some((s) => s.communityRating != null) || communityRange[0] > 0 || communityRange[1] < 10

  const statusOptions = useMemo(() => {
    const set = new Set((series ?? []).map((s) => s.status))
    return ['all', ...[...set].sort()]
  }, [series])

  // Only types some series actually carries, in the catalogue's order rather than alphabetical.
  const typeOptions = useMemo(() => {
    const present = new Set((series ?? []).map((s) => s.type))
    return TYPE_OPTIONS.filter((value) => present.has(value)).map((value) => ({
      value,
      label: renderLabel(TYPE_LABELS[value]),
    }))
  }, [series, renderLabel, i18n.locale])

  const tagOptions = useMemo(
    () => (tags ?? []).map((t) => ({ value: String(t.id), name: t.label, label: `${t.label} (${t.seriesCount})` })),
    [tags],
  )

  // A tag deleted or renamed elsewhere leaves an orphaned pill; drop stale ids once tags load.
  useEffect(() => {
    if (tags == null) return
    const known = new Set(tagOptions.map((o) => o.value))
    const dropStale = (current: string[]) => {
      const next = current.filter((id) => known.has(id))
      return next.length === current.length ? current : next
    }
    setTagFilter(dropStale)
    setExcludeTagFilter(dropStale)
  }, [tags, tagOptions, setTagFilter, setExcludeTagFilter])

  const creditOptions = useMemo(() => facetOptions(series, creditsOf), [series])
  const publisherOptions = useMemo(
    () => facetOptions(series, (s) => (s.publisher ? [s.publisher] : undefined)),
    [series],
  )
  const rootFolderOptions = useMemo(
    () => (rootFolders ?? []).map((f) => ({ value: String(f.id), label: f.path })),
    [rootFolders],
  )
  const overrideOptions = useMemo(
    () => OVERRIDE_VALUES.map((value) => ({ value, label: _(OVERRIDE_LABELS[value]) })),
    [_, i18n.locale],
  )
  const showRootFolderFilter = rootFolderOptions.length > 1 || rootFolderFilter.length > 0

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

  const readingStatusOptions = useMemo(
    () => READING_STATUSES.map((value) => ({ value, label: renderLabel(readingStatusVisual(value).label) })),
    [renderLabel, i18n.locale],
  )

  const groupByOptions = useMemo(
    () =>
      GROUP_BY_VALUES.filter((v) => v !== 'rootFolder' || (rootFolders ?? []).length > 1 || groupBy === 'rootFolder').map(
        (value) => ({ value, label: _(GROUP_BY_LABELS[value]) }),
      ),
    [rootFolders, groupBy, _, i18n.locale],
  )
  const sortOptions = useMemo(
    () => scopeSorts.map((value) => ({ value, label: _(SORT_LABELS[value]) })),
    [scopeSorts, _, i18n.locale],
  )
  const sourceStateOptions = useMemo(
    () => SOURCE_STATE_VALUES.map((value) => ({ value, label: _(SOURCE_STATE_LABELS[value]) })),
    [_, i18n.locale],
  )
  const chapterModeOptions = useMemo(
    () => CHAPTER_MODE_VALUES.map((value) => ({ value, label: _(CHAPTER_MODE_LABELS[value]) })),
    [_, i18n.locale],
  )
  const ratingStateOptions = useMemo(
    () => RATING_STATE_VALUES.map((value) => ({ value, label: _(RATING_STATE_LABELS[value]) })),
    [_, i18n.locale],
  )
  const matchModeOptions = useMemo(
    () => MATCH_MODE_VALUES.map((value) => ({ value, label: _(MATCH_MODE_LABELS[value]) })),
    [_, i18n.locale],
  )

  const filterValues: LibraryFilterValues = {
    status: statusFilter,
    types: typeFilter,
    contentRatings: contentRatingFilter,
    readingStatuses: readingStatusFilter,
    chapterMode,
    chapterMin,
    chapterMax,
    yearMin,
    yearMax,
    addedWithin,
    credits: creditFilter,
    publishers: publisherFilter,
    anime: animeFilter,
    clash: clashFilter,
    tags: tagFilter,
    tagMatch,
    excludeTags: excludeTagFilter,
    untaggedOnly,
    genres: genreFilter,
    genreMatch,
    excludeGenres: excludeGenreFilter,
    metaTags: metaTagFilter,
    metaTagMatch,
    ratingState,
    ratingRange,
    communityRange,
    readRange,
    monitored: monitoredFilter,
    qualityProfile: qualityProfileFilter,
    completeness,
    sourceState,
    sources: sourceFilter,
    sourceMatch,
    fileSources: fileSourceFilter,
    fileSourceMatch,
    rootFolders: rootFolderFilter,
    incognito: incognitoFilter,
    muted: mutedFilter,
    overrides: overrideFilter,
  }
  const filterSetters: LibraryFilterSetters = {
    status: setStatusFilter,
    types: setTypeFilter,
    contentRatings: setContentRatingFilter,
    readingStatuses: setReadingStatusFilter,
    chapterMode: setChapterMode,
    chapterMin: setChapterMin,
    chapterMax: setChapterMax,
    yearMin: setYearMin,
    yearMax: setYearMax,
    addedWithin: setAddedWithin,
    credits: setCreditFilter,
    publishers: setPublisherFilter,
    anime: setAnimeFilter,
    clash: setClashFilter,
    tags: setTagFilter,
    tagMatch: setTagMatch,
    excludeTags: setExcludeTagFilter,
    untaggedOnly: setUntaggedOnly,
    genres: setGenreFilter,
    genreMatch: setGenreMatch,
    excludeGenres: setExcludeGenreFilter,
    metaTags: setMetaTagFilter,
    metaTagMatch: setMetaTagMatch,
    ratingState: setRatingState,
    ratingRange: setRatingRange,
    communityRange: setCommunityRange,
    readRange: setReadRange,
    monitored: setMonitoredFilter,
    qualityProfile: setQualityProfileFilter,
    completeness: setCompleteness,
    sourceState: setSourceState,
    sources: setSourceFilter,
    sourceMatch: setSourceMatch,
    fileSources: setFileSourceFilter,
    fileSourceMatch: setFileSourceMatch,
    rootFolders: setRootFolderFilter,
    incognito: setIncognitoFilter,
    muted: setMutedFilter,
    overrides: setOverrideFilter,
  }
  const filterOptions: LibraryFilterOptions = {
    statuses: statusOptions
      .filter((s) => s !== 'all')
      .map((s) => ({ value: s, label: renderLabel(seriesStatusVisual(s).label) })),
    types: typeOptions,
    contentRatings: contentRatingOptions,
    readingStatuses: readingStatusOptions,
    credits: creditOptions,
    publishers: publisherOptions,
    tags: tagOptions,
    genres: genreOptions,
    metaTags: metaTagOptions,
    sources: sourceOptions,
    fileSources: fileSourceOptions,
    rootFolders: rootFolderOptions,
    overrides: overrideOptions,
    qualityProfiles: [
      { value: 'all', label: t`Any` },
      { value: DEFAULT_PROFILE_FILTER, label: t`Instance default` },
      ...(upgradeProfiles ?? []).map((p) => ({ value: String(p.id), label: p.name })),
    ],
    sourceStates: sourceStateOptions,
    chapterModes: chapterModeOptions,
    ratingStates: ratingStateOptions,
    matchModes: matchModeOptions,
  }

  const currentSpec = (): LibraryFilterSpec => ({
    query,
    status: statusFilter,
    types: typeFilter,
    tagIds: untaggedOnly ? [] : tagFilter.map(Number),
    tagMatch,
    monitored: monitoredFilter,
    completeness,
    sort,
    sortDir: sortDirOverride,
    genres: genreFilter,
    excludeTagIds: untaggedOnly ? [] : excludeTagFilter.map(Number),
    excludeGenres: excludeGenreFilter,
    untaggedOnly,
    genreMatch,
    metadataTags: metaTagFilter,
    metadataTagMatch: metaTagMatch,
    readMin: readRange[0],
    readMax: readRange[1],
    chapterMin,
    chapterMax,
    chapterMode,
    contentRatings: contentRatingFilter,
    readingStatuses: readingStatusFilter,
    sources: sourceFilter,
    sourceMatch,
    sourceState,
    fileSources: fileSourceFilter,
    fileSourceMatch,
    qualityProfile: qualityProfileFilter,
    ratingState,
    ratingMin: ratingRange[0],
    ratingMax: ratingRange[1],
    communityRatingMin: communityRange[0],
    communityRatingMax: communityRange[1],
    yearMin,
    yearMax,
    credits: creditFilter,
    publishers: publisherFilter,
    rootFolderIds: rootFolderFilter.map(Number),
    incognito: incognitoFilter,
    muted: mutedFilter,
    anime: animeFilter,
    numberingClash: clashFilter,
    overrides: overrideFilter,
    addedWithinDays: addedWithin,
    groupBy,
  })

  const applySpec = (spec: LibraryFilterSpec, id: number | null) => {
    // Spread over the defaults so a preset saved by an older build (no genres, no read range)
    // still applies cleanly instead of writing undefined into the filter state.
    const merged = { ...DEFAULT_SPEC, ...spec }
    setQuery(merged.query ?? '')
    setStatusFilter(merged.status)
    setTypeFilter(merged.types ?? [])
    setTagFilter((merged.tagIds ?? []).map(String))
    setTagMatch(merged.tagMatch)
    setGenreFilter(merged.genres ?? [])
    setExcludeTagFilter((merged.excludeTagIds ?? []).map(String))
    setExcludeGenreFilter(merged.excludeGenres ?? [])
    setUntaggedOnly(merged.untaggedOnly ?? false)
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
    setReadingStatusFilter(merged.readingStatuses ?? [])
    setMonitoredFilter(merged.monitored)
    setCompleteness(merged.completeness)
    setSourceFilter(merged.sources ?? [])
    setSourceMatch(merged.sourceMatch)
    setSourceState(merged.sourceState)
    setFileSourceFilter(merged.fileSources ?? [])
    setFileSourceMatch(merged.fileSourceMatch)
    setQualityProfileFilter(merged.qualityProfile ?? 'all')
    setRatingState(merged.ratingState ?? 'all')
    setRatingRange([merged.ratingMin ?? 1, merged.ratingMax ?? 10])
    setCommunityRange([merged.communityRatingMin ?? 0, merged.communityRatingMax ?? 10])
    setYearMin(merged.yearMin ?? null)
    setYearMax(merged.yearMax ?? null)
    setCreditFilter(merged.credits ?? [])
    setPublisherFilter(merged.publishers ?? [])
    setRootFolderFilter((merged.rootFolderIds ?? []).map(String))
    setIncognitoFilter(merged.incognito ?? 'all')
    setMutedFilter(merged.muted ?? 'all')
    setAnimeFilter(merged.anime ?? 'all')
    setClashFilter(merged.numberingClash ?? 'all')
    setOverrideFilter(merged.overrides ?? [])
    setAddedWithin(merged.addedWithinDays ?? null)
    setGroupBy(merged.groupBy ?? 'none')
    setSort(merged.sort)
    setSortDirOverride(merged.sortDir === 'asc' || merged.sortDir === 'desc' ? merged.sortDir : '')
    setActiveFilterId(id)
  }

  /** Active filters per drawer group. Their sum, without the search box, is what "Filters" badges. */
  const groupCounts: Record<FilterGroup, number> = {
    series:
      (statusFilter !== 'all' ? 1 : 0) +
      (typeFilter.length > 0 ? 1 : 0) +
      (contentRatingFilter.length > 0 ? 1 : 0) +
      (readingStatusFilter.length > 0 ? 1 : 0) +
      (chapterMin != null || chapterMax != null ? 1 : 0) +
      (yearMin != null || yearMax != null ? 1 : 0) +
      (creditFilter.length > 0 ? 1 : 0) +
      (publisherFilter.length > 0 ? 1 : 0) +
      (animeFilter !== 'all' ? 1 : 0) +
      (clashFilter !== 'all' ? 1 : 0) +
      (addedWithin != null ? 1 : 0),
    tags:
      (!untaggedOnly && tagFilter.length > 0 ? 1 : 0) +
      (!untaggedOnly && excludeTagFilter.length > 0 ? 1 : 0) +
      (untaggedOnly ? 1 : 0) +
      (genreFilter.length > 0 ? 1 : 0) +
      (excludeGenreFilter.length > 0 ? 1 : 0) +
      (metaTagFilter.length > 0 ? 1 : 0),
    ratings:
      (ratingState !== 'all' ? 1 : 0) +
      (communityRange[0] > 0 || communityRange[1] < 10 ? 1 : 0) +
      (readRange[0] > 0 || readRange[1] < 100 ? 1 : 0),
    monitoring:
      (monitoredFilter !== 'all' ? 1 : 0) +
      (completeness !== 'all' ? 1 : 0) +
      (qualityProfileFilter !== 'all' ? 1 : 0) +
      (sourceState !== 'all' ? 1 : 0) +
      (sourceFilter.length > 0 ? 1 : 0) +
      (fileSourceFilter.length > 0 ? 1 : 0) +
      (rootFolderFilter.length > 0 ? 1 : 0) +
      (incognitoFilter !== 'all' ? 1 : 0) +
      (mutedFilter !== 'all' ? 1 : 0) +
      (overrideFilter.length > 0 ? 1 : 0),
  }
  const activeFilterCount = Object.values(groupCounts).reduce((a, b) => a + b, 0)
  const specActive = query.trim() !== '' || activeFilterCount > 0

  const defaultFilter = (savedFilters ?? []).find((f) => f.isDefault)
  useEffect(() => {
    if (!freshView || defaultApplied.current || savedFilters == null) return
    defaultApplied.current = true
    // Anything the user already did while the presets loaded wins over the default.
    if (defaultFilter && !specActive && activeFilterId === null) applySpec(defaultFilter.spec, defaultFilter.id)
  }, [freshView, savedFilters])

  useLayoutEffect(() => {
    if (urlHydrated || urlSpec === null) return
    applySpec({ ...DEFAULT_SPEC, ...urlSpec }, null)
    setUrlHydrated(true)
  }, [])

  // The address bar follows the view: replaced, never pushed, and only once the search box has
  // settled so typing does not rewrite it on every keystroke.
  const viewParams = (() => {
    const params = specToParams({ ...currentSpec(), query }, { ...DEFAULT_SPEC, sort: defaultSort })
    if (shownView !== 'grid') params.set(VIEW_PARAM, shownView)
    return params.toString()
  })()
  useEffect(() => {
    if (!urlHydrated || query !== debouncedQuery) return
    if (viewParams === searchParams.toString()) return
    setSearchParams(new URLSearchParams(viewParams), { replace: true })
  }, [viewParams, urlHydrated, query, debouncedQuery, setSearchParams])

  const moveSavedFilter = (f: SavedFilterDto, by: -1 | 1) => {
    const ids = (savedFilters ?? []).map((x) => x.id)
    const from = ids.indexOf(f.id)
    const to = from + by
    if (from === -1 || to < 0 || to >= ids.length) return
    ;[ids[from], ids[to]] = [ids[to], ids[from]]
    reorderSavedFilters.mutate(ids)
  }
  const filtersActive = specActive || onDevice

  // Stable across renders so the memoized CoverCards aren't invalidated by a fresh closure,
  // which is why the card takes the id as an argument rather than closing over it.
  const visibleRef = useRef(visible)
  const displayOrder = useMemo(
    () => (groups ?? []).filter((g) => !collapsedGroups.isCollapsed(g.key)).flatMap((g) => g.items),
    [groups, collapsedGroups.isCollapsed],
  )
  useEffect(() => {
    visibleRef.current = groups == null ? visible : displayOrder
  }, [visible, groups, displayOrder])
  /** Where the last plain click landed, the fixed end of a shift-range. */
  const selectAnchor = useRef<number | null>(null)

  const toggle = useCallback((id: number, shiftKey = false) => {
    const ids = visibleRef.current.map((s) => s.id)
    const anchor = selectAnchor.current
    const from = anchor === null ? -1 : ids.indexOf(anchor)
    const to = ids.indexOf(id)
    if (shiftKey && from !== -1 && to !== -1) {
      // Shift-clicking drags a text selection across the cards it spans.
      window.getSelection()?.removeAllRanges()
      const [lo, hi] = from <= to ? [from, to] : [to, from]
      const range = ids.slice(lo, hi + 1)
      // The anchor stays put, so moving the far end re-draws the range from the same start.
      setSelected((s) => new Set([...s, ...range]))
      return
    }
    selectAnchor.current = id
    setSelected((s) => {
      const next = new Set(s)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })
  }, [])

  const exitSelectMode = () => {
    selectAnchor.current = null
    setSelectMode(false)
    setSelected(new Set())
  }

  const removeSavedFilter = (f: SavedFilterDto) => {
    deleteSavedFilter.mutate(f.id, {
      onSuccess: () => {
        const name = f.name
        const toastId = notifications.show({
          autoClose: 8000,
          message: (
            <Group gap="xs" wrap="nowrap" justify="space-between">
              <Text size="sm">
                <Trans>Deleted saved filter {name}.</Trans>
              </Text>
              <Button
                size="xs"
                variant="subtle"
                style={{ flexShrink: 0 }}
                onClick={() => {
                  notifications.hide(toastId)
                  restoreSavedFilter.mutate(f, {
                    onError: (err) =>
                      notifications.show({
                        color: 'var(--danger)',
                        message: now`Failed to restore filter: ${errorText(err)}`,
                      }),
                  })
                }}
              >
                <Trans>Undo</Trans>
              </Button>
            </Group>
          ),
        })
      },
    })
    if (activeFilterId === f.id) setActiveFilterId(null)
  }

  /**
   * Runs an action against every selected series with a live progress notification. A function is
   * called once per series from here; a `SeriesBulkRequest` is queued on the server as one operation.
   */
  const runBulk = async (action: string, fn: ((id: number) => Promise<unknown>) | SeriesBulkRequest) => {
    const ids = [...selected]
    const total = ids.length
    // `action` is the busy-state key and stays English. What the toast shows is its label, or the
    // key itself if the table has no entry, so a missing label degrades to English rather than
    // to nothing.
    const name = BULK_ACTION_LABELS[action] ? _(BULK_ACTION_LABELS[action]) : action
    setBusy(action)
    const stop = new AbortController()
    let ok = 0
    let failed = 0
    let firstFailure: string | null = null
    let done = 0
    const progress = () => (
      <Group gap="xs" wrap="nowrap" justify="space-between">
        <Text size="sm">{`${name}: ${done}/${total}`}</Text>
        <Button
          size="xs"
          variant="subtle"
          style={{ flexShrink: 0 }}
          disabled={stop.signal.aborted}
          onClick={() => {
            stop.abort()
            update()
          }}
        >
          {stop.signal.aborted ? <Trans>Stopping</Trans> : <Trans>Stop</Trans>}
        </Button>
      </Group>
    )
    const update = () =>
      notifications.update({
        id: 'bulk-action',
        loading: true,
        message: progress(),
        autoClose: false,
        withCloseButton: false,
      })
    notifications.show({
      id: 'bulk-action',
      loading: true,
      message: progress(),
      autoClose: false,
      withCloseButton: false,
    })
    if (typeof fn === 'function') {
      for (const id of ids) {
        if (stop.signal.aborted) break
        try {
          await fn(id)
          ok++
        } catch (err) {
          failed++
          firstFailure ??= errorText(err)
        }
        done = ok + failed
        update()
      }
    } else {
      try {
        await runSeriesBulk(
          fn,
          ids,
          (count, result) => {
            ok = result.succeeded
            failed = count - result.succeeded
            firstFailure = result.firstError ?? (result.skipped > 0 ? now`Series not found` : null)
            done = count
            update()
          },
          stop.signal,
        )
      } catch (err) {
        failed = total - ok
        firstFailure ??= errorText(err)
      }
    }
    const stoppedEarly = stop.signal.aborted && ok + failed < total
    const firstError = firstFailure ?? ''
    notifications.update({
      id: 'bulk-action',
      loading: false,
      color: failed ? 'var(--warn)' : 'var(--ok)',
      // The fixed wording is translated; `firstError` carries a raw exception message untouched.
      message: failed
        ? stoppedEarly
          ? now`${name}: stopped, ${ok}/${total} succeeded, first error: ${firstError}`
          : now`${name}: ${ok}/${total} succeeded, first error: ${firstError}`
        : stoppedEarly
          ? now`${name}: stopped after ${ok}/${total}`
          : now`${name}: ${ok}/${total} succeeded`,
      autoClose: 8000,
      withCloseButton: true,
    })
    setBusy(null)
    void queryClient.invalidateQueries({ queryKey: ['series'] })
    void queryClient.invalidateQueries({ queryKey: ['chapters'] })
  }

  // runBulk refreshes the list and chapters; archiving, restoring and removing files also touch these.
  const invalidateArchiveSide = () => {
    void queryClient.invalidateQueries({ queryKey: ['recycle-bin'] })
    void queryClient.invalidateQueries({ queryKey: ['series-files'] })
    for (const key of affectedKeys) void queryClient.invalidateQueries({ queryKey: [key] })
  }
  const seriesById = (id: number) => allSeries?.find((s) => s.id === id)

  const { mutate: restoreMutate } = restoreSeries
  const restoreOne = useCallback(
    (id: number, redownloadRemoved: boolean) =>
      restoreMutate(
        { id, redownloadRemoved },
        {
          onSuccess: (result) => {
            setRestoreAsk(null)
            showRestoreResult(result, redownloadRemoved)
          },
        },
      ),
    [restoreMutate],
  )

  const runRestoreBulk = (redownload: boolean) => {
    setRestoreAsk(null)
    let queued = 0
    let queueError: string | null = null
    void runBulk('Restore', async (id) => {
      const result = await api<RestoreSeriesResponse>(`/series/${id}/restore`, {
        method: 'POST',
        body: JSON.stringify({ redownloadRemoved: redownload && needsRedownloadChoice(seriesById(id)) }),
      })
      queued += result.queued
      queueError ??= result.queueError
    }).then(() => {
      if (redownload || queueError) showBulkRestoreQueued(queued, queueError)
      invalidateArchiveSide()
      exitSelectMode()
    })
  }

  const restoreSelected = () => {
    const removed = [...selected]
      .map(seriesById)
      .reduce((n, s) => (s && needsRedownloadChoice(s) ? n + s.removedChapterCount : n), 0)
    if (removed > 0) setRestoreAsk({ series: null, removed })
    else runRestoreBulk(false)
  }

  // Stable so the memoized archived cards don't re-render on every library change; the series is
  // looked up at call time, since whether the stars set a "so far" score depends on its progress.
  const { onRate } = useStableActions({
    onRate: (seriesId: number, rating: number | null) => {
      const series = seriesById(seriesId)
      if (!series) return
      rateSeries.rate(series, rating, (soFar) =>
        notifications.show({
          color: 'var(--ok)',
          message:
            rating === null ? now`Rating cleared` : soFar ? now`Rated ${rating}/10 so far` : now`Rated ${rating}/10`,
        }),
      )
    },
  })

  const onCardAction = useCallback(
    (action: ArchivedCardAction, s: SeriesDto) => {
      if (action === 'restore') {
        if (needsRedownloadChoice(s)) setRestoreAsk({ series: s, removed: s.removedChapterCount })
        else restoreOne(s.id, false)
      } else if (action === 'removeFiles') {
        setRemoveFilesAsk({ series: s, binRefused: false })
      } else {
        setDeleteOneFiles(false)
        setDeleteOne({ series: s, binRefused: false })
      }
    },
    [restoreOne],
  )

  const removeFilesOne = (s: SeriesDto, permanent: boolean) =>
    removeArchivedFiles.mutate(
      { id: s.id, permanent },
      {
        onSuccess: () => {
          setRemoveFilesAsk(null)
          notifications.show({ color: 'var(--ok)', message: now`Files removed` })
        },
        onError: (err) => {
          if (offersPermanentDelete(err)) setRemoveFilesAsk({ series: s, binRefused: true })
        },
      },
    )

  const removeFilesSelected = () => {
    setRemoveFilesAsk(null)
    void runBulk('Remove files', (id) =>
      (seriesById(id)?.chapterFileCount ?? 0) > 0
        ? api(`/series/${id}/archive/removefiles`, { method: 'POST', body: JSON.stringify({ permanent: false }) })
        : Promise.resolve(),
    ).then(invalidateArchiveSide)
  }

  const deleteOneNow = (permanent: boolean) => {
    if (!deleteOne) return
    const target = deleteOne.series
    deleteSeries.mutate(
      { id: target.id, deleteFiles: deleteOneFiles, permanent },
      {
        onSuccess: () => {
          setDeleteOne(null)
          notifications.show({ color: 'var(--ok)', message: now`Series removed` })
        },
        onError: (err) => {
          if (offersPermanentDelete(err)) setDeleteOne({ series: target, binRefused: true })
        },
      },
    )
  }

  /** Undo for a bulk archive that kept the files: puts each one back, stopping at the first failure. */
  const undoArchive = async (ids: number[]) => {
    try {
      for (const id of ids) {
        await api(`/series/${id}/restore`, { method: 'POST', body: JSON.stringify({ redownloadRemoved: false }) })
      }
    } catch (err) {
      notifications.show({ color: 'var(--danger)', message: errorText(err) })
    }
    void queryClient.invalidateQueries({ queryKey: ['series'] })
    void queryClient.invalidateQueries({ queryKey: ['chapters'] })
    invalidateArchiveSide()
  }

  const archiveSelected = (permanent = false) => {
    const ids = permanent ? archiveBinRefused : [...selected]
    const removeFiles = archiveRemoveFiles && canDelete
    bulkArchive.mutate(
      { seriesIds: ids, removeFiles, permanent },
      {
        onSuccess: ({ results }) => {
          const archived = results.filter((r) => r.ok).map((r) => r.seriesId)
          const binRefused = results.filter((r) => !r.ok && r.permanentDeleteAvailable).map((r) => r.seriesId)
          const refused = results.filter((r) => !r.ok && !r.permanentDeleteAvailable)
          setArchiveBinRefused(binRefused)
          if (binRefused.length === 0) {
            setArchiveModalOpen(false)
            exitSelectMode()
          } else {
            // The archived ones leave this view; only the refused ones stay picked for the retry.
            setSelected(new Set(binRefused))
          }
          const name = _(BULK_ACTION_LABELS.Archive)
          const ok = archived.length
          const total = ids.length
          const firstError = refused[0]?.error ?? refused[0]?.code ?? ''
          if (refused.length > 0) {
            notifications.show({
              color: 'var(--warn)',
              autoClose: 8000,
              message: now`${name}: ${ok}/${total} succeeded, first error: ${firstError}`,
            })
          } else if (ok > 0) {
            const toastId = notifications.show({
              color: 'var(--ok)',
              autoClose: removeFiles ? 4000 : 8000,
              message: (
                <Group gap="xs" wrap="nowrap" justify="space-between">
                  <Text size="sm">
                    <Plural value={ok} one="Archived # series" other="Archived # series" />
                  </Text>
                  {!removeFiles && (
                    <Button
                      size="xs"
                      variant="subtle"
                      style={{ flexShrink: 0 }}
                      onClick={() => {
                        notifications.hide(toastId)
                        void undoArchive(archived)
                      }}
                    >
                      <Trans>Undo</Trans>
                    </Button>
                  )}
                </Group>
              ),
            })
          }
        },
      },
    )
  }


  const openSetting = (setting: BulkSetting) => {
    setSettingValue(BULK_SETTING_FIRST[setting])
    setSettingModal(setting)
  }

  const runReadState = (state: 'read' | 'unread') => {
    setReadStateModalOpen(false)
    void runBulk(state === 'read' ? 'Mark read' : 'Mark unread', (id) =>
      api(`/reader/series/${id}/state`, { method: 'POST', body: JSON.stringify({ state }) }),
    ).then(() => {
      void queryClient.invalidateQueries({ queryKey: ['reader-progress'] })
      void queryClient.invalidateQueries({ queryKey: ['reader-continue'] })
      void queryClient.invalidateQueries({ queryKey: ['home'] })
      void queryClient.invalidateQueries({ queryKey: ['progress'] })
    })
  }

  const settingOptions: Record<BulkSetting, { value: string; label: ReactNode; hint?: ReactNode }[]> = {
    Incognito: incognitoOptions.map((o) => ({
      value: o.value,
      label: o.label,
      hint: INCOGNITO_HELP[o.value] && _(INCOGNITO_HELP[o.value]),
    })),
    'Read file cleanup': [
      { value: 'Default', label: <Trans>Use the Downloads setting</Trans> },
      {
        value: 'On',
        label: <Trans>On</Trans>,
        hint: <Trans>Each chapter's file is deleted for good some days after everyone has read it.</Trans>,
      },
      { value: 'Off', label: <Trans>Off</Trans>, hint: <Trans>Read files stay on disk.</Trans> },
    ],
    'Page conversion': [
      {
        value: 'Default',
        label: <Trans>Follow the rules</Trans>,
        hint: <Trans>The page conversion rules in Settings decide.</Trans>,
      },
      { value: 'Never', label: <Trans>Never</Trans>, hint: <Trans>Pages stay as they were downloaded.</Trans> },
      ...PAGE_CONVERSION_STOPS.map((stop) => ({
        value: stop.charAt(0).toUpperCase() + stop.slice(1),
        label: _(STOP_LABELS[stop]),
        hint: _(STOP_HINTS[stop]),
      })),
    ],
  }
  const settingTitles: Record<BulkSetting, string> = {
    Incognito: plural(selected.size, { one: 'Set incognito for # series', other: 'Set incognito for # series' }),
    'Read file cleanup': plural(selected.size, {
      one: 'Clean up read files for # series',
      other: 'Clean up read files for # series',
    }),
    'Page conversion': plural(selected.size, {
      one: 'Set page conversion for # series',
      other: 'Set page conversion for # series',
    }),
  }

  // `key` is compared against `busy` (see runBulk) and must stay an untranslated identifier;
  // `label` is what actually renders, so it's the only part that gets translated.
  // In priority order: the first four the user may run are buttons, the rest sit under More.
  // Remove is not in here; it stays a button of its own, apart from the rest.
  type BulkAction = { key: string; label: ReactNode; icon: ReactNode; run: () => void }
  const archivedBulkActions: (BulkAction | false)[] = [
    canArchive && {
      key: 'Restore',
      label: <Trans context="series">Restore</Trans>,
      icon: <IconArchiveOff size={16} />,
      run: restoreSelected,
    },
    canDelete && {
      key: 'Remove files',
      label: <Trans>Remove files</Trans>,
      icon: <IconFileOff size={16} />,
      run: () => setRemoveFilesAsk({ series: null, binRefused: false }),
    },
  ]
  const bulkActions: BulkAction[] = (archivedScope ? archivedBulkActions : [
    can('DownloadChapters') && {
      key: 'Search missing',
      label: <Trans>Search missing</Trans>,
      icon: <IconSearch size={16} />,
      run: () => runBulk('Search missing', { path: '/series/searchmissing/bulk' }),
    },
    can('EditMetadata') && {
      key: 'Monitoring',
      label: <Trans>Monitoring</Trans>,
      icon: <IconEye size={16} />,
      run: () => setMonitorModalOpen(true),
    },
    can('ManageTags') && {
      key: 'Tags',
      label: <Trans>Tags</Trans>,
      icon: <IconTag size={16} />,
      run: () => {
        setTagsToAdd([])
        setTagsToRemove([])
        setTagModalOpen(true)
      },
    },
    can('EditMetadata') && {
      key: 'Refresh',
      label: <Trans>Refresh</Trans>,
      icon: <IconRefresh size={16} />,
      run: () => runBulk('Refresh', { path: '/series/refresh/bulk' }),
    },
    can('EditMetadata') && {
      key: 'Metadata',
      label: <Trans>Metadata</Trans>,
      icon: <IconPhoto size={16} />,
      run: () => runBulk('Metadata', { path: '/series/refreshmetadata/bulk' }),
    },
    can('EditMetadata') && {
      key: 'Rescan',
      label: <Trans>Rescan files</Trans>,
      icon: <IconScan size={16} />,
      run: () => runBulk('Rescan', { path: '/series/rescan/bulk' }),
    },
    can('ManageSources') && {
      key: 'Auto-match',
      label: <Trans>Auto-match</Trans>,
      icon: <IconWand size={16} />,
      run: () => setAutoMatchModalOpen(true),
    },
    can('EditMetadata') && {
      key: 'Quality profile',
      label: <Trans>Quality profile</Trans>,
      icon: <IconRanking size={16} />,
      run: () => {
        setBulkProfile(DEFAULT_PROFILE_FILTER)
        setProfileModalOpen(true)
      },
    },
    {
      key: 'Reading status',
      label: <Trans>Reading status</Trans>,
      icon: <IconBookmark size={16} />,
      run: () => setReadingStatusModalOpen(true),
    },
    {
      key: 'Notifications',
      label: <Trans>Notifications</Trans>,
      icon: <IconBell size={16} />,
      run: () => setNotifyModalOpen(true),
    },
    {
      key: 'Read state',
      label: <Trans>Mark read or unread</Trans>,
      icon: <IconBook size={16} />,
      run: () => setReadStateModalOpen(true),
    },
    can('EditMetadata') && {
      key: 'Incognito',
      label: <Trans>Incognito</Trans>,
      icon: <IconEyeOff size={16} />,
      run: () => openSetting('Incognito'),
    },
    // "ComicInfo" is the ComicInfo.xml format name, not translated (see rule 5).
    can('EditMetadata') && {
      key: 'ComicInfo',
      label: 'ComicInfo',
      icon: <IconFileText size={16} />,
      run: () => runBulk('ComicInfo', (id) => api(`/series/${id}/updatecomicinfo`, { method: 'POST' })),
    },
    can('EditMetadata') && {
      key: 'Move',
      label: <Trans>Move</Trans>,
      icon: <IconFolderSymlink size={16} />,
      run: () => {
        setMoveTarget(null)
        setMoveFiles(true)
        setMoveModalOpen(true)
      },
    },
    can('EditMetadata') && {
      key: 'Rename',
      label: <Trans>Rename files</Trans>,
      icon: <IconPencil size={16} />,
      run: () => setRenameModalOpen(true),
    },
    canDelete && {
      key: 'Read file cleanup',
      label: <Trans>Clean up read files</Trans>,
      icon: <IconTrashX size={16} />,
      run: () => openSetting('Read file cleanup'),
    },
    can('Admin') && {
      key: 'Page conversion',
      label: <Trans>Page conversion</Trans>,
      icon: <IconPhotoCog size={16} />,
      run: () => openSetting('Page conversion'),
    },
  ]).filter((a) => a !== false)
  // The danger button's busy key and toast name; it reads Delete in Archived, beside Remove files.
  const removeKey = archivedScope ? 'Delete' : 'Remove'
  const bulkArchiveOffered = !archivedScope && canArchive
  const openArchive = () => {
    setArchiveRemoveFiles(false)
    setArchiveBinRefused([])
    setArchiveModalOpen(true)
  }
  const primaryActions = bulkActions.slice(0, 4)
  const moreActions = bulkActions.slice(4)
  const bulkDisabled = (key: string) => selected.size === 0 || (busy !== null && busy !== key)

  // A series that drops out of `visible` (filter change, bulk tag removal, ...) must drop out of
  // `selected` too, or a later bulk action (Delete, optionally with files) still hits it even
  // though it's no longer shown as selected.
  useEffect(() => {
    const visibleIds = new Set(visible.map((s) => s.id))
    setSelected((prev) => {
      if (prev.size === 0) return prev
      let changed = false
      const next = new Set<number>()
      for (const id of prev) {
        if (visibleIds.has(id)) next.add(id)
        else changed = true
      }
      return changed ? next : prev
    })
  }, [visible])

  // Against the *filtered* set, not the whole library: "select all" under an active filter that
  // silently grabbed hidden series would make every bulk action a foot-gun. Series in a collapsed
  // group are hidden too.
  const selectable = groups == null ? visible : displayOrder
  const allSelected = selectable.length > 0 && selectable.every((s) => selected.has(s.id))
  const selectedCount = selected.size

  const windowOn = visible.length >= WINDOW_MIN_ITEMS

  // Hoisted so the "X of Y series" messages below get named placeholders instead of `{0}`/`{1}`.
  const visibleCount = formatNumber(visible.length)
  const totalCount = formatNumber(series?.length ?? 0)
  const totalSeries = series?.length ?? 0
  const shownCount = visible.length
  const loadErrorMessage = error ? (error instanceof Error ? error.message : String(error)) : null
  // Kept mounted through loading and error too, so the grid lands where it will stay and an error isn't a dead end.
  const showChrome = isLoading || error != null || (series != null && series.length > 0)

  const indexRef = useRef<HTMLDivElement>(null)
  useEffect(() => {
    const panel = indexRef.current
    const strip = panel?.querySelector<HTMLElement>('.library-index-metrics')
    if (!panel || !strip) return
    const apply = () => panel.style.setProperty('--library-strip-h', `${strip.offsetHeight}px`)
    apply()
    const observer = new ResizeObserver(apply)
    observer.observe(strip)
    return () => observer.disconnect()
  }, [showChrome])

  // Marks the toolbar while it is pinned, so it can draw the edge the grid scrolls under.
  useEffect(() => {
    const panel = indexRef.current
    if (!panel) return
    let frame = 0
    const check = () => {
      frame = 0
      const pinnedAt = parseFloat(getComputedStyle(panel).top) || 0
      if (panel.getBoundingClientRect().top <= pinnedAt + 0.5) panel.dataset.stuck = ''
      else delete panel.dataset.stuck
    }
    const schedule = () => {
      if (!frame) frame = requestAnimationFrame(check)
    }
    check()
    const stop = onPageScroll(schedule)
    window.addEventListener('resize', schedule)
    return () => {
      cancelAnimationFrame(frame)
      stop()
      window.removeEventListener('resize', schedule)
    }
  }, [showChrome])

  const reverseSort = () => {
    const next: SortDir = sortDir === 'asc' ? 'desc' : 'asc'
    setSortDirOverride(next === SORT_DEFAULT_DIR[sort] ? '' : next)
  }
  const sortByColumn = (value: string) => {
    if (value === sort) return reverseSort()
    setSort(value)
    setSortDirOverride('')
  }
  const tagsById = useMemo(() => new Map((tags ?? []).map((tag) => [tag.id, tag])), [tags])
  const profileNames = useMemo(
    () => new Map((upgradeProfiles ?? []).map((p) => [p.id, p.name])),
    [upgradeProfiles],
  )

  // The library's figures count monitoring and downloads, which archived series no longer do.
  const archiveCount = archiveBinRefused.length || selectedCount
  const seriesTitle = deleteOne?.series.displayTitle ?? ''
  const removeFilesTitle = removeFilesAsk?.series?.displayTitle ?? ''
  const archivedFilesRemoved = (series ?? []).reduce((n, s) => (needsRedownloadChoice(s) ? n + 1 : n), 0)
  const archivedFilesKept = (series ?? []).reduce((n, s) => (s.chapterFileCount > 0 ? n + 1 : n), 0)
  const archivedFigures = [
    { label: t({ message: 'Archived', context: 'series' }), value: series?.length ?? 0 },
    { label: t`Files kept`, value: archivedFilesKept, tone: 'ok' as const },
    { label: t`Files removed`, value: archivedFilesRemoved },
  ]

  // Absent while nothing is archived, so the library looks exactly as it did before archiving existed.
  const showScopeSwitch = (archivedScope || archivedCount > 0) && !selectMode
  const scopeSwitch = (phone: boolean) => (
    <SegmentedControl
      aria-label={t`Library view`}
      visibleFrom={phone ? undefined : 'sm'}
      hiddenFrom={phone ? 'sm' : undefined}
      fullWidth={phone}
      mb={phone ? 'md' : undefined}
      value={scope}
      onChange={(v) => navigate(v === 'archived' ? '/library/archived' : '/library')}
      data={[
        { value: 'library', label: t`Library` },
        {
          value: 'archived',
          label: <Plural value={archivedCount} one="Archived (#)" other="Archived (#)" context="series" />,
        },
      ]}
    />
  )

  const renderItems = (items: SeriesDto[], innerRef: (el: HTMLDivElement | null) => void, last: boolean) => {
    if (shownView === 'table') {
      return (
        <LibraryTableBody innerRef={innerRef}>
          {items.map((s) => (
            <LibraryTableRow
              key={s.id}
              series={s}
              columns={tableColumns}
              selectMode={selectMode}
              selected={selected.has(s.id)}
              readTracking={readTracking}
              onToggle={toggle}
              offline={offlineOf(s.id)}
              tagsById={tagsById}
              profileNames={profileNames}
              sourceLabel={sourceLabel}
            />
          ))}
        </LibraryTableBody>
      )
    }
    if (shownView === 'list') {
      return archivedScope ? (
        <ArchivedList
          ref={innerRef}
          series={items}
          selectMode={selectMode}
          selectedIds={selected}
          readTracking={readTracking}
          onToggle={toggle}
          canRestore={canArchive}
          canRemoveFiles={canDelete}
          canDelete={canDelete}
          onRate={onRate}
          onAction={onCardAction}
        />
      ) : (
        <Stack ref={innerRef} gap="xs">
          {items.map((s) => (
            <SeriesRow
              key={s.id}
              series={s}
              selectMode={selectMode}
              selected={selected.has(s.id)}
              readTracking={readTracking}
              density={density}
              onToggle={toggle}
              offline={offlineOf(s.id)}
            />
          ))}
        </Stack>
      )
    }
    return (
      <SimpleGrid ref={innerRef} cols={LIBRARY_POSTER_COLS_BY_DENSITY[density]} spacing="md">
        {items.map((s) =>
          archivedScope ? (
            <ArchivedSeriesCard
              key={s.id}
              series={s}
              variant="grid"
              density={density}
              selectMode={selectMode}
              selected={selected.has(s.id)}
              readTracking={readTracking}
              onToggle={toggle}
              offline={offlineOf(s.id)}
              canRestore={canArchive}
              canRemoveFiles={canDelete}
              canDelete={canDelete}
              onRate={onRate}
              onAction={onCardAction}
            />
          ) : (
            <CoverCard
              key={s.id}
              series={s}
              selectMode={selectMode}
              selected={selected.has(s.id)}
              readTracking={readTracking}
              onToggle={toggle}
              offline={offlineOf(s.id)}
              menu
            />
          ),
        )}
        {last && !archivedScope && !selectMode && (series?.length ?? 0) < 12 && (
          <Link
            to="/add"
            className="library-add-cell"
            aria-label={can('AddSeries') ? t`Add a series` : t`Request series`}
          >
            <IconPlus size={24} aria-hidden />
          </Link>
        )}
      </SimpleGrid>
    )
  }

  const itemsBody =
    groups == null ? (
      <WindowedItems items={visible} enabled={windowOn}>
        {(slice, innerRef) => renderItems(slice, innerRef, true)}
      </WindowedItems>
    ) : (
      <div className="library-groups" data-view={shownView}>
        {groups.map((group, i) => {
          const folded = collapsedGroups.isCollapsed(group.key)
          return (
            <Fragment key={group.key}>
              <LibraryGroupHeader
                group={group}
                collapsed={folded}
                onToggle={() => collapsedGroups.toggle(group.key)}
                asRow={shownView === 'table'}
              />
              {!folded && (
                <WindowedItems items={group.items} enabled={windowOn}>
                  {(slice, innerRef) => renderItems(slice, innerRef, i === groups.length - 1)}
                </WindowedItems>
              )}
            </Fragment>
          )
        })}
      </div>
    )

  return (
    <SurfaceFrame width="full" pageStyle="editorial">
      <PageHeader
        className="library-page-header"
        title={t`Library`}
        actions={
          showChrome && !selectMode ? (
            <>
              {showScopeSwitch && scopeSwitch(false)}
              <Button
                variant="default"
                className="library-select-button"
                aria-label={t`Select`}
                leftSection={<IconListCheck size={16} />}
                onClick={() => setSelectMode(true)}
              >
                <Trans>Select</Trans>
              </Button>
              {!archivedScope && (
                <Button
                  component={Link}
                  to="/add"
                  className="library-add-button"
                  aria-label={can('AddSeries') ? t`Add series` : t`Request series`}
                  leftSection={<IconPlus size={16} />}
                >
                  {can('AddSeries') ? <Trans>Add series</Trans> : <Trans>Request series</Trans>}
                </Button>
              )}
            </>
          ) : showScopeSwitch ? (
            scopeSwitch(false)
          ) : undefined
        }
      />
      {showScopeSwitch && scopeSwitch(true)}

      {showChrome && (
        <Panel ref={indexRef} p={0} className="library-index layer-sunken">
          <FigureStrip
            flush
            loading={isLoading}
            className="library-index-metrics"
            figures={archivedScope ? archivedFigures : [
              { label: t`Series`, value: stats.total },
              { label: t`Monitored`, value: stats.monitored, tip: t`Series Maki keeps checking for new chapters.` },
              { label: t`On disk`, value: stats.downloaded, tone: 'ok' },
              { label: t`Missing`, value: stats.missing, tone: 'warn', tip: t`Chapters you want that aren't on disk yet.` },
              ...(stats.inQueue > 0 ? [{ label: t`In queue`, value: stats.inQueue, tone: 'info' as const }] : []),
            ]}
          />

          {selectMode ? (
            <Group className="library-selection-bar" justify="space-between" wrap="wrap" gap="xs">
              <Group gap="xs">
                {/* On a phone Done is this icon beside the count; the text button is desktop only. */}
                <ActionIcon
                  hiddenFrom="sm"
                  variant="default"
                  disabled={busy !== null}
                  onClick={exitSelectMode}
                  aria-label={t`Done`}
                >
                  <IconX size={16} />
                </ActionIcon>
                <Text size="sm" c="var(--ink-3)" className="tnum">
                  <Plural value={selectedCount} one="# selected" other="# selected" />
                </Text>
                <Button
                  size="xs"
                  variant="subtle"
                  onClick={() => {
                    selectAnchor.current = null
                    setSelected(allSelected ? new Set() : new Set(selectable.map((s) => s.id)))
                  }}
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
                    plural(totalCount, {
                      one: `${visibleCount} of # series match`,
                      other: `${visibleCount} of # series match`,
                    })
                  ) : (
                    <Plural value={totalSeries} one="# series" other="# series" />
                  )}
                </Text>
              </Group>
              <Group gap="xs">
                {primaryActions.map((a) => (
                  <Button
                    key={a.key}
                    visibleFrom="sm"
                    size="xs"
                    variant="light"
                    leftSection={a.icon}
                    disabled={bulkDisabled(a.key)}
                    loading={busy === a.key}
                    onClick={a.run}
                  >
                    {a.label}
                  </Button>
                ))}
                {/* On a phone the primary four join the menu too, so the row is one menu and Remove. */}
                <Menu position="bottom-end" withinPortal>
                  <Menu.Target>
                    <Button
                      size="xs"
                      variant="default"
                      rightSection={<IconChevronDown size={16} />}
                      disabled={selected.size === 0 || (busy !== null && !moreActions.some((a) => a.key === busy))}
                      loading={moreActions.some((a) => a.key === busy)}
                      className={moreActions.length === 0 ? 'library-bulk-more-phone' : undefined}
                    >
                      <Text span inherit visibleFrom="sm">
                        <Trans>More</Trans>
                      </Text>
                      <Text span inherit hiddenFrom="sm">
                        <Trans>Actions</Trans>
                      </Text>
                    </Button>
                  </Menu.Target>
                  <Menu.Dropdown>
                    {primaryActions.map((a) => (
                      <Menu.Item key={a.key} hiddenFrom="sm" leftSection={a.icon} onClick={a.run}>
                        {a.label}
                      </Menu.Item>
                    ))}
                    {primaryActions.length > 0 && moreActions.length > 0 && <Menu.Divider hiddenFrom="sm" />}
                    {moreActions.map((a) => (
                      <Menu.Item key={a.key} leftSection={a.icon} onClick={a.run}>
                        {a.label}
                      </Menu.Item>
                    ))}
                    {/* On a phone Archive joins the menu, so the row stays one menu and Remove. */}
                    {bulkArchiveOffered && (
                      <>
                        <Menu.Divider hiddenFrom="sm" />
                        <Menu.Item hiddenFrom="sm" leftSection={<IconArchive size={16} />} onClick={openArchive}>
                          <Trans context="series">Archive</Trans>
                        </Menu.Item>
                      </>
                    )}
                  </Menu.Dropdown>
                </Menu>
                {(canDelete || bulkArchiveOffered) && (
                  <Divider orientation="vertical" className="library-bulk-divider" />
                )}
                {bulkArchiveOffered && (
                  <Button
                    visibleFrom="sm"
                    size="xs"
                    variant="light"
                    leftSection={<IconArchive size={16} />}
                    disabled={bulkDisabled('Archive')}
                    loading={bulkArchive.isPending}
                    onClick={openArchive}
                  >
                    <Trans context="series">Archive</Trans>
                  </Button>
                )}
                {canDelete && (
                  <Button
                    size="xs"
                    variant="light"
                    color="var(--danger)"
                    leftSection={<IconTrash size={16} />}
                    disabled={bulkDisabled(removeKey)}
                    loading={busy === removeKey}
                    onClick={() => {
                      setDeleteFiles(false)
                      setDeleteModalOpen(true)
                    }}
                  >
                    {archivedScope ? <Trans>Delete</Trans> : <Trans>Remove</Trans>}
                  </Button>
                )}
                <Button
                  visibleFrom="sm"
                  size="xs"
                  variant="default"
                  leftSection={<IconX size={16} />}
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
                <SearchInput
                  aria-label={t`Filter library…`}
                  className="library-search"
                  placeholder={t`Filter library…`}
                  value={query}
                  onClear={() => setQuery('')}
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
                {/* Field and direction as one control: the arrow only means something next to the field. */}
                <div className="library-sort-control">
                  <Select
                    aria-label={t`Sort by`}
                    className="library-sort"
                    data={sortOptions}
                    value={sort}
                    onChange={(v) => {
                      setSort(v ?? defaultSort)
                      setSortDirOverride('')
                    }}
                    comboboxProps={{ withinPortal: true }}
                  />
                  <Tooltip label={sortDir === 'asc' ? t`Ascending` : t`Descending`} withArrow>
                    <ActionIcon
                      variant="default"
                      size="input-sm"
                      aria-label={t`Reverse sort order`}
                      onClick={reverseSort}
                    >
                      {sortDir === 'asc' ? <IconSortAscending size={16} /> : <IconSortDescending size={16} />}
                    </ActionIcon>
                  </Tooltip>
                </div>
                <Select
                  aria-label={t`Group by`}
                  className="library-group-by"
                  visibleFrom="sm"
                  leftSection={<IconListTree size={16} />}
                  data={groupByOptions}
                  value={groupBy}
                  onChange={(v) => setGroupBy(v ?? 'none')}
                  comboboxProps={{ withinPortal: true }}
                />
                {filtersActive && !isLoading && (
                  <Text size="sm" c="var(--ink-3)" className="tnum library-toolbar-count">
                    {t`${visibleCount} of ${totalCount}`}
                  </Text>
                )}
                <Group className="library-view-controls" gap="sm" wrap="nowrap">
                  <Box visibleFrom="sm">
                    <ViewModeToggle value={shownView} withTable={!archivedScope} onChange={chooseView} />
                  </Box>
                  {shownView === 'table' && (
                    <Tooltip label={t`Columns`} withArrow>
                      <ActionIcon
                        visibleFrom="sm"
                        variant="default"
                        size="input-sm"
                        aria-label={t`Columns`}
                        onClick={() => setColumnsOpen(true)}
                      >
                        <IconColumns size={16} />
                      </ActionIcon>
                    </Tooltip>
                  )}
                  {!archivedScope && (
                    <LuckyButton
                      candidates={luckyPool}
                      onPick={(id) => navigate(`/series/${id}`, { state: { lucky: true } })}
                    />
                  )}
                  <Menu position="bottom-end" withinPortal>
                    <Menu.Target>
                      <ToolbarMenuButton />
                    </Menu.Target>
                    <Menu.Dropdown>
                      <Menu.Item leftSection={<IconSettings size={16} />} onClick={() => setTagManagerOpen(true)}>
                        <Trans>Manage tags</Trans>
                      </Menu.Item>
                      {/* A phone keeps search, Filters, the dice and this menu, so view and sort move in here. */}
                      <Box hiddenFrom="sm">
                        <Menu.Divider />
                        <Menu.Label>{t`View`}</Menu.Label>
                        {VIEW_MODES.filter((mode) => mode !== 'table' || !archivedScope).map((mode) => (
                          <Menu.Item
                            key={mode}
                            onClick={() => chooseView(mode)}
                            rightSection={shownView === mode ? <IconCheck size={16} /> : undefined}
                          >
                            {mode === 'grid' ? t`Grid view` : mode === 'list' ? t`List view` : t`Table view`}
                          </Menu.Item>
                        ))}
                        {shownView === 'table' && (
                          <Menu.Item leftSection={<IconTable size={16} />} onClick={() => setColumnsOpen(true)}>
                            {t`Columns`}
                          </Menu.Item>
                        )}
                        <Menu.Divider />
                        <Menu.Label>{t`Sort by`}</Menu.Label>
                        {sortOptions.map((o) => (
                          <Menu.Item
                            key={o.value}
                            onClick={() => {
                              setSort(o.value)
                              setSortDirOverride('')
                            }}
                            rightSection={sort === o.value ? <IconCheck size={16} /> : undefined}
                          >
                            {o.label}
                          </Menu.Item>
                        ))}
                        <Menu.Item
                          onClick={reverseSort}
                          rightSection={
                            <Text size="xs" c="var(--ink-3)">
                              {sortDir === 'asc' ? t`Ascending` : t`Descending`}
                            </Text>
                          }
                        >
                          {t`Reverse sort order`}
                        </Menu.Item>
                        <Menu.Divider />
                        <Menu.Label>{t`Group by`}</Menu.Label>
                        {groupByOptions.map((o) => (
                          <Menu.Item
                            key={o.value}
                            onClick={() => setGroupBy(o.value)}
                            rightSection={groupBy === o.value ? <IconCheck size={16} /> : undefined}
                          >
                            {o.label}
                          </Menu.Item>
                        ))}
                      </Box>
                      <Menu.Divider />
                      <DensityMenuItems
                        value={density}
                        onChange={(d) => {
                          setDensity(d)
                          writeStored(LS_DENSITY, d)
                        }}
                      />
                    </Menu.Dropdown>
                  </Menu>
                </Group>
              </Group>
            </Stack>
          )}
        </Panel>
      )}

      {showChrome && !selectMode && ((savedFilters ?? []).length > 0 || filtersActive || offlineSeries.size > 0) && (
        <Group
          className="library-saved-filters"
          gap="xs"
          wrap="wrap"
        >
          {offlineSeries.size > 0 && (
            <TagChip active={onDevice} aria-pressed={onDevice} onClick={() => setOnDeviceOnly(!onDeviceOnly)}>
              <Group gap={4} wrap="nowrap" component="span">
                <IconDeviceMobileCheck size={13} />
                <Trans>Saved on this device</Trans>
              </Group>
            </TagChip>
          )}
          {(savedFilters ?? []).length > 0 && (
            <span className="library-saved-label">
              <IconBookmark size={16} />
              <Trans>Saved filters</Trans>
            </span>
          )}
          {(savedFilters ?? []).map((f, index, all) => {
            const { name } = f
            return (
              <Group key={f.id} gap={2}>
                <TagChip active={activeFilterId === f.id} onClick={() => applySpec(f.spec, f.id)}>
                  <Group gap={4} wrap="nowrap" component="span">
                    {f.isDefault && <IconStar size={13} aria-label={t`Opens by default`} />}
                    {f.name}
                  </Group>
                </TagChip>
                <Menu position="bottom-end" withinPortal>
                  <Menu.Target>
                    <OverflowButton size={24} aria-label={t`Options for ${name}`} />
                  </Menu.Target>
                  <Menu.Dropdown>
                    <Menu.Item
                      leftSection={<IconPencil size={16} />}
                      onClick={() => {
                        setRenameValue(f.name)
                        setRenameTarget(f)
                      }}
                    >
                      <Trans>Rename</Trans>
                    </Menu.Item>
                    <Menu.Item
                      leftSection={<IconStar size={16} />}
                      onClick={() => setDefaultSavedFilter.mutate({ id: f.id, isDefault: !f.isDefault })}
                    >
                      {f.isDefault ? <Trans>Stop opening the Library with this</Trans> : <Trans>Open the Library with this</Trans>}
                    </Menu.Item>
                    <Menu.Item
                      leftSection={<IconChevronLeft size={16} />}
                      disabled={index === 0}
                      onClick={() => moveSavedFilter(f, -1)}
                    >
                      <Trans>Move earlier</Trans>
                    </Menu.Item>
                    <Menu.Item
                      leftSection={<IconChevronRight size={16} />}
                      disabled={index === all.length - 1}
                      onClick={() => moveSavedFilter(f, 1)}
                    >
                      <Trans>Move later</Trans>
                    </Menu.Item>
                    <Menu.Divider />
                    <Menu.Item color="var(--danger)" leftSection={<IconTrash size={16} />} onClick={() => removeSavedFilter(f)}>
                      <Trans>Delete</Trans>
                    </Menu.Item>
                  </Menu.Dropdown>
                </Menu>
              </Group>
            )
          })}
          {specActive && (
            <Button
              size="compact-xs"
              variant="subtle"
              leftSection={<IconDeviceFloppy size={16} />}
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
              leftSection={<IconX size={16} />}
              onClick={() => {
                applySpec(DEFAULT_SPEC, null)
                setOnDeviceOnly(false)
              }}
            >
              <Trans>Clear</Trans>
            </Button>
          )}
        </Group>
      )}

      <LibraryFilterPanel
        opened={filtersOpen}
        onClose={() => setFiltersOpen(false)}
        values={filterValues}
        set={filterSetters}
        options={filterOptions}
        hasCommunityRatings={hasCommunityRatings}
        readTracking={readTracking}
        showRootFolder={showRootFolderFilter}
        shownCount={shownCount}
        activeCount={activeFilterCount}
        onReset={() => applySpec(DEFAULT_SPEC, null)}
        onManageTags={() => {
          setFiltersOpen(false)
          setTagManagerOpen(true)
        }}
      />

      <TagManagerModal opened={tagManagerOpen} onClose={() => setTagManagerOpen(false)} />

      <Modal
        opened={saveFilterOpen}
        onClose={() => setSaveFilterOpen(false)}
        title={
          <ModalHeading
            title={t`Save this filter`}
            description={
              <>
                <Trans>Saves the current search, sort and every filter in the panel as a named preset.</Trans>{' '}
                <Trans>Reusing the name of the active preset overwrites it.</Trans>
              </>
            }
          />
        }
      >
        <Stack
          component="form"
          gap="md"
          onSubmit={(e: FormEvent) => {
            e.preventDefault()
            if (!filterName.trim()) return
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
          <TextInput
            label={t`Name`}
            placeholder={t`e.g. Ongoing & behind`}
            value={filterName}
            onChange={(e) => setFilterName(e.currentTarget.value)}
            data-autofocus
          />
          <ModalFooter>
            <Button variant="default" onClick={() => setSaveFilterOpen(false)}>
              <Trans>Cancel</Trans>
            </Button>
            <Button type="submit" disabled={!filterName.trim()} loading={saveFilter.isPending}>
              <Trans>Save</Trans>
            </Button>
          </ModalFooter>
        </Stack>
      </Modal>

      <Modal opened={renameTarget != null} onClose={() => setRenameTarget(null)} title={t`Rename saved filter`}>
        <Stack
          component="form"
          gap="md"
          onSubmit={(e: FormEvent) => {
            e.preventDefault()
            const target = renameTarget
            const name = renameValue.trim()
            if (!target || !name) return
            saveFilter.mutate(
              { id: target.id, name, spec: target.spec },
              {
                onSuccess: () => setRenameTarget(null),
                onError: (err) => {
                  const detail = err instanceof Error ? err.message : String(err)
                  notifications.show({ color: 'var(--danger)', message: now`Failed to rename filter: ${detail}` })
                },
              },
            )
          }}
        >
          <TextInput
            label={t`Name`}
            value={renameValue}
            onChange={(e) => setRenameValue(e.currentTarget.value)}
            data-autofocus
          />
          <ModalFooter>
            <Button variant="default" onClick={() => setRenameTarget(null)}>
              <Trans>Cancel</Trans>
            </Button>
            <Button type="submit" disabled={!renameValue.trim()} loading={saveFilter.isPending}>
              <Trans>Save</Trans>
            </Button>
          </ModalFooter>
        </Stack>
      </Modal>

      <Modal
        opened={tagModalOpen}
        onClose={() => setTagModalOpen(false)}
        title={
          <ModalHeading
            title={plural(selectedCount, { one: 'Tag # series', other: 'Tag # series' })}
            description={
              <>
                <Trans>Adds and removes run in one pass over the selection.</Trans>{' '}
                <Trans>Create new tags from "Manage tags".</Trans>
              </>
            }
          />
        }
      >
        <Stack gap="md">
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
          <ModalFooter>
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
          </ModalFooter>
        </Stack>
      </Modal>

      <Modal
        opened={autoMatchModalOpen}
        onClose={() => setAutoMatchModalOpen(false)}
        title={
          <ModalHeading
            title={plural(selectedCount, { one: 'Auto-match sources for # series', other: 'Auto-match sources for # series' })}
            description={<Trans>Searches every source that isn't linked yet. Linked sources stay as they are.</Trans>}
          />
        }
      >
        <Text size="sm" c="var(--ink-3)" mb="lg">
          <Trans>A large selection can take a while.</Trans>
        </Text>
        <ModalFooter>
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
        </ModalFooter>
      </Modal>

      <Modal
        opened={renameModalOpen}
        onClose={() => setRenameModalOpen(false)}
        title={
          <ModalHeading
            title={plural(selectedCount, { one: 'Rename files of # series', other: 'Rename files of # series' })}
            description={
              <Trans>Renames each series folder and its chapter files to match the naming formats in Settings.</Trans>
            }
          />
        }
      >
        <Text size="sm" c="var(--ink-3)" mb="lg">
          <Trans>
            A series with a download in progress, or with two chapters that would get the same name, is
            skipped.
          </Trans>
        </Text>
        <ModalFooter>
          <Button variant="default" onClick={() => setRenameModalOpen(false)}>
            <Trans>Cancel</Trans>
          </Button>
          <Button
            leftSection={<IconPencil size={16} />}
            onClick={() => {
              setRenameModalOpen(false)
              void runBulk('Rename', { path: '/series/rename/bulk' })
            }}
          >
            <Trans>Rename</Trans>
          </Button>
        </ModalFooter>
      </Modal>

      <Modal
        opened={readStateModalOpen}
        onClose={() => setReadStateModalOpen(false)}
        title={
          <ModalHeading
            title={plural(selectedCount, { one: 'Mark # series read or unread', other: 'Mark # series read or unread' })}
            description={
              <Trans>
                Read marks every chapter that has a file. It isn't logged as reading, so Rewind and your reading
                days stay as they are.
              </Trans>
            }
          />
        }
      >
        <Text size="sm" c="var(--ink-3)" mb="lg">
          <Trans>Unread clears your progress and resume position in every chapter.</Trans>
        </Text>
        <ModalFooter>
          <Button variant="default" onClick={() => setReadStateModalOpen(false)}>
            <Trans>Cancel</Trans>
          </Button>
          <Button variant="light" onClick={() => runReadState('unread')}>
            <Trans>Mark unread</Trans>
          </Button>
          <Button leftSection={<IconCheck size={16} />} onClick={() => runReadState('read')}>
            <Trans>Mark read</Trans>
          </Button>
        </ModalFooter>
      </Modal>

      <Modal
        opened={settingModal != null}
        onClose={() => setSettingModal(null)}
        title={
          settingModal ? (
            <ModalHeading
              title={settingTitles[settingModal]}
              description={
                settingModal === 'Page conversion' ? <Trans>Files already converted stay as they are.</Trans> : undefined
              }
            />
          ) : undefined
        }
      >
        <Radio.Group
          value={settingValue}
          onChange={setSettingValue}
          aria-label={settingModal ? _(BULK_ACTION_LABELS[settingModal]) : undefined}
          mb="lg"
        >
          <Stack gap="sm">
            {settingModal &&
              settingOptions[settingModal].map((o) => (
                <Radio key={o.value} value={o.value} label={o.label} description={o.hint} />
              ))}
          </Stack>
        </Radio.Group>
        <ModalFooter>
          <Button variant="default" onClick={() => setSettingModal(null)}>
            <Trans>Cancel</Trans>
          </Button>
          <Button
            onClick={() => {
              if (!settingModal) return
              const setting = settingModal
              const mode = settingValue
              setSettingModal(null)
              void runBulk(setting, (id) =>
                api(`/series/${id}/${BULK_SETTING_ENDPOINTS[setting]}`, {
                  method: 'POST',
                  body: JSON.stringify({ mode }),
                }),
              )
            }}
          >
            <Trans>Apply</Trans>
          </Button>
        </ModalFooter>
      </Modal>

      <RemoveSeriesDialog
        opened={deleteModalOpen}
        onClose={() => setDeleteModalOpen(false)}
        title={plural(selectedCount, { one: 'Remove # series?', other: 'Remove # series?' })}
        deleteFiles={deleteFiles}
        onDeleteFilesChange={setDeleteFiles}
        onConfirm={() => {
          setDeleteModalOpen(false)
          void runBulk(removeKey, (id) =>
            api(`/series/${id}?deleteFiles=${deleteFiles}`, { method: 'DELETE' }),
          ).then(exitSelectMode)
        }}
      >
        <Trans>The selected series will be removed from Maki and stop being monitored.</Trans>
      </RemoveSeriesDialog>

      <ArchiveSeriesDialog
        opened={archiveModalOpen}
        onClose={() => {
          setArchiveModalOpen(false)
          setArchiveBinRefused([])
        }}
        title={plural(archiveCount, { one: '# series', other: '# series' })}
        removeFiles={archiveRemoveFiles}
        onRemoveFilesChange={setArchiveRemoveFiles}
        canRemoveFiles={canDelete}
        loading={bulkArchive.isPending}
        onConfirm={() => archiveSelected()}
        onConfirmPermanent={archiveBinRefused.length > 0 ? () => archiveSelected(true) : undefined}
      />

      <RestoreSeriesDialog
        opened={restoreAsk != null}
        onClose={() => setRestoreAsk(null)}
        seriesId={restoreAsk?.series?.id ?? null}
        removedCount={restoreAsk?.removed ?? 0}
        canDownload={can('DownloadChapters')}
        loading={restoreSeries.isPending}
        onRestore={(redownload) => {
          const target = restoreAsk?.series
          if (target) restoreOne(target.id, redownload)
          else runRestoreBulk(redownload)
        }}
      />

      <RemoveArchivedFilesDialog
        opened={removeFilesAsk != null}
        onClose={() => setRemoveFilesAsk(null)}
        title={
          removeFilesAsk?.series
            ? t`Remove the files of ${removeFilesTitle}?`
            : plural(selectedCount, { one: 'Remove the files of # series?', other: 'Remove the files of # series?' })
        }
        loading={removeArchivedFiles.isPending}
        onConfirm={() => {
          const target = removeFilesAsk?.series
          if (target) removeFilesOne(target, false)
          else removeFilesSelected()
        }}
        onConfirmPermanent={
          removeFilesAsk?.series && removeFilesAsk.binRefused
            ? () => removeFilesAsk.series && removeFilesOne(removeFilesAsk.series, true)
            : undefined
        }
      />

      <RemoveSeriesDialog
        opened={deleteOne != null}
        onClose={() => setDeleteOne(null)}
        title={<Trans>Remove series?</Trans>}
        deleteFiles={deleteOneFiles}
        onDeleteFilesChange={setDeleteOneFiles}
        loading={deleteSeries.isPending}
        onConfirm={() => deleteOneNow(false)}
        onConfirmPermanent={deleteOne?.binRefused ? () => deleteOneNow(true) : undefined}
      >
        <Trans>This removes "{seriesTitle}" and its chapters from Maki.</Trans>
      </RemoveSeriesDialog>

      <Modal
        opened={monitorModalOpen}
        onClose={() => setMonitorModalOpen(false)}
        title={
          <ModalHeading
            title={plural(selectedCount, { one: 'Set monitoring for # series', other: 'Set monitoring for # series' })}
            description={
              <>
                <Trans>Applies to chapters released later.</Trans>{' '}
                <Trans>Chapters already listed keep whatever you set on them.</Trans>{' '}
                <Trans>
                  "Main" skips specials (decimal chapters like 10.5); "Smart" downloads a few at a time as you
                  read.
                </Trans>
              </>
            }
          />
        }
      >
        <SegmentedControl
          aria-label={t`Monitoring`}
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
        <ModalFooter>
          <Button variant="default" onClick={() => setMonitorModalOpen(false)}>
            <Trans>Cancel</Trans>
          </Button>
          <Button
            onClick={() => {
              setMonitorModalOpen(false)
              void runBulk('Set monitoring', { path: '/series/monitormode/bulk', body: { mode: monitorMode } })
            }}
          >
            <Trans>Apply</Trans>
          </Button>
        </ModalFooter>
      </Modal>

      <Modal
        opened={profileModalOpen}
        onClose={() => setProfileModalOpen(false)}
        title={
          <ModalHeading
            title={plural(selectedCount, { one: 'Set quality profile for # series', other: 'Set quality profile for # series' })}
            description={<Trans>Instance default clears each series' own choice.</Trans>}
          />
        }
      >
        <div className="library-profile-choices">
          <DrillChoices
            label={t`Quality profile`}
            value={bulkProfile}
            options={[
              {
                value: DEFAULT_PROFILE_FILTER,
                label: t`Instance default`,
                hint: t`Follows the default profile set in Settings.`,
              },
              ...(upgradeProfiles ?? []).map((p) => ({
                value: String(p.id),
                label: p.name,
                hint: p.description ?? undefined,
              })),
            ]}
            onPick={setBulkProfile}
          />
        </div>
        <ModalFooter>
          <Button variant="default" onClick={() => setProfileModalOpen(false)}>
            <Trans>Cancel</Trans>
          </Button>
          <Button
            loading={bulkUpgradeProfile.isPending}
            onClick={() =>
              bulkUpgradeProfile.mutate(
                {
                  seriesIds: [...selected],
                  upgradeProfileId: bulkProfile === DEFAULT_PROFILE_FILTER ? null : Number(bulkProfile),
                },
                {
                  onSuccess: ({ updated }) => {
                    setProfileModalOpen(false)
                    notifications.show({
                      color: 'var(--ok)',
                      message: plural(updated, {
                        one: 'Quality profile set for # series',
                        other: 'Quality profile set for # series',
                      }),
                    })
                  },
                  onError: (err) => {
                    const detail = err instanceof Error ? err.message : String(err)
                    notifications.show({
                      color: 'var(--danger)',
                      message: now`Failed to set the quality profile: ${detail}`,
                    })
                  },
                },
              )
            }
          >
            <Trans>Apply</Trans>
          </Button>
        </ModalFooter>
      </Modal>

      <Modal
        opened={readingStatusModalOpen}
        onClose={() => setReadingStatusModalOpen(false)}
        title={
          <ModalHeading
            title={plural(selectedCount, { one: 'Set reading status for # series', other: 'Set reading status for # series' })}
            description={<Trans>Yours alone. Trackers you have connected are updated too.</Trans>}
          />
        }
      >
        <SegmentedControl
          aria-label={t`Reading status`}
          fullWidth
          orientation="vertical"
          value={bulkReadingStatusValue}
          onChange={(v) => setBulkReadingStatusValue(v as ReadingStatus)}
          data={readingStatusOptions}
          mb="lg"
        />
        <ModalFooter>
          <Button variant="default" onClick={() => setReadingStatusModalOpen(false)}>
            <Trans>Cancel</Trans>
          </Button>
          <Button
            loading={bulkReadingStatus.isPending}
            onClick={() =>
              bulkReadingStatus.mutate(
                { seriesIds: [...selected], status: bulkReadingStatusValue },
                {
                  onSuccess: ({ updated }) => {
                    setReadingStatusModalOpen(false)
                    notifications.show({
                      color: 'var(--ok)',
                      message: plural(updated, {
                        one: 'Reading status updated for # series',
                        other: 'Reading status updated for # series',
                      }),
                    })
                  },
                  onError: (err) => {
                    const detail = err instanceof Error ? err.message : String(err)
                    notifications.show({
                      color: 'var(--danger)',
                      message: now`Failed to update reading status: ${detail}`,
                    })
                  },
                },
              )
            }
          >
            <Trans>Apply</Trans>
          </Button>
        </ModalFooter>
      </Modal>

      <Modal
        opened={notifyModalOpen}
        onClose={() => setNotifyModalOpen(false)}
        title={
          <ModalHeading
            title={plural(selectedCount, { one: 'Set notifications for # series', other: 'Set notifications for # series' })}
            description={<Trans>Yours alone. This changes what lands in your bell, not anybody else's.</Trans>}
          />
        }
      >
        <SegmentedControl
          aria-label={t`Notifications`}
          fullWidth
          value={notifyMode}
          onChange={(v) => setNotifyMode(v as SeriesNotificationMode)}
          data={notificationOptions}
          mb="xs"
        />
        <Text size="xs" c="var(--ink-3)" mb="lg">
          {notificationHelp}
        </Text>
        <ModalFooter>
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
        </ModalFooter>
      </Modal>

      <Modal
        opened={moveModalOpen}
        onClose={() => setMoveModalOpen(false)}
        title={
          <ModalHeading
            title={plural(selectedCount, { one: 'Move # series', other: 'Move # series' })}
            description={
              <>
                <Trans>Re-triggers a Kavita scan of both locations either way.</Trans>{' '}
                <Trans>Series already in the destination root folder are skipped.</Trans>{' '}
                <Trans>A file move is blocked for any series with an active download.</Trans>
              </>
            }
          />
        }
      >
        <Stack gap="md">
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
          <ModalFooter>
            <Button variant="default" onClick={() => setMoveModalOpen(false)}>
              <Trans>Cancel</Trans>
            </Button>
            <Button
              disabled={!moveTarget}
              onClick={() => {
                if (!moveTarget) return
                setMoveModalOpen(false)
                void runBulk('Move', {
                  path: '/series/move/bulk',
                  body: { rootFolderId: Number(moveTarget), moveFiles },
                })
              }}
            >
              <Trans>Move</Trans>
            </Button>
          </ModalFooter>
        </Stack>
      </Modal>

      {isLoading && (
        <PosterSkeletons
          density={density}
          viewMode={shownView === 'grid' ? 'grid' : 'list'}
          colsByDensity={LIBRARY_POSTER_COLS_BY_DENSITY}
        />
      )}
      {error && (
        <EmptyState
          title={t`Failed to load library`}
          description={loadErrorMessage}
          actionLabel={isRefetchingSeries ? t`Retrying…` : t`Retry`}
          onAction={() => void refetchSeries()}
        />
      )}
      {archivedScope && series && series.length === 0 && (
        <EmptyState
          art="shelf"
          title={t({ message: 'Nothing archived', context: 'series' })}
          description={t({
            message:
              'Archiving a finished series stops Maki checking its sources and keeps your reading history and rating. Archived series show up here.',
            context: 'series',
          })}
          actionLabel={t`Back to the library`}
          actionTo="/library"
        />
      )}
      {!archivedScope && series && series.length === 0 && (
        <EmptyState
          art="shelf"
          title={t`Your library is empty`}
          description={t`Search MangaBaka and add your first series. Maki will monitor for new chapters and download them automatically.`}
          actionLabel={can('AddSeries') ? t`Add a series` : t`Request series`}
          actionTo="/add"
          actionIcon={<IconPlus size={16} />}
          secondaryActionLabel={can('ImportLibrary') ? t`Import a folder` : undefined}
          secondaryActionTo="/import"
          secondaryActionIcon={<IconFolderDown size={16} />}
        />
      )}
      {series && series.length > 0 && visible.length === 0 && (
        <EmptyState
          title={t`No matches`}
          description={t`No series match your filters. Try clearing the search or a filter.`}
          actionLabel={t`Clear all filters`}
          onAction={() => {
            applySpec(DEFAULT_SPEC, null)
            setOnDeviceOnly(false)
          }}
        />
      )}
      {/* Every view renders a slice, not the whole filtered set, once the library is big enough to
          be worth it (see useWindowedRows for the threshold and what it costs), each group through
          its own window. Bulk selection is unaffected: "select filtered" works off `visible`,
          never off what is mounted. */}
      {visible.length > 0 && shownView === 'table' && (
        <LibraryTableFrame columns={tableColumns} selectMode={selectMode}>
          <LibraryTableHeader
            columns={tableColumns}
            selectMode={selectMode}
            sort={sort}
            sortDir={sortDir}
            sorts={scopeSorts}
            onSort={sortByColumn}
          />
          {itemsBody}
        </LibraryTableFrame>
      )}
      {visible.length > 0 && shownView !== 'table' && itemsBody}
      <LibraryColumnsModal
        opened={columnsOpen}
        onClose={() => setColumnsOpen(false)}
        columns={tableColumns}
        onChange={changeColumns}
      />
    </SurfaceFrame>
  )
}

/**
 * Which table columns show and in what order. Shown columns come first in their order with arrows
 * to move them; hidden ones follow in the default order.
 */
function LibraryColumnsModal({
  opened,
  onClose,
  columns,
  onChange,
}: {
  opened: boolean
  onClose: () => void
  columns: LibraryColumn[]
  onChange: (columns: LibraryColumn[]) => void
}) {
  const { _ } = useLingui()
  const { t } = useLinguiMacro()
  const hidden = LIBRARY_COLUMNS.filter((c) => !columns.includes(c))
  const move = (column: LibraryColumn, by: -1 | 1) => {
    const from = columns.indexOf(column)
    const to = from + by
    if (from === -1 || to < 0 || to >= columns.length) return
    const next = [...columns]
    ;[next[from], next[to]] = [next[to], next[from]]
    onChange(next)
  }
  return (
    <Modal
      opened={opened}
      onClose={onClose}
      title={
        <ModalHeading
          title={t`Columns`}
          description={<Trans>The cover and title always show. Your choice is kept in this browser.</Trans>}
        />
      }
      closeButtonProps={{ 'aria-label': t`Close` }}
    >
      <Stack gap="xs">
        {[...columns, ...hidden].map((column) => {
          const shown = columns.includes(column)
          const index = columns.indexOf(column)
          const label = _(LIBRARY_COLUMN_LABELS[column])
          return (
            <Group key={column} justify="space-between" wrap="nowrap" className="library-columns-row">
              <Checkbox
                label={label}
                checked={shown}
                onChange={(e) =>
                  onChange(e.currentTarget.checked ? [...columns, column] : columns.filter((c) => c !== column))
                }
              />
              {shown && (
                <Group gap={4} wrap="nowrap">
                  <ActionIcon
                    variant="subtle"
                    aria-label={t`Move ${label} up`}
                    disabled={index === 0}
                    onClick={() => move(column, -1)}
                  >
                    <IconArrowUp size={16} />
                  </ActionIcon>
                  <ActionIcon
                    variant="subtle"
                    aria-label={t`Move ${label} down`}
                    disabled={index === columns.length - 1}
                    onClick={() => move(column, 1)}
                  >
                    <IconArrowDown size={16} />
                  </ActionIcon>
                </Group>
              )}
            </Group>
          )
        })}
        <ModalFooter>
          <Button variant="subtle" onClick={() => onChange([...DEFAULT_LIBRARY_COLUMNS])}>
            <Trans>Reset to default</Trans>
          </Button>
        </ModalFooter>
      </Stack>
    </Modal>
  )
}
