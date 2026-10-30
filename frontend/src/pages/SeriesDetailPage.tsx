import { errorText } from '../api/errorText'
import { Fragment, useCallback, useDeferredValue, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import type { CSSProperties, FormEvent, ReactNode, RefObject } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { msg, plural, t as staticT } from '@lingui/core/macro'
import { Trans, Plural, useLingui } from '@lingui/react/macro'
import { useLingui as useLinguiReact } from '@lingui/react'
import type { MessageDescriptor } from '@lingui/core'
import {
  ActionIcon,
  Alert,
  Anchor,
  Badge,
  Box,
  Button,
  Divider,
  Group,
  Loader,
  Menu,
  NumberInput,
  Modal,
  Pagination,
  Paper,
  Radio,
  SegmentedControl,
  Select,
  Stack,
  Table,
  Tabs,
  Text,
  TextInput,
  Title,
  Tooltip,
  VisuallyHidden,
} from '@mantine/core'
import {
  IconAlertTriangle,
  IconArrowLeft,
  IconBook,
  IconChevronDown,
  IconCircleCheck,
  IconDownload,
  IconEye,
  IconEyeCheck,
  IconLink,
  IconLinkOff,
  IconListCheck,
  IconSearch,
  IconSend,
  IconRowRemove,
  IconTrash,
  IconWand,
  IconX,
  IconDeviceTv,
  IconEyeOff,
  IconSortAscendingNumbers,
  IconSortDescendingNumbers,
} from '@tabler/icons-react'
import { useMediaQuery } from '@mantine/hooks'
import { notifications } from '../lib/toast'
import { Link, useLocation, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import {
  useChapters,
  useSourceMappings,
  useSources,
  useArchiveSeries,
  useDismissArchiveHint,
  useDismissRateHint,
  useDeleteSeries,
  useRemoveArchivedFiles,
  useRestoreSeries,
  useMoveSeries,
  useRefreshMetadata,
  useRefreshSeries,
  useRescanSeries,
  useRootFolders,
  useSearchChapter,
  useDownloadChapters,
  useDownloadNext,
  useSearchMissing,
  useSeries,
  useSeriesDetail,
  useSetChaptersWanted,
  useSetIncognito,
  useSetSeriesNotificationMode,
  useSetMonitorMode,
  useSetUpgradeProfile,
  useSetReadFileCleanup,
  useSetSeriesPageConversion,
  useSetRating,
  useToggleChapterWanted,
  useUnlinkChapters,
  useDeleteChapterFiles,
  useDeleteChapters,
  useSeriesQueue, useSeriesFilesSummary,
  useRecommendationDetail,
} from '../api/hooks'
import {
  useContinueReading,
  useReadTracking,
  useSeriesReadProgress,
  useSetChapterRead,
  useSetChaptersState,
  type ChapterReadState,
} from '../api/reader'
import { useApplyAnimeResume, useDismissAnimeResume, useSeriesAnimeResume } from '../api/animeResume'
import { useCreateSeriesRequest } from '../api/requests'
import { altTitleLabel, readableTitles } from '../api/titles'
import type { ChapterDto } from '../api/types'
import {
  isVolumeSearchStarted,
  volumeSearchResultText,
  useRunUpgradeScan,
  useSetFileTrusted,
  useTorrentProposals,
  useUpgradeChapterNow,
  scanReasonSummary,
  useSeriesUpgradeScanStatus,
  useUpgradeProfiles,
  useVolumeSearch,
} from '../api/upgrades'
import type { UpgradeScanResultDto } from '../api/upgrades'
import { ApiError, isOfflineUnavailable } from '../api/client'
import { useAuth } from '../auth/AuthProvider'
import { useLabel } from '../i18n-context'
import { useDocumentTitle } from '../lib/documentTitle'
import { usePageLabel } from '../lib/navHistory'
import { AnimeCoverageBar } from '../components/AnimeCoverageBar'
import { LinkChaptersModal } from '../components/LinkChaptersModal'
import { RelinkFilesModal } from '../components/RelinkFilesModal'
import { MetadataLinks } from '../components/MetadataLinks'
import { RelatedSeriesSection } from '../components/RelatedSeriesSection'
import { TagBuckets } from '../components/TagBuckets'
import { SimilarSeriesSection } from '../components/SimilarSeriesSection'
import { ReleaseSearchModal } from '../components/ReleaseSearchModal'
import { TorrentProposalCard } from '../components/upgrades/TorrentProposalCard'
import { RenameSeriesModal } from '../components/RenameSeriesModal'
import { RequestForm } from '../components/RequestForm'
import { AnimeResumeCallout } from '../components/series/AnimeResumeCallout'
import {
  ChapterRow,
  ChapterRowSkeletons,
  ChapterSpanRow,
  ChunkedRows,
  type ChapterRowActions,
  type ChapterSpanRowActions,
} from '../components/series/ChapterTableRows'
import {
  chapterLabel,
  finishedHighWater,
  isSkipped,
  readLanguagesOf,
  readStateOf,
  type FileOrigin,
} from '../components/series/chapterTable'
import { ArchivedBanner } from '../components/series/ArchivedBanner'
import { ArchiveHint } from '../components/series/ArchiveHint'
import { RateHint } from '../components/series/RateHint'
import { RemoveArchivedFilesDialog } from '../components/series/RemoveArchivedFilesDialog'
import { ArchiveSeriesDialog } from '../components/series/ArchiveSeriesDialog'
import { RemoveSeriesDialog } from '../components/series/RemoveSeriesDialog'
import { UndoConversionModal } from '../components/series/UndoConversionModal'
import { useSeriesConversion } from '../api/pageConversion'
import { RestoreSeriesDialog } from '../components/series/RestoreSeriesDialog'
import { SeriesActionsMenu } from '../components/series/SeriesActionsMenu'
import { EditMetadataModal, MetadataLock } from '../components/series/EditMetadataModal'
import { MetadataHistoryModal } from '../components/series/MetadataHistoryModal'
import { UpgradeNowResultModal } from '../components/series/UpgradeNowResultModal'
import { SeriesHero, SeriesHeroSkeleton } from '../components/series/SeriesHero'
import { SeriesReviews } from '../components/series/SeriesReviews'
import { SeriesFilesSection } from '../components/SeriesFilesSection'
import { SeriesTagsEditor } from '../components/SeriesTagsEditor'
import { SeriesScrobbleSection } from '../components/SeriesScrobbleSection'
import { SourceMappingsSection } from '../components/SourceMappingsSection'
import { ReleaseOutlookSummary } from '../components/releases/ReleaseOutlookSummary'
import { SourceCompareModal } from '../components/SourceCompareModal'
import type { PickChapter } from '../components/SourceCompareModal'
import { formatDate, formatReadingTime } from '../format'
import {
  contentRatingVisual,
  seriesProgressVisual,
  seriesStatusVisual,
} from '../components/ui/status'
import { readStored, writeStored } from '../components/ui/viewPrefs'
import { MonoValue } from '../components/ui/MonoValue'
import { useColumnWidths, type ColumnSpec } from '../lib/useColumnWidths'
import { useStableActions } from '../lib/useStableActions'
import { SurfaceFrame } from '../components/ui/SurfaceFrame'
import { EmptyState } from '../components/ui/EmptyState'
import { Panel } from '../components/ui/Panel'
import { LuckyButton } from '../components/LuckyButton'
import { isUnfinished } from '../lib/lucky'
import { activeSeries } from '../lib/series'
import { showRestoreResult } from '../components/series/restoreToast'
import { buildAnimeSpans, mergeAnimeMarkers, type AnimeSpan } from '../lib/animeCoverage'
import { cleanSynopsis } from '../lib/synopsis'
import { useIncognitoOptions } from '../components/ui/incognito'
import { useSeriesNotificationOptions } from '../components/ui/seriesNotifications'
import { scrollBehavior } from '../lib/scrollBehavior'
import { offersPermanentDelete, useRecycleBinDays } from '../api/recycleBin'
import { PermanentDeleteFallback } from '../components/PermanentDeleteFallback'
import { pwaSupported } from '../lib/pwa/serviceWorker'
import {
  removeChaptersOffline,
  removeSeriesOffline,
  saveChaptersOffline,
  saveUnreadOffline,
  useOfflineChapters,
} from '../pwa/offlineChapters'

/** A special is a decimal-numbered chapter (10.5 omake etc.). */
const isSpecial = (c: ChapterDto) => c.number !== null && c.number % 1 !== 0

const TABS = ['details', 'chapters', 'files'] as const
type Tab = (typeof TABS)[number]

const CHAPTER_PAGE_SIZE_STORAGE_KEY = 'series-chapter-page-size'
const CHAPTER_PAGE_SIZES = ['10', '25', '50', '75', '100', 'all'] as const
type ChapterPageSize = (typeof CHAPTER_PAGE_SIZES)[number]

const CHAPTER_FILTER_STORAGE_KEY = 'series-chapter-filter'
const CHAPTER_FILTERS = [
  'all',
  'wanted',
  'missing',
  'downloaded',
  'unread',
  'main',
  'specials',
  'cutoffUnmet',
  'skipped',
] as const
type ChapterFilter = (typeof CHAPTER_FILTERS)[number]

const CHAPTER_SORT_STORAGE_KEY = 'series-chapter-sort'
const CHAPTER_SORTS = ['asc', 'desc'] as const
type ChapterSort = (typeof CHAPTER_SORTS)[number]

const CHAPTER_COLUMNS_STORAGE_KEY = 'series-chapter-columns'
const CHAPTER_COLUMNS = {
  chapter: { width: 200, min: 110 },
  released: { width: 120, min: 90 },
  source: { width: 190, min: 100 },
  status: { width: 240, min: 140 },
}
// The Wanted and actions columns hold fixed-size controls, and Title takes whatever is left, down
// to this much before the rows turn into two-line list rows instead.
const CHAPTER_WANTED_COLUMN_WIDTH = 80
const CHAPTER_ACTIONS_COLUMN_WIDTH = 124
const CHAPTER_FIXED_COLUMNS_WIDTH = CHAPTER_WANTED_COLUMN_WIDTH + CHAPTER_ACTIONS_COLUMN_WIDTH
const CHAPTER_TITLE_MIN_WIDTH = 220
type ChapterColumn = keyof typeof CHAPTER_COLUMNS
const CHAPTER_COLUMN_KEYS = Object.keys(CHAPTER_COLUMNS) as ChapterColumn[]
const CHAPTER_COLUMN_MAX = 640
/**
 * Full shows every column, compact sheds Released, list turns rows into two-line list rows. Released
 * goes first because it is the one column nothing on the page acts on: Source and Status are what
 * downloads and upgrades are decided from, and the actions need the room.
 */
type ChapterLayout = 'full' | 'compact' | 'list'
const CHAPTER_SHED_COLUMN: ChapterColumn = 'released'
const shownChapterColumns = (layout: ChapterLayout) =>
  layout === 'compact' ? CHAPTER_COLUMN_KEYS.filter((key) => key !== CHAPTER_SHED_COLUMN) : CHAPTER_COLUMN_KEYS
const chapterColumnsWidthFor = (widths: Record<ChapterColumn, number>, layout: ChapterLayout) =>
  CHAPTER_FIXED_COLUMNS_WIDTH + CHAPTER_TITLE_MIN_WIDTH + shownChapterColumns(layout).reduce((sum, key) => sum + widths[key], 0)
// Off the default widths, never the dragged ones: a column dragged wide used to tip the table into
// list rows, which have no header to drag it back from.
const CHAPTER_DEFAULT_WIDTHS = Object.fromEntries(
    CHAPTER_COLUMN_KEYS.map((key) => [key, CHAPTER_COLUMNS[key].width]),
) as Record<ChapterColumn, number>
const CHAPTER_FULL_MIN_WIDTH = chapterColumnsWidthFor(CHAPTER_DEFAULT_WIDTHS, 'full')
const CHAPTER_COMPACT_MIN_WIDTH = chapterColumnsWidthFor(CHAPTER_DEFAULT_WIDTHS, 'compact')
// The layout was picked because the panel holds at least this much, so a table never needs to
// scroll sideways in it. Widths stored from a wider window stay as preferences the browser can
// squeeze rather than a floor that pushes the table past the panel.
const chapterTableMinWidthFor = (widths: Record<ChapterColumn, number>, layout: ChapterLayout) =>
  Math.min(
      chapterColumnsWidthFor(widths, layout),
      layout === 'full' ? CHAPTER_FULL_MIN_WIDTH : CHAPTER_COMPACT_MIN_WIDTH,
  )
const chapterLayoutFor = (panelWidth: number): ChapterLayout =>
  panelWidth === 0 || panelWidth >= CHAPTER_FULL_MIN_WIDTH ? 'full' : panelWidth >= CHAPTER_COMPACT_MIN_WIDTH ? 'compact' : 'list'

type ChapterColumnRoom = { panel: number; layout: ChapterLayout; widths: Record<ChapterColumn, number> | null }
/**
 * Column specs whose max is whatever the panel has left once every other shown column has its
 * width, so a drag can't push the table wider than the panel. Read at drag time from `room`, which
 * the page keeps current without re-rendering on every pixel of a window resize.
 */
const chapterColumnSpecsWithin = (room: RefObject<ChapterColumnRoom>) =>
  Object.fromEntries(
      CHAPTER_COLUMN_KEYS.map((key): [ChapterColumn, ColumnSpec] => [key, {
        ...CHAPTER_COLUMNS[key],
        get max() {
          const { panel, layout, widths } = room.current
          if (panel === 0 || layout === 'list' || !widths) return CHAPTER_COLUMN_MAX
          const others = shownChapterColumns(layout).reduce((sum, k) => (k === key ? sum : sum + widths[k]), 0)
          const left = Math.floor(panel - CHAPTER_FIXED_COLUMNS_WIDTH - CHAPTER_TITLE_MIN_WIDTH - others)
          return Math.min(CHAPTER_COLUMN_MAX, Math.max(CHAPTER_COLUMNS[key].min, left))
        },
      }]),
  ) as Record<ChapterColumn, ColumnSpec>

/**
 * Descriptors, not strings: this table is built once when the module loads, so a rendered string
 * here would be stuck in whichever language was active at that moment. Render through the hook
 * below, which is what the page-size Select's `data` prop actually uses.
 */
const CHAPTER_PAGE_SIZE_LABELS: Record<ChapterPageSize, MessageDescriptor> = {
  '10': msg`10 / page`,
  '25': msg`25 / page`,
  '50': msg`50 / page`,
  '75': msg`75 / page`,
  '100': msg`100 / page`,
  all: msg`All`,
}

function useChapterPageSizeOptions() {
  const { _, i18n } = useLinguiReact()
  return useMemo(
      () => CHAPTER_PAGE_SIZES.map((value) => ({ value, label: _(CHAPTER_PAGE_SIZE_LABELS[value]) })),
      [_, i18n.locale],
  )
}

const LARGE_WANTED_DOWNLOAD_THRESHOLD = 50

type RenderedRow =
    | { kind: 'chapter'; chapter: ChapterDto }
    | { kind: 'span'; span: AnimeSpan; rows: ChapterDto[] }

const NO_ROWS: RenderedRow[] = []

type ReadTimeEstimate = {
  seconds: number
  remainingChapters: number
  style: 'scrolling' | 'paged'
  sampleChapters: number
  seriesSpecific: boolean
}

/**
 * Mirrors the monitor-mode Select's own labels, for the toast after a change.
 *
 * Descriptors, not strings: this table is built once when the module loads, so a rendered string
 * here would be stuck in whichever language was active at that moment. Render with `useLabel()`.
 */
const MONITOR_MODE_LABELS: Record<string, MessageDescriptor> = {
  All: msg`all chapters`,
  MainOnly: msg`main (no specials)`,
  Smart: msg`smart`,
  None: msg`none`,
}

const chapterFilters: Record<string, (c: ChapterDto) => boolean> = {
  all: () => true,
  wanted: (c) => c.wanted,
  missing: (c) => !c.hasFile && !c.fileRemovedAt,
  downloaded: (c) => c.hasFile,
  specials: isSpecial,
  // A one-shot has no number and counts as main, matching NewChapterMonitorMode.MainOnly.
  main: (c) => !isSpecial(c),
  // Matches `/upgrades/cutoff-unmet`, which excludes trusted files even when their cutoff isn't met.
  cutoffUnmet: (c) => c.fileQuality?.cutoffMet === false && !c.fileQuality?.trusted,
}

// Errors are reported globally (see main.tsx); only success needs saying here. `info` is for
// outcomes that aren't failures but aren't wins either — a download action that found nothing
// left to queue, which would otherwise report a cheerful "Queued 0".
// Hover alone leaves keyboard users without the reason a control is switched off.
const TOOLTIP_EVENTS = { hover: true, focus: true, touch: true }

const notify = {
  ok: (message: string) => notifications.show({ message, color: 'var(--ok)' }),
  info: (message: string) => notifications.show({ message, color: 'var(--warn)' }),
  err: (message: string) => notifications.show({ message, color: 'var(--danger)' }),
}

// The route is not keyed by id, so everything below (scan watcher, selection, filters, the sources
// card's seen-scout marker) would otherwise carry over when navigating from one series to another.
export default function SeriesDetailPage() {
  const { id } = useParams()
  return <SeriesDetailBody key={id} />
}

function SeriesDetailBody() {
  const renderLabel = useLabel()
  const incognitoOptions = useIncognitoOptions()
  const notificationOptions = useSeriesNotificationOptions()
  const { t, i18n } = useLingui()
  const chapterPageSizeOptions = useChapterPageSizeOptions()
  const { id } = useParams()
  const seriesId = Number(id)
  const navigate = useNavigate()

  /**
   * Which tab is open lives in the URL, so a refresh, a bookmark and a link someone pastes into
   * chat all land on the same tab. `replace` rather than a push: tab switches are not places you
   * want the back button to walk through one at a time on the way out of the series.
   */
  const [searchParams, setSearchParams] = useSearchParams()
  // Anything unrecognised falls back rather than rendering no panel at all, which is what a
  // bookmark of the old ?tab=sources would otherwise do now that sources lives under Progress.
  const requestedTab = searchParams.get('tab')
  const urlTab = TABS.includes(requestedTab as Tab) ? (requestedTab as Tab) : 'details'
  // Held in state as well, because the router applies every URL change inside a transition: a tab
  // read off the URL alone kept the old tab on screen, unresponsive, until the new one had rendered
  // in full. A URL change from elsewhere (a link to ?tab=files) still wins.
  const [tab, setTab] = useState<Tab>(urlTab)
  const [syncedUrlTab, setSyncedUrlTab] = useState<Tab>(urlTab)
  if (urlTab !== syncedUrlTab) {
    setSyncedUrlTab(urlTab)
    setTab(urlTab)
  }
  const changeTab = (value: string | null) => {
    if (!value || !TABS.includes(value as Tab)) return
    setTab(value as Tab)
    const next = new URLSearchParams(searchParams)
    if (value === 'details') next.delete('tab')
    else next.set('tab', value)
    setSearchParams(next, { replace: true })
  }
  const isMobile = useMediaQuery('(max-width: 47.99em)')
  const { data: series, isLoading, error: seriesError } = useSeriesDetail(seriesId)
  // So that a series reached from another one (Similar, Related) offers a back link that names it
  // rather than the generic "Series".
  usePageLabel(series?.title)
  useDocumentTitle(series?.displayTitle)
  const { data: chapters, isError: chaptersFailed } = useChapters(seriesId)

  /**
   * Series.Tags is a flat list of names: the weight, description and spoiler flag live only on
   * the MangaBaka row, so the graded chips need the provider detail. Same query key and 30min
   * staleTime as Discover's card, so opening one after the other is a cache hit either way.
   * Falls back to the flat names when the series has no MangaBaka id or the local dump is absent.
   */
  const { data: providerDetail } = useRecommendationDetail(
      series?.mangaBakaId != null ? String(series.mangaBakaId) : null,
  )
  const providerTags = providerDetail?.tags ?? []

  // Registered sources are cached with staleTime Infinity, so this is a lookup, not a fetch per row.
  const { data: sources } = useSources()
  /**
   * ChapterFile.SourceName is a scrape source's name, the sentinel "import" for a file the user
   * brought in from disk, or "torrent:{indexer}". Only a scraped file can be re-fetched from a
   * different source, so the badge says which kind it is.
   */
  const fileOrigin = useCallback((name: string, releaseName: string | null): FileOrigin => {
    const source = sources?.find((x) => x.name === name)
    if (source) {
      return { label: source.displayName, scraped: true, hint: '' }
    }
    if (name === 'import') {
      return { label: t`Imported`, scraped: false, hint: t`Brought in from disk, not downloaded by Maki` }
    }
    if (name.startsWith('torrent:')) {
      const indexer = name.slice('torrent:'.length)
      const hint = releaseName
          ? t`Grabbed from ${indexer}: ${releaseName}`
          : t`Grabbed from ${indexer}`
      return { label: t`Torrent`, scraped: false, hint }
    }
    return { label: name, scraped: false, hint: '' }
  }, [sources, t])
  const queryClient = useQueryClient()

  // `sourceMatchFinished` normally refreshes these, but this page also polls the series row while
  // matching runs, so it can notice the flag clearing on a connection that missed the push. The
  // mappings and the chapter list arrive with it, and neither has a flag of its own to poll on.
  const wasMatching = useRef(false)
  useEffect(() => {
    const matching = series?.sourceMatchPending ?? false
    if (wasMatching.current && !matching) {
      void queryClient.invalidateQueries({ queryKey: ['sourcemappings', seriesId] })
      void queryClient.invalidateQueries({ queryKey: ['chapters', seriesId] })
    }
    wasMatching.current = matching
  }, [series?.sourceMatchPending, seriesId, queryClient])
  // An imported series links its files a little after the match, in a stage of its own.
  const wasLinking = useRef(false)
  useEffect(() => {
    const linking = series?.importLinkPending ?? false
    if (wasLinking.current && !linking) {
      void queryClient.invalidateQueries({ queryKey: ['chapters', seriesId] })
      void queryClient.invalidateQueries({ queryKey: ['series-files', seriesId] })
    }
    wasLinking.current = linking
  }, [series?.importLinkPending, seriesId, queryClient])
  const readTracking = useReadTracking()
  const { data: progressRows } = useSeriesReadProgress(seriesId)
  const { data: continueAt } = useContinueReading(seriesId)
  const { data: filesSummary } = useSeriesFilesSummary(seriesId)
  const setRead = useSetChapterRead(seriesId)
  // Only worth asking once there is an anime to have finished, and only meaningful with read
  // tracking on: the callout's own "Read ch. N" action needs somewhere to record progress.
  const { data: animeResume } = useSeriesAnimeResume(
      seriesId,
      readTracking && Boolean(series?.animeStart || series?.animeEnd),
  )
  const applyAnimeResume = useApplyAnimeResume(seriesId)
  const dismissAnimeResumeMutation = useDismissAnimeResume(seriesId)
  const readProgress = useMemo(
      () => new Map((progressRows ?? []).map((p) => [p.chapterId, p])),
      [progressRows],
  )
  const readStateFor = useCallback(
      (c: ChapterDto) => readStateOf(readProgress.get(c.id)),
      [readProgress],
  )
  const canSaveOffline = pwaSupported()
  const { records: offlineRecords, progress: offlineProgress } = useOfflineChapters()
  const offlineSaved = useMemo(
      () => [...offlineRecords.values()].filter((r) => r.seriesId === seriesId).length,
      [offlineRecords, seriesId],
  )
  const { data: queue } = useSeriesQueue(seriesId)
  const queueByChapterId = useMemo(
      () => new Map((queue?.items ?? []).filter((q) => q.seriesId === seriesId).map((q) => [q.chapterId, q])),
      [queue, seriesId],
  )
  const deleteSeries = useDeleteSeries()
  const archiveSeries = useArchiveSeries()
  const dismissArchiveHint = useDismissArchiveHint()
  const dismissRateHint = useDismissRateHint()
  const restoreSeries = useRestoreSeries()
  const removeArchivedFiles = useRemoveArchivedFiles()
  const refresh = useRefreshSeries()
  const refreshMetadata = useRefreshMetadata()
  const rescan = useRescanSeries()
  const moveSeries = useMoveSeries()
  const { data: rootFolders } = useRootFolders()
  const [moveModalOpen, setMoveModalOpen] = useState(false)
  const [renameModalOpen, setRenameModalOpen] = useState(false)
  const [editMetadataOpen, setEditMetadataOpen] = useState(false)
  const [metadataHistoryOpen, setMetadataHistoryOpen] = useState(false)
  const [moveTarget, setMoveTarget] = useState<string | null>(null)
  const [moveFiles, setMoveFiles] = useState(true)
  const search = useSearchChapter()
  const toggleWanted = useToggleChapterWanted()
  const searchMissing = useSearchMissing()
  const downloadChapters = useDownloadChapters()
  const downloadNext = useDownloadNext()
  const [downloadAllConfirmOpen, setDownloadAllConfirmOpen] = useState(false)
  const [nextCountOpen, setNextCountOpen] = useState(false)
  const [nextCount, setNextCount] = useState<number | string>(10)
  const setMonitorMode = useSetMonitorMode()
  const setUpgradeProfile = useSetUpgradeProfile()
  const setReadFileCleanup = useSetReadFileCleanup()
  const setSeriesPageConversion = useSetSeriesPageConversion()
  const { data: upgradeProfiles } = useUpgradeProfiles()
  const setFileTrusted = useSetFileTrusted()
  const upgradeChapterNow = useUpgradeChapterNow()
  const runUpgradeScan = useRunUpgradeScan()
  const { data: scanStatus } = useSeriesUpgradeScanStatus(seriesId)
  const scanning = scanStatus?.state === 'queued' || scanStatus?.state === 'running'
  const scanWasRunning = useRef(false)

  // The scan runs as a background job, so its end is only seen by polling its status: say how it
  // went and pull the new "last upgrade scan" line and any queued upgrades onto the page.
  useEffect(() => {
    if (!scanStatus) return
    if (scanStatus.state === 'queued' || scanStatus.state === 'running') {
      scanWasRunning.current = true
      return
    }
    if (!scanWasRunning.current) return
    scanWasRunning.current = false
    void queryClient.invalidateQueries({ queryKey: ['series'] })
    void queryClient.invalidateQueries({ queryKey: ['chapters', seriesId] })
    void queryClient.invalidateQueries({ queryKey: ['upgrades'] })
    const { queued, chaptersChecked } = scanStatus
    if (scanStatus.state === 'done') {
      const reasons = scanReasonSummary(renderLabel, scanStatus.skipped)
      notifications.show({
        color: queued > 0 ? 'var(--ok)' : 'var(--info)',
        title:
          chaptersChecked === 0
            ? staticT`Upgrade scan finished with nothing to check`
            : queued > 0
              ? plural(queued, {
                  one: 'Upgrade scan finished: # better copy queued',
                  other: 'Upgrade scan finished: # better copies queued',
                })
              : plural(chaptersChecked, {
                  one: 'Upgrade scan finished: no better copy for the # chapter checked',
                  other: 'Upgrade scan finished: no better copies for the # chapters checked',
                }),
        message:
          chaptersChecked === 0
            ? staticT`It needs a quality profile and downloaded chapters.`
            : reasons
              ? staticT`Passed over: ${reasons}`
              : undefined,
        autoClose: reasons ? 10_000 : undefined,
      })
    } else if (scanStatus.state === 'busy') {
      notifications.show({
        color: 'var(--warn)',
        message: staticT`Another upgrade scan was already running, so this one did not run. Try again once it finishes.`,
      })
    } else {
      notifications.show({
        color: 'var(--danger)',
        message: staticT`The upgrade scan could not run. The server log has the details.`,
      })
    }
  }, [scanStatus, queryClient, seriesId, renderLabel])
  const runVolumeSearch = useVolumeSearch()
  const [upgradeNowResult, setUpgradeNowResult] = useState<UpgradeScanResultDto | null>(null)
  const [upgradeNowModalOpen, setUpgradeNowModalOpen] = useState(false)
  const setIncognito = useSetIncognito()
  const setNotificationMode = useSetSeriesNotificationMode()
  const setRating = useSetRating()
  const unlinkChapters = useUnlinkChapters()
  const setChaptersWanted = useSetChaptersWanted()
  const deleteChapters = useDeleteChapters()
  const deleteChapterFiles = useDeleteChapterFiles()
  const [releaseModalOpen, setReleaseModalOpen] = useState(false)
  // A series with no upgrade profile (own or default) never gets a cutoffMet verdict at all, so the
  // "Cutoff unmet" chip disappears rather than sitting at "(0)" forever. The effective filter below
  // falls back to All when the chip is hidden, so a remembered or active cutoffUnmet never leaves
  // the table stuck on a filter with no chip showing it.
  const cutoffUnmetChipVisible =
      (chapters?.filter(chapterFilters.cutoffUnmet).length ?? 0) > 0 ||
      (chapters?.some((c) => c.fileQuality?.cutoffMet != null) ?? false)
  // Remembered across series, so someone who never wants specials picks Main once rather than on
  // every series they open.
  const [chapterFilterPreference, setChapterFilterPreference] = useState<ChapterFilter>(() =>
      readStored(CHAPTER_FILTER_STORAGE_KEY, CHAPTER_FILTERS, 'all'),
  )
  const setChapterFilter = (value: string) => {
    if (!CHAPTER_FILTERS.includes(value as ChapterFilter)) return
    setChapterFilterPreference(value as ChapterFilter)
    writeStored(CHAPTER_FILTER_STORAGE_KEY, value)
  }
  const [chapterSort, setChapterSortState] = useState<ChapterSort>(() =>
      readStored(CHAPTER_SORT_STORAGE_KEY, CHAPTER_SORTS, 'asc'),
  )
  const toggleChapterSort = () => {
    const next = chapterSort === 'asc' ? 'desc' : 'asc'
    setChapterSortState(next)
    writeStored(CHAPTER_SORT_STORAGE_KEY, next)
  }
  const chapterColumnRoom = useRef<ChapterColumnRoom>({ panel: 0, layout: 'full', widths: null })
  const [chapterColumnSpecs] = useState(() => chapterColumnSpecsWithin(chapterColumnRoom))
  const { widths: chapterColumnWidths, handleProps: chapterColumnHandle } = useColumnWidths(
      CHAPTER_COLUMNS_STORAGE_KEY,
      chapterColumnSpecs,
      (widths, key, shift) => {
        // Mid-drag nothing renders, so the table's min-width and the season lines are moved by
        // hand. The lines sit at the Chapter cell's end, so they shift by exactly what it grew.
        chapterTable
            ?.querySelector<HTMLElement>('.mantine-TableScrollContainer-scrollContainer')
            ?.style.setProperty('--table-min-width', `${chapterTableMinWidthFor(widths, chapterColumnRoom.current.layout)}px`)
        const overlay = chapterTable?.querySelector<HTMLElement>('.chapter-span-overlay')
        if (overlay) overlay.style.transform = key === 'chapter' && shift ? `translateX(${shift}px)` : ''
      },
  )
  const [chapterSearch, setChapterSearch] = useState('')
  const [chapterPageSizePreference, setChapterPageSizePreference] = useState<ChapterPageSize>(() =>
      readStored(CHAPTER_PAGE_SIZE_STORAGE_KEY, CHAPTER_PAGE_SIZES, '50'),
  )
  const setChapterPageSize = (value: string | null) => {
    if (!value || !CHAPTER_PAGE_SIZES.includes(value as ChapterPageSize)) return
    const next = value as ChapterPageSize
    setChapterPageSizePreference(next)
    writeStored(CHAPTER_PAGE_SIZE_STORAGE_KEY, next)
  }
  const [chapterPage, setChapterPage] = useState(1)
  const [selectMode, setSelectMode] = useState(false)
  const [selected, setSelected] = useState<Set<number>>(new Set())
  // Chapter ids the link dialog is working on; null keeps it closed. Set from the selection
  // bar or from a single missing row's link button.
  const [linkChapterIds, setLinkChapterIds] = useState<number[] | null>(null)
  const [relinkOpen, setRelinkOpen] = useState(false)
  const [deleteChaptersModalOpen, setDeleteChaptersModalOpen] = useState(false)
  // Chapter ids the "Delete files" dialog is working on; null keeps it closed. Set from the
  // selection bar or from a single row's menu.
  const [deleteFileIds, setDeleteFileIds] = useState<number[] | null>(null)
  const [deleteSeriesModalOpen, setDeleteSeriesModalOpen] = useState(false)
  const [deleteSeriesFiles, setDeleteSeriesFiles] = useState(false)
  const [archiveModalOpen, setArchiveModalOpen] = useState(false)
  const [archiveRemoveFiles, setArchiveRemoveFiles] = useState(false)
  const [restoreModalOpen, setRestoreModalOpen] = useState(false)
  const [removeFilesModalOpen, setRemoveFilesModalOpen] = useState(false)
  const binDays = useRecycleBinDays(deleteChaptersModalOpen || deleteFileIds !== null)
  // Which delete dialog the server answered with "the recycle bin cannot take these".
  const [binRefused, setBinRefused] = useState<'chapters' | 'files' | 'series' | 'archive' | 'archiveFiles' | null>(null)
  const onBinRefused = (dialog: 'chapters' | 'files' | 'series' | 'archive' | 'archiveFiles') => (error: unknown) => {
    if (offersPermanentDelete(error)) setBinRefused(dialog)
  }

  // Without DownloadChapters the two buttons that queue downloads become one that asks an admin to.
  const { can, me, singleUser } = useAuth()
  const { data: conversionHistory } = useSeriesConversion(seriesId, can('Admin'))
  const [undoConversionOpen, setUndoConversionOpen] = useState(false)
  const canDownload = can('DownloadChapters')
  const canEditMetadata = can('EditMetadata')
  const canDelete = can('DeleteSeries')
  const canArchive = can('ArchiveSeries')
  const pendingProposalId = series?.pendingProposalId ?? null
  const { data: seriesProposals } = useTorrentProposals(seriesId, canDownload && pendingProposalId != null)
  const pendingProposal = seriesProposals?.find((p) => p.id === pendingProposalId) ?? null
  const createRequest = useCreateSeriesRequest()
  // Already in cache: the sources section below this page fetches the same query. Two enabled
  // mappings is the floor for "find better copy" having anything to show.
  const canManageSources = can('ManageSources')
  const { data: sourceMappings } = useSourceMappings(seriesId, canManageSources)
  const enabledMappings = (sourceMappings ?? []).filter((m) => m.enabled).length
  /**
   * Read-aware filters, kept separate from `chapterFilters` because they need the progress map.
   * `unread` and `read` only consider downloaded chapters, plus read ones whose file was removed: a
   * missing chapter is neither read nor "left to read". `skipped` is the opposite case, chapters
   * passed over, so it looks at wanted ones whether or not a file exists.
   */
  const readLanguages = useMemo(() => readLanguagesOf(sourceMappings, sources), [sourceMappings, sources])
  const filters = useMemo<Record<string, (c: ChapterDto) => boolean>>(() => {
    const highWater = finishedHighWater(chapters ?? [], readProgress)
    return {
      ...chapterFilters,
      unread: (c: ChapterDto) => c.hasFile && !readStateFor(c).read,
      read: (c: ChapterDto) => (c.hasFile || c.fileRemovedAt != null) && readStateFor(c).read,
      skipped: (c: ChapterDto) => isSkipped(c, highWater, readProgress, readLanguages),
    }
  }, [chapters, readProgress, readStateFor, readLanguages])
  const [pickChapter, setPickChapter] = useState<PickChapter | null>(null)
  const [requestModalOpen, setRequestModalOpen] = useState(false)
  const [requestStart, setRequestStart] = useState<number | ''>('')
  const [requestEnd, setRequestEnd] = useState<number | ''>('')
  const [requestNote, setRequestNote] = useState('')

  const toggleChapterSelected = (id: number) =>
      setSelected((s) => {
        const next = new Set(s)
        if (next.has(id)) next.delete(id)
        else next.add(id)
        return next
      })

  const exitSelectMode = () => {
    setSelectMode(false)
    setSelected(new Set())
    selectAnchor.current = null
  }

  const tagListRef = useRef<HTMLDivElement>(null)

  // Straight from the DTO rather than recomputed off the chapter list: this page and the library
  // cards used to hold two independent copies of the same arithmetic, which is exactly how a
  // denominator change lands on one surface and not the other. Costs a refetch of `['series']` for
  // the bar to move after a Wanted toggle, which the toggle mutations already invalidate.
  //
  // Zeroed while the series is still loading: this hook has to run before the `!series` early
  // return below, so it can't be conditional and the render never reads it in that state anyway.
  const progress = useMemo(
      () =>
          seriesProgressVisual(
              series ?? { wantedChapterCount: 0, knownChapterCount: 0, chapterFileCount: 0, readChapterCount: null },
              readTracking,
          ),
      [series, readTracking],
  )

  const hasSpecials = useMemo(() => (chapters ?? []).some(isSpecial), [chapters])
  const canFilterUnread = readTracking && progress.have > 0
  const rememberedFilterMatchesNothing = useMemo(
      () =>
          chapterFilterPreference !== 'all' &&
          !(chapters ?? []).some(filters[chapterFilterPreference] ?? filters.all),
      [chapters, filters, chapterFilterPreference],
  )
  // The remembered filter may not be offered on this series, or may match nothing on it, and either
  // would show an empty table with nothing on screen saying why.
  const chapterFilter: ChapterFilter =
      (chapterFilterPreference === 'main' && !hasSpecials) ||
      (chapterFilterPreference === 'unread' && !canFilterUnread) ||
      (chapterFilterPreference === 'cutoffUnmet' && !cutoffUnmetChipVisible) ||
      (chapterFilterPreference === 'skipped' && !readTracking) ||
      rememberedFilterMatchesNothing
          ? 'all'
          : chapterFilterPreference

  /**
   * The rows the table is currently showing. Shift-ranges and "Select all" both work over this
   * rather than the full chapter list: with a filter active, a range drawn between two visible
   * rows would otherwise sweep in every hidden chapter numbered between them.
   */
  const visibleChapters = useMemo(() => {
    const query = chapterSearch.trim().toLocaleLowerCase()
    const numberQuery = query.match(/^(?:ch(?:apter)?\.?\s*)?(\d+(?:\.\d+)?)$/)?.[1]
    // The API sends numbered chapters ascending with unnumbered one-shots after them. Descending
    // flips only the numbered run, so a pile of one-shots doesn't bury the latest chapter.
    const ordered = chapterSort === 'desc'
        ? [
          ...(chapters ?? []).filter((c) => c.number !== null).reverse(),
          ...(chapters ?? []).filter((c) => c.number === null),
        ]
        : (chapters ?? [])
    return ordered
        .filter(filters[chapterFilter] ?? filters.all)
        .filter((chapter) => {
          if (!query) return true
          const number = chapter.number === null ? '' : String(chapter.number)
          if (numberQuery) return number === numberQuery
          return (
              chapterLabel(chapter).toLocaleLowerCase().includes(query) ||
              chapter.title?.toLocaleLowerCase().includes(query)
          )
        })
  }, [chapters, chapterSort, filters, chapterFilter, chapterSearch, i18n.locale])

  // "Main" is everything that isn't a decimal-numbered special, so one-shots land there rather
  // than in neither bucket, where the dropdown could never reach them.
  const visibleSpecials = useMemo(() => visibleChapters.filter(isSpecial), [visibleChapters])
  const visibleMain = useMemo(() => visibleChapters.filter((c) => !isSpecial(c)), [visibleChapters])

  /** Where the last plain click landed, i.e. the fixed end of a shift-range. */
  const selectAnchor = useRef<number | null>(null)

  /** Replaces the selection with the visible rows matching `pick`. */
  const selectAll = (pick: (c: ChapterDto) => boolean) => {
    selectAnchor.current = null
    setSelected(new Set(visibleChapters.filter(pick).map((c) => c.id)))
  }

  const clickChapterRow = (id: number, shiftKey: boolean) => {
    // Ranges walk the *rendered* rows, so a folded season counts as one step and drags in every
    // chapter it hides. Walking the raw visible list instead would select rows the user cannot see
    // while skipping the ones the fold is standing in for.
    const units = rangeUnits
    const anchor = selectAnchor.current
    const from = anchor === null ? -1 : units.findIndex((u) => u.ids.includes(anchor))
    const to = units.findIndex((u) => u.ids.includes(id))

    if (shiftKey && from !== -1 && to !== -1) {
      // Shift-clicking drags a text selection across the rows it spans; nothing here is text the
      // user wants highlighted, so drop it.
      window.getSelection()?.removeAllRanges()
      const [lo, hi] = from <= to ? [from, to] : [to, from]
      const range = units.slice(lo, hi + 1).flatMap((u) => u.ids)
      // The anchor stays put, so walking the far end of the range up and down re-draws it from
      // the same start instead of ratcheting forward one row at a time.
      setSelected((s) => new Set([...s, ...range]))
      return
    }

    selectAnchor.current = id
    toggleChapterSelected(id)
  }

  // What "Download all wanted" would actually queue, so the button can say so rather than making
  // the user open the Chapters tab to find out.
  const unlinkedFilesOnDisk = filesSummary?.unlinkedOnDisk ?? 0
  const missingWanted = useMemo(
      () => (chapters ?? []).filter((c) => c.wanted && !c.hasFile && !c.fileRemovedAt && !queueByChapterId.has(c.id)).length,
      [chapters, queueByChapterId],
  )
  const allWantedOnDisk = chapters !== undefined && missingWanted === 0

  // What "Delete files" will actually touch: a volume file backs several chapters, and the server
  // removes it from all of them, so the dialog has to say so before anyone confirms.
  const deleteFilesPlan = useMemo(() => {
    if (!deleteFileIds || !chapters) return null
    const named = new Set(deleteFileIds)
    const paths = new Set(
        chapters.filter((c) => named.has(c.id) && c.hasFile && c.filePath).map((c) => c.filePath!),
    )
    const affected = chapters.filter((c) => c.hasFile && c.filePath && paths.has(c.filePath))
    return { files: paths.size, others: affected.filter((c) => !named.has(c.id)).length }
  }, [deleteFileIds, chapters])


  const animeMarkers = useMemo(
      () => mergeAnimeMarkers(series?.animeStart, series?.animeEnd),
      [series?.animeStart, series?.animeEnd],
  )

  /** Highest chapter number the library knows about, null when nothing is numbered yet. */
  const lastChapterNumber = useMemo(() => {
    const numbered = (chapters ?? []).map((c) => c.number).filter((n): n is number => n !== null)
    return numbered.length === 0 ? null : Math.max(...numbered)
  }, [chapters])

  const animeSpans = useMemo(() => {
    if (lastChapterNumber === null) return [] as AnimeSpan[]
    return buildAnimeSpans(animeMarkers, lastChapterNumber)
  }, [animeMarkers, lastChapterNumber])

  /**
   * Which spans are folded into a single summary row. Keyed by span key rather than by index so a
   * metadata refresh that adds a season doesn't silently re-point an open fold at a different one.
   */
  const [foldedSpans, setFoldedSpans] = useState<Set<string>>(new Set())
  const foldsSeeded = useRef(false)

  const chaptersInSpan = useCallback(
      (span: AnimeSpan) =>
          (chapters ?? []).filter((c) => c.number !== null && c.number >= span.from && c.number <= span.to),
      [chapters],
  )

  /**
   * A season whose downloaded chapters are all finished starts folded — read or watched, either
   * way there is nothing left to do in it, so it is the run worth collapsing out of the way on
   * open. `read` covers both: a watched chapter carries `Completed` like any other.
   * <para>
   * Seeded once per series rather than kept in sync, so finishing the last chapter of a season, or
   * watching one off, doesn't yank it shut under the cursor mid-click.
   * </para>
   */
  useEffect(() => {
    if (progressRows === undefined || animeSpans.length === 0) return
    if (foldsSeeded.current) return
    foldsSeeded.current = true

    const folded = new Set<string>()
    for (const span of animeSpans) {
      // Only what's on disk: a season nobody has downloaded is not a season nobody has left to
      // read, and folding those would hide most of the table on a fresh series.
      const downloaded = chaptersInSpan(span).filter((c) => c.hasFile)
      if (downloaded.length > 0 && downloaded.every((c) => readStateFor(c).read)) {
        folded.add(span.key)
      }
    }
    setFoldedSpans(folded)
  }, [progressRows, animeSpans, seriesId, chaptersInSpan, readStateFor])

  const refocusSpan = useRef<string | null>(null)
  const toggleSpanFold = useCallback((key: string) => {
    refocusSpan.current = key
    setFoldedSpans((current) => {
      const next = new Set(current)
      if (!next.delete(key)) next.add(key)
      return next
    })
  }, [])

  // Folding swaps the control that was pressed for a different row, so focus would drop to the
  // page; hand it to the span's control in the new layout instead.
  useEffect(() => {
    const key = refocusSpan.current
    refocusSpan.current = null
    if (key === null || (document.activeElement && document.activeElement !== document.body)) return
    document.querySelector<HTMLElement>(`[data-span-fold="${CSS.escape(key)}"]`)?.focus()
  }, [foldedSpans])

  /** The span (if any) a marker at this exact chapter number both starts or ends, so its badge can
   *  double as the fold control and as the line's anchor. A marker that lost its pairing, or its
   *  lane to the cap, stays a plain informational badge — same as before spans existed. */
  const spanForMarker = useCallback(
      (num: number, kind: 'start' | 'end') =>
          animeSpans.find((s) => (kind === 'start' ? s.from === num : !s.openEnded && s.to === num)),
      [animeSpans],
  )

  /**
   * The rendered marker badges, keyed `{spanKey}:{start|end}`. The season lines are drawn as an
   * overlay positioned off these, so the anchors have to be the real DOM nodes: a season's run is
   * hundreds of variable-height rows and there is no arithmetic that predicts where its end badge
   * lands.
   */
  const markerRefs = useRef(new Map<string, HTMLElement>())
  const setMarkerRef = useCallback((key: string, el: HTMLElement | null) => {
    if (el) markerRefs.current.set(key, el)
    else markerRefs.current.delete(key)
  }, [])

  /**
   * State, not a `useRef`: the chapters tab is unmounted whenever another tab is showing
   * (`keepMounted={false}`), and a ref filling in does not re-run an effect. Measured off a plain
   * ref, the overlay effect ran once while the table did not exist and never again, so the season
   * lines only appeared if the page was loaded straight into Chapters.
   */
  const [chapterTable, setChapterTable] = useState<HTMLDivElement | null>(null)
  // Only the layout is state, so a window resize re-renders the page when it crosses a threshold
  // rather than on every pixel. The width itself goes to the column specs' drag limits.
  const [chapterLayoutByWidth, setChapterLayoutByWidth] = useState<ChapterLayout>('full')
  useEffect(() => {
    if (!chapterTable) return
    const observer = new ResizeObserver(([entry]) => {
      chapterColumnRoom.current.panel = entry.contentRect.width
      setChapterLayoutByWidth(chapterLayoutFor(entry.contentRect.width))
    })
    observer.observe(chapterTable)
    return () => observer.disconnect()
  }, [chapterTable])
  // A panel that can't hold the default columns first drops Released, then turns rows into two-line
  // list rows. A scrolling table squeezed Title to a few characters and pushed the switch and
  // actions out of view.
  const chapterLayout: ChapterLayout = isMobile ? 'list' : chapterLayoutByWidth
  const chapterRowsAsList = chapterLayout === 'list'
  const chapterTableMinWidth = chapterTableMinWidthFor(chapterColumnWidths, chapterLayout)
  useLayoutEffect(() => {
    chapterColumnRoom.current.layout = chapterLayout
    chapterColumnRoom.current.widths = chapterColumnWidths
  }, [chapterLayout, chapterColumnWidths])
  const [spanLines, setSpanLines] = useState<
      { key: string; label: string; top: number; height: number; left: number; openStart: boolean; openEnded: boolean }[]
  >([])
  /**
   * Width of the widest marker group, which becomes reserved padding on *every* Chapter cell.
   * Without it only the rows that actually carry a badge leave room at the right of the column,
   * and the chapter labels on all the others run straight under the line.
   */
  const [markerSlot, setMarkerSlot] = useState(0)
  const hasAnimeMarkers = animeMarkers.size > 0

  /**
   * The table's rows, with folded spans collapsed to one summary row each.
   *
   * Everything downstream — shift-ranges, the lane cells below — works over this rather than the
   * raw list, so what the table shows is what the toolbar acts on. A span with no visible chapter
   * under the active filter contributes nothing at all: no lane cell, no summary row.
   */
  const renderedRows = useMemo(() => {
    const visibleSpans = animeSpans
        .map((span) => ({
          span,
          rows: visibleChapters.filter(
              (c) => c.number !== null && c.number >= span.from && c.number <= span.to,
          ),
        }))
        .filter((s) => s.rows.length > 0)

    const foldedNow = visibleSpans.filter((s) => foldedSpans.has(s.span.key))
    const emitted = new Set<string>()
    const swallowed = new Set<number>()
    for (const { rows } of foldedNow) {
      for (const c of rows) swallowed.add(c.id)
    }

    const out: RenderedRow[] = []
    for (const c of visibleChapters) {
      if (swallowed.has(c.id)) {
        // The first row a fold swallows is where its summary goes. An inner span nested inside an
        // already-folded one never gets there, so it collapses into the outer summary rather than
        // producing a second row for chapters that are already gone.
        const owner = foldedNow.find((s) => s.rows.some((r) => r.id === c.id))
        if (owner && !emitted.has(owner.span.key)) {
          emitted.add(owner.span.key)
          out.push({ kind: 'span', span: owner.span, rows: owner.rows })
        }
        continue
      }
      out.push({ kind: 'chapter', chapter: c })
    }

    return { rows: out, visibleSpans }
  }, [visibleChapters, animeSpans, foldedSpans])

  /**
   * Highest chapter number the reader has finished, for the marker on the coverage bar. The DTO's
   * readChapterCount is a count of downloaded chapters below the high-water mark, which is not a
   * chapter number and drifts from one as soon as the library has gaps.
   */
  const highestReadChapter = useMemo(() => {
    let max: number | null = null
    for (const c of chapters ?? []) {
      if (c.number === null || !readStateFor(c).read) continue
      if (max === null || c.number > max) max = c.number
    }
    return max
  }, [chapters, readStateFor])

  const chapterPageSize =
      chapterPageSizePreference === 'all'
          ? Math.max(1, renderedRows.rows.length)
          : Number(chapterPageSizePreference)
  const chapterPageCount = Math.max(1, Math.ceil(renderedRows.rows.length / chapterPageSize))
  const chapterPageLabels = useMemo(
      () =>
          Array.from({ length: chapterPageCount }, (_, index) => {
            const rows = renderedRows.rows.slice(index * chapterPageSize, (index + 1) * chapterPageSize)
            const numbers = rows.flatMap((row) =>
                row.kind === 'chapter'
                    ? row.chapter.number === null
                        ? []
                        : [row.chapter.number]
                    : row.rows.flatMap((chapter) => (chapter.number === null ? [] : [chapter.number])),
            )

            if (numbers.length === 0) {
              const start = index * chapterPageSize + 1
              const end = index * chapterPageSize + rows.length
              return t`Items ${start}–${end}`
            }
            const first = Math.min(...numbers)
            const last = Math.max(...numbers)
            return first === last ? String(first) : `${first}–${last}`
          }),
      [renderedRows.rows, chapterPageCount, chapterPageSize, t, i18n.locale],
  )
  const currentChapterPage = Math.min(chapterPage, chapterPageCount)
  const pagedRows = useMemo(
      () =>
          renderedRows.rows.slice(
              (currentChapterPage - 1) * chapterPageSize,
              currentChapterPage * chapterPageSize,
          ),
      [renderedRows.rows, currentChapterPage, chapterPageSize],
  )
  /**
   * What the table body actually renders. Deferred, and empty while another tab is open, so that
   * opening Chapters commits the tab with a skeleton at once and builds the rows in a background
   * render that yields to the browser. A long page used to freeze the click for seconds. A sort or
   * search on a long page gets the same treatment: the old rows stay until the new ones are ready.
   */
  const shownRows = useDeferredValue(tab === 'chapters' ? pagedRows : NO_ROWS, NO_ROWS)
  const chapterRowsLoading = chapters === undefined || (shownRows.length === 0 && pagedRows.length > 0)

  useEffect(() => {
    setChapterPage(1)
    selectAnchor.current = null
  }, [chapterFilter, chapterSort, chapterSearch, chapterPageSizePreference])

  useEffect(() => {
    if (chapterPage > chapterPageCount) setChapterPage(chapterPageCount)
  }, [chapterPage, chapterPageCount])

  /** One entry per displayed row; a folded span is a single step carrying every chapter it hides. */
  const rangeUnits = useMemo(
      () =>
          shownRows.map((r) =>
              r.kind === 'chapter' ? { ids: [r.chapter.id] } : { ids: r.rows.map((c) => c.id) },
          ),
      [shownRows],
  )

  /**
   * Measures where each un-folded season's line should run: from just under its start badge down to
   * just above its end badge.
   *
   * This is deliberately an overlay measured from the DOM rather than anything woven into the
   * table. Every in-table approach tried before distorted the rows it crossed — a merged `rowSpan`
   * cell broke the row heights and the read-state tint at its boundaries, and per-cell borders and
   * box-shadows are per-row segments that can't be made to read as one continuous line. An absolute
   * overlay in a `position: relative` wrapper touches no table geometry at all.
   *
   * Positions are rect differences against that wrapper, so page scroll cancels out; the
   * ResizeObserver catches row-height changes and the scroll listener catches the table's own
   * horizontal scroll, which does move the Chapter column under the line.
   */
  useLayoutEffect(() => {
    const wrap = chapterTable
    if (!wrap) return

    const measure = () => {
      // A column drag moves the lines itself and re-measures once it lets go.
      if (document.body.classList.contains('column-resizing')) return
      const wrapRect = wrap.getBoundingClientRect()
      // Laid out but not yet painted (a hidden tab, a tab being restored). Every rect would read
      // zero and every span would fail the length test below, silently wiping the lines.
      if (wrapRect.height === 0) return

      // Widest marker group in the table, which is what every Chapter cell has to reserve. Measured
      // rather than assumed because the labels are free text out of MangaBaka — "S1" and
      // "Film + OVA" are wildly different widths. The groups are absolutely positioned out of the
      // label flow, so their own width doesn't depend on the padding this feeds them into.
      // Continuation pages can have no badges at all, but still need a lane for the line.
      let slot = 24
      for (const group of wrap.querySelectorAll<HTMLElement>('.chapter-span-markers')) {
        slot = Math.max(slot, group.offsetWidth)
      }
      // Reserving the slot widens the Chapter column, which moves the badges the lines are drawn
      // off. `markerSlot` is therefore a dependency of this effect, not just an output of it: the
      // pass that changes it re-runs and re-measures against the settled layout. Without that the
      // lines keep their pre-reflow x and sit over the chapter labels.
      setMarkerSlot((current) => (current === slot ? current : slot))

      const next: typeof spanLines = []
      const chapterCells = Array.from(wrap.querySelectorAll<HTMLElement>('.chapter-cell[data-chapter-number]'))
      for (const span of animeSpans) {
        // An off-page end continues the line; an inferred end is not an actual boundary.
        if (span.openEnded || foldedSpans.has(span.key)) continue
        const cells = chapterCells.filter((cell) => {
          const number = Number(cell.dataset.chapterNumber)
          return number >= span.from && number <= span.to
        })
        if (cells.length === 0) continue
        const startEl = markerRefs.current.get(`${span.key}:start`)
        const endEl = markerRefs.current.get(`${span.key}:end`)
        // Sorted descending, the end badge is the one on top.
        const topEl = chapterSort === 'desc' ? endEl : startEl
        const bottomEl = chapterSort === 'desc' ? startEl : endEl
        // Clip to the visible run, below the table header. Either badge can be on another
        // page (or hidden by a filter), including both on a middle page of a long season.
        const top = topEl
            ? topEl.getBoundingClientRect().bottom - wrapRect.top + 2
            : cells[0].getBoundingClientRect().top - wrapRect.top
        const bottom = bottomEl
            ? bottomEl.getBoundingClientRect().top - wrapRect.top - 2
            : cells[cells.length - 1].getBoundingClientRect().bottom - wrapRect.top
        if (bottom - top < 4) continue

        // Centred on the marker's *slot*, not on the badge inside it. Every slot is the width of
        // the series' widest label and centres its badge, so this is one shared x for every season
        // — a badge's own centre would drift with its label's width. Taking it off the slot rather
        // than assuming the badge is centred in it also keeps the anchor right on the rare chapter
        // that carries two markers (one season ending where the next begins).
        const anchor = startEl ?? endEl
        const holder = anchor?.closest('.chapter-span-markers') ?? cells[0].querySelector('.chapter-span-markers')
        if (!holder) continue
        const holderRect = holder.getBoundingClientRect()

        next.push({
          key: span.key,
          label: span.label,
          top,
          height: bottom - top,
          left: holderRect.left - wrapRect.left + holderRect.width / 2,
          openStart: !topEl,
          openEnded: !bottomEl,
        })
      }

      // Same-value bail-out: this runs from a ResizeObserver, and setting state that re-renders
      // into an observed subtree is how those turn into loops.
      setSpanLines((current) =>
          current.length === next.length &&
          current.every((c, i) =>
              c.key === next[i].key && c.label === next[i].label && c.top === next[i].top &&
              c.height === next[i].height && c.left === next[i].left &&
              c.openStart === next[i].openStart && c.openEnded === next[i].openEnded,
          )
              ? current
              : next,
      )
    }

    measure()
    // A second pass after the browser has settled the reflow the first one triggered. Fonts and
    // the Chapter column's width both land late, and neither resizes the wrapper, so nothing else
    // here would notice them.
    const frame = requestAnimationFrame(measure)

    const observer = new ResizeObserver(measure)
    observer.observe(wrap)
    // The table too, not just the wrapper: a column growing to fit its content is a layout change
    // the lines depend on, and it leaves the wrapper's own box untouched.
    const table = wrap.querySelector('table')
    if (table) observer.observe(table)

    const viewport = wrap.querySelector<HTMLElement>('[data-scrollarea-viewport], .mantine-ScrollArea-viewport')
    viewport?.addEventListener('scroll', measure, { passive: true })
    return () => {
      cancelAnimationFrame(frame)
      observer.disconnect()
      viewport?.removeEventListener('scroll', measure)
    }
  }, [chapterTable, animeSpans, foldedSpans, shownRows, markerSlot, chapterSort, chapterColumnWidths, chapterLayout])

  // On a phone only the toolbar stays pinned; the search scrolls away with the page, or the two
  // together cover a third of the screen.
  const chapterSearchInput = chapters && chapters.length > 0 && (
      <TextInput
          size="sm"
          mt={isMobile ? -8 : undefined}
          value={chapterSearch}
          onChange={(event) => setChapterSearch(event.currentTarget.value)}
          leftSection={<IconSearch size={16} />}
          rightSection={
            chapterSearch ? (
                <ActionIcon
                    size="sm"
                    variant="subtle"
                    color="var(--neutral)"
                    aria-label={t`Clear chapter search`}
                    onClick={() => setChapterSearch('')}
                >
                  <IconX size={14} />
                </ActionIcon>
            ) : null
          }
          placeholder={t`Search by chapter number or title`}
          aria-label={t`Search chapters`}
      />
  )

  const setChaptersState = useSetChaptersState(seriesId)

  /**
   * Applies a read state to a set of chapters and reports what happened. Shared by the select-mode
   * toolbar and the per-span menu so the two can't drift on which queries get invalidated.
   */
  const applyReadState = (chapterIds: number[], state: ChapterReadState, done?: () => void) => {
    if (chapterIds.length === 0) return
    setChaptersState.mutate(
        { chapterIds, state },
        {
          onSuccess: (r) => {
            const message =
                state === 'watched'
                    ? plural(r.updated, { one: 'Marked watched: # chapter', other: 'Marked watched: # chapters' })
                    : state === 'read'
                        ? plural(r.updated, { one: 'Marked read: # chapter', other: 'Marked read: # chapters' })
                        : plural(r.updated, { one: 'Marked unread: # chapter', other: 'Marked unread: # chapters' })
            notify.ok(message)
            done?.()
          },
        },
    )
  }

  /**
   * Sets Wanted across a set of chapters. Shared by the select-mode toolbar's Want/Don't want and
   * the folded-span switch, so the two can't drift on wording or on what gets invalidated.
   */
  const applyWanted = (chapterIds: number[], wanted: boolean) => {
    if (chapterIds.length === 0) return
    setChaptersWanted.mutate(
        { chapterIds, wanted },
        {
          onSuccess: (r) =>
              notify.ok(
                  wanted
                      ? plural(r.updated, { one: 'Want # chapter', other: 'Want # chapters' })
                      : plural(r.updated, { one: 'No longer want # chapter', other: 'No longer want # chapters' }),
              ),
        },
    )
  }

  // The rows are memoized, so everything they call goes through these stable objects: a handler
  // that changed identity every render would re-render all of them on any state change at all.
  const chapterRowActions = useStableActions<ChapterRowActions>({
    clickRow: clickChapterRow,
    toggleWanted: (chapterId, wanted) => toggleWanted.mutate({ chapterId, wanted }),
    setRead: (chapterId, read) => setRead.mutate({ chapterId, read }),
    link: (chapterId) => setLinkChapterIds([chapterId]),
    download: (chapterId, chapterLbl) =>
      search.mutate(chapterId, {
        onSuccess: () => notify.ok(staticT`Queued ${chapterLbl}`),
      }),
    pickChapter: setPickChapter,
    upgradeNow: (chapterId) =>
      upgradeChapterNow.mutate(chapterId, {
        onSuccess: (result) => {
          setUpgradeNowResult(result)
          setUpgradeNowModalOpen(true)
        },
        onError: (error) => {
          notify.err(
              error instanceof ApiError && error.status === 409
                  ? staticT`A scan is already running`
                  : staticT`Couldn't run the scan`,
          )
        },
      }),
    setTrusted: (fileId, trusted) => setFileTrusted.mutate({ fileId, trusted }),
    deleteFile: (chapterId) => setDeleteFileIds([chapterId]),
    saveOffline: (chapterId) => void saveChaptersOffline([{ chapterId, seriesId }]),
    removeOffline: (chapterId) => void removeChaptersOffline([chapterId]),
    toggleSpanFold,
    setMarkerRef,
  })
  const spanRowActions = useStableActions<ChapterSpanRowActions>({
    applyWanted,
    applyReadState,
    selectChapters: (ids) => {
      setSelectMode(true)
      selectAnchor.current = null
      setSelected(new Set(ids))
    },
    toggleSpanFold,
  })

  const nextChapter = useMemo(
      () => {
        if (!chapters) return null

        const next = chapters.find((c) => c.id === continueAt?.chapterId) ?? null
        if (!next) return null

        return chapterLabel(next)
      },
      // `continueAt` resolves after `chapters` on a cold load, so without it in the deps the button
      // renders without its chapter number until something else changes the chapter list's identity.
      [chapters, continueAt?.chapterId, i18n.locale]
  )

  // Arrived by a dice roll: offer another. Local state so a tab switch, which drops router state,
  // does not take the pill with it.
  const location = useLocation()
  const arrivedLucky = Boolean((location.state as { lucky?: boolean } | null)?.lucky)
  const [lucky, setLucky] = useState(arrivedLucky)
  const { data: library } = useSeries()
  const luckyPool = useMemo(
    () =>
      activeSeries(library ?? [])
        .filter((s) => s.id !== seriesId && isUnfinished(s))
        .map((s) => ({ key: String(s.id), title: s.displayTitle, coverUrl: s.coverUrl })),
    [library, seriesId],
  )
  const luckyPill = lucky ? (
    <LuckyButton
      variant="pill"
      candidates={luckyPool}
      onPick={(next) => {
        navigate(`/series/${next}`, { state: { lucky: true }, replace: true })
      }}
      onDismiss={() => {
        setLucky(false)
        navigate({ pathname: location.pathname, search: location.search }, { replace: true, state: null })
      }}
    />
  ) : null

  if (isLoading) {
    return (
        <SurfaceFrame width="full" pageStyle="editorial">
          <SeriesHeroSkeleton />
          {luckyPill}
        </SurfaceFrame>
    )
  }

  // Offline the series is not gone, this device just holds no copy of its page.
  if (!series && isOfflineUnavailable(seriesError)) {
    return (
        <SurfaceFrame width="full" pageStyle="editorial">
          <EmptyState
              art="offline"
              headingOrder={1}
              title={t`Not available offline`}
              description={t`This series isn't saved on this device. It opens again as soon as Maki can reach the server.`}
              actionLabel={t`Back to library`}
              actionTo="/library"
              actionIcon={<IconArrowLeft size={16} />}
          />
        </SurfaceFrame>
    )
  }

  if (!series) {
    return (
        <SurfaceFrame width="full" pageStyle="editorial">
          <EmptyState
              art="missing"
              headingOrder={1}
              code={`#${id}`}
              title={t`Series not found`}
              description={t`It may have been removed from the library.`}
              actionLabel={t`Back to library`}
              actionTo="/library"
              actionIcon={<IconArrowLeft size={16} />}
              secondaryActionLabel={can('AddSeries') ? t`Search MangaBaka` : undefined}
              secondaryActionTo="/add"
              secondaryActionIcon={<IconSearch size={16} />}
          />
        </SurfaceFrame>
    )
  }

  const status = seriesStatusVisual(series.status)
  const contentRating = contentRatingVisual(series.contentRating)
  const altTitles = readableTitles(series.altTitles, i18n.locale)
  const seriesTitle = series.title
  const scanSeriesForUpgrades = () =>
    runUpgradeScan.mutate(seriesId, {
      onSuccess: () => {
        notify.ok(staticT`Scan started`)
      },
      onError: (error) => {
        notify.err(
            error instanceof ApiError && error.status === 409
                ? staticT`A scan is already running`
                : staticT`Couldn't start the scan`,
        )
      },
    })
  const searchSeriesVolumes = () =>
    runVolumeSearch.mutate(seriesId, {
      onSuccess: (result) => {
        notify.ok(isVolumeSearchStarted(result) ? staticT`Volume search started` : volumeSearchResultText(renderLabel, result))
      },
      onError: (error) => {
        notify.err(
            error instanceof ApiError && error.status === 409
                ? staticT`A scan is already running`
                : staticT`Couldn't start the volume search`,
        )
      },
    })
  const wantedFilterCount = chapters?.filter(chapterFilters.wanted).length ?? 0
  const missingFilterCount = chapters?.filter(chapterFilters.missing).length ?? 0
  const downloadedFilterCount = chapters?.filter(chapterFilters.downloaded).length ?? 0
  const unreadFilterCount = chapters?.filter(filters.unread).length ?? 0
  const specialsFilterCount = chapters?.filter(chapterFilters.specials).length ?? 0
  const mainFilterCount = chapters?.filter(chapterFilters.main).length ?? 0
  const readFilterCount = chapters?.filter(filters.read).length ?? 0
  const cutoffUnmetFilterCount = chapters?.filter(chapterFilters.cutoffUnmet).length ?? 0
  const skippedFilterCount = chapters?.filter(filters.skipped).length ?? 0
  const selectedCount = selected.size
  const visibleAllCount = visibleChapters.length
  const visibleMainCount = visibleMain.length
  const visibleSpecialsCount = visibleSpecials.length
  const currentPageLabel = chapterPageLabels[currentChapterPage - 1]
  const chapterFilterData = chapters
      ? [
        { value: 'all', label: t`All` },
        { value: 'wanted', label: t`Wanted (${wantedFilterCount})` },
        { value: 'missing', label: t`Missing (${missingFilterCount})` },
        { value: 'downloaded', label: t`Have (${downloadedFilterCount})` },
        ...(canFilterUnread
            ? [{ value: 'unread', label: t`Unread (${unreadFilterCount})` }]
            : []),
        // Without a special to hide, "Main" is "All" under a second name.
        ...(hasSpecials ? [{ value: 'main', label: t`Main (${mainFilterCount})` }] : []),
        { value: 'specials', label: t`Specials (${specialsFilterCount})` },
        ...(cutoffUnmetChipVisible
            ? [{ value: 'cutoffUnmet', label: t`Cutoff unmet (${cutoffUnmetFilterCount})` }]
            : []),
        ...(readTracking && skippedFilterCount > 0
            ? [{ value: 'skipped', label: t`Skipped (${skippedFilterCount})` }]
            : []),
      ]
      : []

  const queueNext = (count: number) => {
    downloadNext.mutate(
        { seriesId, count },
        {
          onSuccess: (r) => {
            setNextCountOpen(false)
            if (r.queued > 0) notify.ok(plural(r.queued, { one: 'Queued # chapter', other: 'Queued # chapters' }))
            else notify.info(staticT`Nothing left to queue. Every wanted chapter is on disk or already queued.`)
          },
        },
    )
  }

  const queueAllWanted = () =>
      searchMissing.mutate(seriesId, {
        onSuccess: (r) =>
            notify.ok(plural(r.queued, { one: 'Queued # missing chapter', other: 'Queued # missing chapters' })),
      })

  const requestQueueAllWanted = () => {
    if (missingWanted >= LARGE_WANTED_DOWNLOAD_THRESHOLD) {
      setDownloadAllConfirmOpen(true)
      return
    }
    queueAllWanted()
  }

  const submitRating = (rating: number | null) =>
      setRating.mutate(
          { seriesId, rating },
          {
            onSuccess: () =>
                notify.ok(rating === null ? staticT`Rating cleared` : staticT`Rated ${rating}/10`),
          },
      )

  const markAnimeWatched = (coveredTo: number) =>
      applyAnimeResume.mutate(
          { markWatched: true, coveredTo },
          {
            onSuccess: (result) => {
              const to = result.coveredTo
              notifications.show({ color: 'var(--ok)', message: <Trans>Marked ch. 1 to {to} watched</Trans> })
            },
          },
      )

  const readFromAnime = () => {
    if (!animeResume) return
    const resumeAt = animeResume.resumeAt
    applyAnimeResume.mutate(
        { markWatched: false },
        {
          onSuccess: (result) => {
            const chapterId = result.resumeChapterId ?? animeResume.resumeChapterId
            if (animeResume.resumeDownloaded && chapterId != null) {
              navigate(`/read/${chapterId}`)
            } else {
              changeTab('chapters')
              notifications.show({ message: <Trans>Chapter {resumeAt} is not downloaded yet</Trans> })
            }
          },
        },
    )
  }

  // Same shape as `ReadingCardMenu`'s remove: `mutateAsync` rather than `mutate` with callbacks,
  // since the callout unmounts as soon as the query invalidates and per-call callbacks would never
  // fire for a component that's gone.
  const dismissAnimeResume = async () => {
    try {
      await dismissAnimeResumeMutation.mutateAsync({})
    } catch (error) {
      notifications.show({ color: 'var(--danger)', message: errorText(error) })
      return
    }

    // Kept open (not hidden) until the undo mutation actually resolves, so a failure has
    // somewhere to show a retry rather than silently vanishing along with the toast.
    const performUndo = async () => {
      notifications.update({
        id,
        autoClose: false,
        message: (
          <Group gap="xs" wrap="nowrap" justify="space-between">
            <Text size="sm">
              <Trans>Won't suggest resuming from this anime again.</Trans>
            </Text>
            <Button size="xs" variant="subtle" style={{ flexShrink: 0 }} loading disabled>
              <Trans>Undo</Trans>
            </Button>
          </Group>
        ),
      })
      try {
        await dismissAnimeResumeMutation.mutateAsync({ undo: true })
        notifications.hide(id)
      } catch (error) {
        const reason = errorText(error)
        notifications.update({
          id,
          color: 'var(--danger)',
          autoClose: false,
          message: (
            <Group gap="xs" wrap="nowrap" justify="space-between">
              <Text size="sm">
                <Trans>Undo failed: {reason}</Trans>
              </Text>
              <Button size="xs" variant="subtle" style={{ flexShrink: 0 }} onClick={performUndo}>
                <Trans>Retry</Trans>
              </Button>
            </Group>
          ),
        })
      }
    }

    const id = notifications.show({
      autoClose: 8000,
      message: (
        <Group gap="xs" wrap="nowrap" justify="space-between">
          <Text size="sm">
            <Trans>Won't suggest resuming from this anime again.</Trans>
          </Text>
          <Button size="xs" variant="subtle" style={{ flexShrink: 0 }} onClick={performUndo}>
            <Trans>Undo</Trans>
          </Button>
        </Group>
      ),
    })
  }

  const closeRemoveChapters = () => {
    setDeleteChaptersModalOpen(false)
    setBinRefused(null)
  }

  const removeChapters = (permanent = false) =>
    deleteChapters.mutate(
      { chapterIds: [...selected], permanent },
      {
        onSuccess: (r) => {
          notify.ok(plural(r.deleted, { one: 'Removed # chapter', other: 'Removed # chapters' }))
          closeRemoveChapters()
          exitSelectMode()
        },
        onError: onBinRefused('chapters'),
      },
    )

  const closeDeleteFiles = () => {
    setDeleteFileIds(null)
    setBinRefused(null)
  }

  const deleteFilesOf = (permanent = false) =>
    deleteChapterFiles.mutate(
      { chapterIds: deleteFileIds ?? [], permanent },
      {
        onSuccess: (r) => {
          const gone = r.deleted + r.kept
          if (gone > 0) {
            notify.ok(
              permanent
                ? plural(gone, { one: 'Deleted # file', other: 'Deleted # files' })
                : plural(gone, { one: 'Moved # file to the recycle bin', other: 'Moved # files to the recycle bin' }),
            )
          }
          if (r.failed > 0) {
            notify.err(
              plural(r.failed, {
                one: "Couldn't delete # file, check the log",
                other: "Couldn't delete # files, check the log",
              }),
            )
          }
          closeDeleteFiles()
          if (selectMode) exitSelectMode()
        },
        onError: onBinRefused('files'),
      },
    )

  const removeSeries = (permanent = false) =>
    deleteSeries.mutate(
      { id: series.id, deleteFiles: deleteSeriesFiles, permanent },
      {
        onSuccess: () => {
          notify.ok(staticT`Series removed`)
          navigate('/library')
        },
        onError: onBinRefused('series'),
      },
    )

  const openArchiveDialog = () => {
    setArchiveRemoveFiles(false)
    setBinRefused(null)
    setArchiveModalOpen(true)
  }

  const restoreArchived = (redownloadRemoved: boolean) =>
    restoreSeries.mutate(
      { id: series.id, redownloadRemoved },
      {
        onSuccess: (result) => {
          setRestoreModalOpen(false)
          showRestoreResult(result, redownloadRemoved)
        },
      },
    )

  // With every file gone there is a choice to make about fetching them again; otherwise nothing to ask.
  const restoreFromMenu = () =>
    series.chapterFileCount === 0 && series.removedChapterCount > 0 ? setRestoreModalOpen(true) : restoreArchived(false)

  const removeArchivedSeriesFiles = (permanent = false) =>
    removeArchivedFiles.mutate(
      { id: series.id, permanent },
      {
        onSuccess: () => {
          setRemoveFilesModalOpen(false)
          setBinRefused(null)
          notify.ok(staticT`Files removed`)
        },
        onError: onBinRefused('archiveFiles'),
      },
    )

  const archived = series.archivedAt != null
  const archivedReason = archived ? t`Archived series are not checked against sources. Restore it first.` : null
  const archivedBy =
    archived && !singleUser && me != null && series.archivedByUserId === me.id ? (me.displayName ?? me.userName) : null

  type ChapterBulkAction = {
    key: string
    label: ReactNode
    icon: ReactNode
    run: () => void
    loading?: boolean
    disabled?: boolean
  }
  // Accent buttons for the everyday three, everything else in More, and the destructive pair behind
  // one red menu. A colour per action read as a row of unrelated badges.
  const chapterPrimaryActions: ChapterBulkAction[] = [
    ...(canDownload
      ? [{
          key: 'download',
          label: <Trans>Download</Trans>,
          icon: <IconDownload size={15} />,
          disabled: archived,
          loading: downloadChapters.isPending,
          run: () => {
            if (archived) return
            downloadChapters.mutate([...selected], {
              onSuccess: (r) =>
                r.queued > 0
                  ? notify.ok(plural(r.queued, { one: 'Queued # chapter', other: 'Queued # chapters' }))
                  : notify.info(r.error ?? staticT`Nothing to queue, those chapters are already on disk`),
            })
          },
        }]
      : []),
    ...(readTracking
      ? [{
          key: 'read',
          label: <Trans>Mark read</Trans>,
          icon: <IconEyeCheck size={15} />,
          loading: setChaptersState.isPending && setChaptersState.variables?.state === 'read',
          run: () => applyReadState([...selected], 'read'),
        }]
      : []),
    ...(canEditMetadata
      ? [{
          key: 'want',
          label: <Trans>Want</Trans>,
          icon: <IconEye size={15} />,
          loading: setChaptersWanted.isPending && setChaptersWanted.variables?.wanted === true,
          run: () => applyWanted([...selected], true),
        }]
      : []),
  ]
  const chapterMoreGroups: ChapterBulkAction[][] = [
    readTracking
      ? [
          {
            key: 'unread',
            label: <Trans>Mark unread</Trans>,
            icon: <IconEyeOff size={15} />,
            loading: setChaptersState.isPending && setChaptersState.variables?.state === 'unread',
            run: () => applyReadState([...selected], 'unread'),
          },
          {
            key: 'watched',
            label: <Trans>Mark watched</Trans>,
            icon: <IconDeviceTv size={15} />,
            loading: setChaptersState.isPending && setChaptersState.variables?.state === 'watched',
            run: () => applyReadState([...selected], 'watched'),
          },
        ]
      : [],
    canEditMetadata
      ? [
          {
            key: 'unwant',
            label: <Trans>Don't want</Trans>,
            icon: <IconEyeOff size={15} />,
            loading: setChaptersWanted.isPending && setChaptersWanted.variables?.wanted === false,
            run: () => applyWanted([...selected], false),
          },
        ]
      : [],
    canEditMetadata
      ? [
          {
            key: 'link',
            label: <Trans>Link to file</Trans>,
            icon: <IconLink size={15} />,
            run: () => setLinkChapterIds([...selected]),
          },
          {
            key: 'unlink',
            label: <Trans>Unlink</Trans>,
            icon: <IconLinkOff size={15} />,
            loading: unlinkChapters.isPending,
            run: () =>
              unlinkChapters.mutate([...selected], {
                onSuccess: (r) => {
                  notify.ok(plural(r.unlinked, { one: 'Unlinked # chapter', other: 'Unlinked # chapters' }))
                  exitSelectMode()
                },
              }),
          },
        ]
      : [],
  ].filter((g) => g.length > 0)
  const chapterMoreBusy = chapterMoreGroups.some((g) => g.some((a) => a.loading))

  const showArchivedToast = (filesKept: boolean) => {
    const id = notifications.show({
      color: 'var(--ok)',
      autoClose: filesKept ? 8000 : 4000,
      message: (
        <Group gap="xs" wrap="nowrap" justify="space-between">
          <Text size="sm">
            <Trans context="series">Archived</Trans>
          </Text>
          {filesKept && (
            <Button
              size="xs"
              variant="subtle"
              style={{ flexShrink: 0 }}
              onClick={() => {
                notifications.hide(id)
                restoreArchived(false)
              }}
            >
              <Trans>Undo</Trans>
            </Button>
          )}
        </Group>
      ),
    })
  }

  const archiveCurrent = (permanent = false) =>
    archiveSeries.mutate(
      { id: series.id, removeFiles: archiveRemoveFiles, permanent },
      {
        onSuccess: () => {
          setArchiveModalOpen(false)
          setBinRefused(null)
          showArchivedToast(!archiveRemoveFiles)
        },
        onError: onBinRefused('archive'),
      },
    )

  return (
    <SurfaceFrame width="full" pageStyle="editorial">
      <Tabs
          className="series-detail-surface"
          value={tab}
          onChange={changeTab}
          variant="unstyled"
          // Unmounted rather than hidden: the chapter table's season overlay measures real DOM rects,
          // and a laid-out-but-unpainted table reads every one of them as zero.
          keepMounted={false}
          classNames={{ list: 'series-tabs', tab: 'series-tab', panel: 'series-body' }}
      >
        <SeriesHero
            series={series}
            onRate={submitRating}
            actions={
              <>
                {continueAt && (
                    <Button
                        component={Link}
                        to={`/read/${continueAt.chapterId}`}
                        size="md"
                        radius="md"
                        leftSection={<IconBook size={18} />}
                    >
                      {continueAt.page > 0 ? (
                          nextChapter ? <Trans>Continue reading {nextChapter}</Trans> : <Trans>Continue reading</Trans>
                      ) : nextChapter ? (
                          <Trans>Read {nextChapter}</Trans>
                      ) : (
                          <Trans>Read</Trans>
                      )}
                    </Button>
                )}
                {canDownload ? (
                    <>
                      {/* Unticking chapters used to be the only way to download a series a bit at a time,
                    which is what made the wanted switch double as a deferral tool and wrecked every
                    chapter count. This is the replacement: "all wanted" is the old Search missing,
                    "next N" queues in chapter-number order using the same selector Smart top-ups use. */}
                      {!allWantedOnDisk && (
                        <Button.Group>
                        {/* data-disabled rather than disabled: a disabled button gets no hover, so the
                            tooltip saying why an archived series cannot download would never show. */}
                        <Tooltip label={archivedReason} withArrow disabled={!archived} events={TOOLTIP_EVENTS}>
                        <Button
                            variant="default"
                            size="md"
                            radius="md"
                            leftSection={<IconDownload size={17} />}
                            loading={searchMissing.isPending || downloadNext.isPending}
                            data-disabled={archived || undefined}
                            aria-disabled={archived || undefined}
                            onClick={() => {
                              if (!archived) requestQueueAllWanted()
                            }}
                        >
                          {/* The count is the point of the label: it says what the click will actually
                        queue, so nobody has to open the Chapters tab to find out. */}
                          {missingWanted > 0 ? (
                              <Trans>Download {missingWanted} wanted</Trans>
                          ) : (
                              <Trans>Download all wanted</Trans>
                          )}
                        </Button>
                        </Tooltip>
                        {archived ? (
                        <Tooltip label={archivedReason} withArrow events={TOOLTIP_EVENTS}>
                          <Button
                              variant="default"
                              size="md"
                              radius="md"
                              px={10}
                              aria-label={t`More download options`}
                              data-disabled
                              aria-disabled
                          >
                            <IconChevronDown size={16} />
                          </Button>
                        </Tooltip>
                        ) : (
                        <Menu position="bottom-end" withinPortal>
                          <Menu.Target>
                            <Button
                                variant="default"
                                size="md"
                                radius="md"
                                px={10}
                                aria-label={t`More download options`}
                                disabled={searchMissing.isPending || downloadNext.isPending}
                            >
                              <IconChevronDown size={16} />
                            </Button>
                          </Menu.Target>
                          <Menu.Dropdown>
                            <Menu.Label><Trans>Download the next</Trans></Menu.Label>
                            {[10, 25].map((n) => (
                                <Menu.Item key={n} onClick={() => queueNext(n)}>
                                  <Plural value={n} one="Next # chapter" other="Next # chapters" />
                                </Menu.Item>
                            ))}
                            <Menu.Item onClick={() => setNextCountOpen(true)}><Trans>Next...</Trans></Menu.Item>
                          </Menu.Dropdown>
                        </Menu>
                        )}
                        </Button.Group>
                      )}
                      <Tooltip label={archivedReason} withArrow disabled={!archived} events={TOOLTIP_EVENTS}>
                      <Button
                          variant="default"
                          size="md"
                          radius="md"
                          leftSection={<IconSearch size={17} />}
                          data-disabled={archived || undefined}
                          aria-disabled={archived || undefined}
                          onClick={() => {
                            if (!archived) setReleaseModalOpen(true)
                          }}
                      >
                        <Trans>Search releases</Trans>
                      </Button>
                      </Tooltip>
                    </>
                ) : (
                    <Tooltip label={archivedReason} withArrow disabled={!archived} events={TOOLTIP_EVENTS}>
                    <Button
                        variant="default"
                        size="md"
                        radius="md"
                        leftSection={<IconSend size={17} />}
                        data-disabled={archived || undefined}
                        aria-disabled={archived || undefined}
                        onClick={() => {
                          if (archived) return
                          setRequestStart('')
                          setRequestEnd('')
                          setRequestNote('')
                          setRequestModalOpen(true)
                        }}
                    >
                      <Trans>Request chapters</Trans>
                    </Button>
                    </Tooltip>
                )}
                <SeriesActionsMenu
                    monitorMode={series.monitorNewItems}
                    incognito={series.incognito}
                    notificationMode={series.notificationMode}
                    upgradeProfileId={series.upgradeProfileId}
                    upgradeProfiles={upgradeProfiles ?? []}
                    readFileCleanup={series.readFileCleanup}
                    readFileCleanupActive={series.readFileCleanupActive}
                    onSetReadFileCleanup={(mode) =>
                        setReadFileCleanup.mutate({ seriesId, mode }, { onSuccess: () => notify.ok(staticT`Saved`) })
                    }
                    pageConversion={series.pageConversion}
                    canSetPageConversion={can('Admin')}
                    onSetPageConversion={(mode) =>
                        setSeriesPageConversion.mutate({ seriesId, mode }, { onSuccess: () => notify.ok(staticT`Saved`) })
                    }
                    onUndoConversion={
                        conversionHistory && conversionHistory.restorableFiles > 0
                            ? () => setUndoConversionOpen(true)
                            : undefined
                    }
                    canEdit={canEditMetadata}
                    canCleanUpReadFiles={canDelete}
                    canScanUpgrades={canDownload}
                    scanningUpgrades={runUpgradeScan.isPending || scanning}
                    busy={refresh.isPending || refreshMetadata.isPending || rescan.isPending}
                    onRefreshChapters={() =>
                        refresh.mutate(seriesId, {
                          onSuccess: (r) =>
                              notify.ok(
                                  plural(r.newChapters, {
                                    one: 'Refreshed, # new chapter',
                                    other: 'Refreshed, # new chapters',
                                  }),
                              ),
                        })
                    }
                    onRefreshMetadata={() =>
                        refreshMetadata.mutate(seriesId, {
                          onSuccess: () => notify.ok(staticT`Metadata and poster refreshed`),
                        })
                    }
                    onRescan={() =>
                        rescan.mutate(seriesId, {
                          onSuccess: (r) => {
                            const { newFiles, relinked, removed } = r
                            notify.ok(staticT`Rescanned: ${newFiles} new, ${relinked} relinked, ${removed} removed`)
                          },
                        })
                    }
                    onMove={() => {
                      setMoveTarget(null)
                      setMoveFiles(true)
                      setMoveModalOpen(true)
                    }}
                    onRename={() => setRenameModalOpen(true)}
                    onEditMetadata={() => setEditMetadataOpen(true)}
                    onSetMonitor={(mode) =>
                        setMonitorMode.mutate(
                            { seriesId, mode },
                            {
                              onSuccess: (r) => {
                                const modeLabel = renderLabel(MONITOR_MODE_LABELS[r.mode] ?? r.mode)
                                notify.ok(staticT`Monitoring: ${modeLabel}`)
                              },
                            },
                        )
                    }
                    onSetUpgradeProfile={(upgradeProfileId) =>
                        setUpgradeProfile.mutate(
                            { seriesId, upgradeProfileId },
                            {
                              onSuccess: () => {
                                const profileName = upgradeProfiles?.find((p) => p.id === upgradeProfileId)?.name
                                notify.ok(
                                    profileName
                                        ? staticT`Quality profile: ${profileName}`
                                        : staticT`Quality profile: Instance default`,
                                )
                              },
                            },
                        )
                    }
                    onSetIncognito={(mode) =>
                        setIncognito.mutate(
                            { seriesId, mode },
                            {
                              onSuccess: (r) => {
                                const incognito = incognitoOptions.find((o) => o.value === r.incognito)?.label ?? r.incognito
                                notify.ok(staticT`Incognito: ${incognito}`)
                              },
                            },
                        )
                    }
                    onSetNotify={(mode) =>
                        setNotificationMode.mutate(
                            { seriesId, mode },
                            {
                              onSuccess: (r) => {
                                const notificationMode =
                                    notificationOptions.find((o) => o.value === r.notificationMode)?.label ?? r.notificationMode
                                notify.ok(staticT`Notifications: ${notificationMode}`)
                              },
                            },
                        )
                    }
                    onScanUpgrades={scanSeriesForUpgrades}
                    searchingVolumes={runVolumeSearch.isPending}
                    onSearchVolumes={searchSeriesVolumes}
                    canArchive={canArchive}
                    archived={archived}
                    sourceToolsDisabledReason={archivedReason}
                    onArchive={openArchiveDialog}
                    onRestore={restoreFromMenu}
                    canRemove={can('DeleteSeries')}
                    onRemove={() => {
                      setDeleteSeriesFiles(false)
                      setDeleteSeriesModalOpen(true)
                    }}
                    offlineSaved={canSaveOffline ? offlineSaved : undefined}
                    onSaveUnreadOffline={() =>
                        void saveUnreadOffline(seriesId).then(
                            (count) =>
                                count > 0
                                    ? notify.ok(
                                        plural(count, {
                                          one: 'Saving # chapter for offline',
                                          other: 'Saving # chapters for offline',
                                        }),
                                    )
                                    : notify.info(staticT`Every unread chapter is already on this device`),
                            (error: unknown) => notify.err(error instanceof Error ? error.message : String(error)),
                        )
                    }
                    onRemoveOffline={() => void removeSeriesOffline(seriesId)}
                />
                {/* A state, not a control: nothing here could enable a download button, so it stops posing as one. */}
                {canDownload && allWantedOnDisk && chapters.length > 0 && (
                    <Tooltip label={t`Every wanted chapter is already on disk`} withArrow>
                      <Text component="span" size="sm" c="var(--ink-3)" className="series-actions-done">
                        <IconCircleCheck size={16} />
                        <Trans>Nothing left to download</Trans>
                      </Text>
                    </Tooltip>
                )}
              </>
            }
            tabs={
              <Tabs.List>
                <Tabs.Tab value="details"><Trans>Details</Trans></Tabs.Tab>
                <Tabs.Tab value="chapters">
                  <Trans>Chapters</Trans>
                  <span className="series-tab-count tnum">{series.knownChapterCount}</span>
                </Tabs.Tab>
                <Tabs.Tab value="files">
                  <Trans>Files</Trans>
                  <span className="series-tab-count tnum">{filesSummary ? filesSummary.count : "?"}</span>
                </Tabs.Tab>
              </Tabs.List>
            }
        />

        {/* One hint at a time: rating comes first, the archive offer follows once it is settled. */}
        {series.rateHintEligible ? (
          <RateHint onRate={submitRating} onDismiss={() => dismissRateHint.mutate(series.id)} />
        ) : series.archiveHintEligible && series.archivedAt == null && canArchive && (
          <ArchiveHint
            onArchive={openArchiveDialog}
            onDismiss={() => dismissArchiveHint.mutate(series.id)}
          />
        )}

        {series.archivedAt != null && (
          <ArchivedBanner
            archivedAt={series.archivedAt}
            archivedBy={archivedBy}
            onRestore={canArchive ? restoreFromMenu : undefined}
            restoring={restoreSeries.isPending}
            onRemoveFiles={
              canDelete && series.chapterFileCount > 0
                ? () => {
                    setBinRefused(null)
                    setRemoveFilesModalOpen(true)
                  }
                : undefined
            }
          />
        )}

        <Tabs.Panel value="details">
          <Stack className="series-detail-overview" gap="lg">
            {animeResume && (
                <AnimeResumeCallout
                    resume={animeResume}
                    variant="library"
                    pending={
                      applyAnimeResume.isPending
                        ? applyAnimeResume.variables?.markWatched
                          ? 'mark'
                          : 'read'
                        : dismissAnimeResumeMutation.isPending
                          ? 'dismiss'
                          : null
                    }
                    onMarkWatched={markAnimeWatched}
                    onRead={readFromAnime}
                    onDismiss={() => void dismissAnimeResume()}
                />
            )}
            <div className="series-split">
              <Panel className="series-detail-synopsis">
                <Title order={2} size="h3" fz="var(--type-section)">
                  <Trans>Synopsis</Trans>
                  {series.lockedFields.includes('overview') && <>{' '}<MetadataLock /></>}
                </Title>
                {series.overview ? (
                    <Text size="sm" mt="sm" c="var(--ink-3)" style={{ lineHeight: 1.66, maxWidth: '75ch' }}>
                      {cleanSynopsis(series.overview)}
                    </Text>
                ) : (
                    <Text size="sm" mt="sm" c="var(--ink-3)">
                      <Trans>No synopsis yet.</Trans> <Trans>Refresh metadata to fetch one.</Trans>
                    </Text>
                )}

                {(series.animeStart || series.animeEnd) && (
                    <>
                      <Divider my="md" color="var(--hairline)" />
                      <Title order={3} size="h4" fz="var(--type-body)" mb={10}>
                        <Trans>Anime coverage</Trans>
                      </Title>
                      <AnimeCoverageBar
                          start={series.animeStart}
                          end={series.animeEnd}
                          totalChapters={series.totalChapters ?? lastChapterNumber}
                          readChapter={highestReadChapter}
                          hideResumeHint={Boolean(animeResume)}
                      />
                    </>
                )}

                <Divider my="md" color="var(--hairline)" />

                <Stack gap="md">
                  {series.genres.length > 0 && (
                      <div>
                        <Title order={3} size="h4" fz="var(--type-body)" mb={10}>
                          <Trans>Genres</Trans>
                          {series.lockedFields.includes('genres') && <>{' '}<MetadataLock /></>}
                        </Title>
                        {/* Genres carry no relevance weight, so they are one flat row rather than
                            buckets, on their own dot colour to read apart from the tags below. */}
                        <div className="tag-chips" style={{ '--bucket': 'var(--info)' } as CSSProperties}>
                          {series.genres.map((g) => (
                              <span key={g} className="tag-chip">
                                <i className="tag-dot" />
                                <span>{g}</span>
                              </span>
                          ))}
                        </div>
                      </div>
                  )}
                  {(providerTags.length > 0 || series.metadataTags.length > 0) && (
                      <div>
                        <Divider my="md" color="var(--hairline)" />
                        <Title order={3} size="h4" fz="var(--type-body)" mb={10}>
                          <Trans>Tags</Trans>
                        </Title>
                        <div ref={tagListRef}>
                          {providerTags.length > 0 ? (
                              <TagBuckets tags={providerTags} />
                          ) : (
                              // No weights available (no MangaBaka id, or the local dump is not
                              // installed): show the stored names ungrouped rather than nothing.
                              <div className="tag-chips">
                                {series.metadataTags.map((t) => (
                                    <span key={t} className="tag-chip">
                                      <i className="tag-dot" />
                                      <span>{t}</span>
                                    </span>
                                ))}
                              </div>
                          )}
                        </div>
                      </div>
                  )}
                  <Divider mt="sm" color="var(--hairline)" />
                  <SeriesTagsEditor seriesId={series.id} tagIds={series.tagIds} />
                  {series.links.length > 0 && (
                      <div>
                        <Divider mb="sm" color="var(--hairline)" />
                        <Title order={3} size="h4" fz="var(--type-body)" mb={10}>
                          <Trans>Open on</Trans>
                        </Title>
                        <Group gap="xs" wrap="wrap">
                          <MetadataLinks links={series.links} />
                          {series.malId != null && <SeriesReviews malId={series.malId} />}
                        </Group>
                      </div>
                  )}
                </Stack>
              </Panel>

              <div className="series-split-row">
                <Panel className="series-detail-source-panel series-machine">
                  {series.numberingClash && (
                      <Alert
                          mb="md"
                          color="var(--warn)"
                          icon={<IconAlertTriangle size={18} />}
                          title={t`Sources disagree on chapter numbering`}
                      >
                        {(() => {
                          const [sub, whole] = series.numberingClash.split('|')
                          return (
                              <>
                                <Trans>
                                  <Text span fw={600}>
                                    {sub}
                                  </Text>{' '}
                                  lists sub-chapters (1.1, 1.2, …) where{' '}
                                  <Text span fw={600}>
                                    {whole}
                                  </Text>{' '}
                                  lists whole chapters for the same content, so both appear as separate rows below.
                                </Trans>{' '}
                                <Trans>There is no safe automatic merge.</Trans>{' '}
                                <Trans>Consider disabling one of the two source mappings; the warning clears on the next refresh.</Trans>
                              </>
                          )
                        })()}
                      </Alert>
                  )}
                  {canManageSources && (
                    <>
                      {/* The inner div is inert rather than every control inside getting a disabled
                          prop: the section is shared, and the outer one stays hoverable so the reason
                          can show. */}
                      <Tooltip label={archivedReason} withArrow disabled={!archived} events={TOOLTIP_EVENTS}>
                        <div style={archived ? { cursor: 'not-allowed' } : undefined}>
                          <div inert={archived} style={archived ? { opacity: 0.55 } : undefined}>
                            <SourceMappingsSection
                                seriesId={seriesId}
                                seriesTitle={series.title}
                                altTitles={series.altTitles}
                                matching={series.sourceMatchPending}
                            />
                          </div>
                        </div>
                      </Tooltip>
                      <Divider my="md" color="var(--hairline)" />
                    </>
                  )}
                  <ReleaseOutlookSummary seriesId={seriesId} divided />
                  <Text size="xs" c="var(--ink-3)">
                    {scanning ? (
                        <Group gap={6} component="span" wrap="nowrap">
                          <Loader size={10} />
                          <Trans>Scanning for upgrades…</Trans>
                        </Group>
                    ) : series.lastUpgradeScan ? (
                        (() => {
                          const at = formatDate(series.lastUpgradeScan.at)
                          const { probed, queued, checked } = series.lastUpgradeScan
                          return (
                              <>
                                {checked == null ? (
                                    <Trans>
                                      Last upgrade scan: {at}, <Plural value={probed} one="# probed" other="# probed" />
                                      ,{' '}
                                      <Plural value={queued} one="# queued" other="# queued" />
                                    </Trans>
                                ) : (
                                    <Trans>
                                      Last upgrade scan: {at}, <Plural value={checked} one="# chapter checked" other="# chapters checked" />
                                      , <Plural value={probed} one="# probed" other="# probed" />
                                      ,{' '}
                                      <Plural value={queued} one="# queued" other="# queued" />
                                    </Trans>
                                )}
                              </>
                          )
                        })()
                    ) : (
                        <Trans>Never scanned for upgrades</Trans>
                    )}
                  </Text>
                </Panel>
                <Panel className="series-detail-metadata-panel series-machine">
                  <Group justify="space-between" align="baseline" mb="sm" wrap="nowrap">
                    <Title order={2} size="h3" fz="var(--type-subhead)">
                      <Trans>Metadata</Trans>
                    </Title>
                    <Anchor component="button" type="button" size="xs" c="var(--ink-3)" onClick={() => setMetadataHistoryOpen(true)}>
                      <Trans>History</Trans>
                    </Anchor>
                  </Group>
                  <div className="series-records">
                    {series.originalTitle && (
                        <RecordRow label={t`Original title`}>{series.originalTitle}</RecordRow>
                    )}
                    {series.displayTitle !== series.title && (
                        // What the folder on disk and every file in it are named after. Shown
                        // whenever a title-language preference has moved the heading off it.
                        <RecordRow label={t`Library title`}>{series.title}</RecordRow>
                    )}
                    {altTitles.length > 0 && (
                        <RecordRow label={t`Alt titles`}>
                          {altTitles.map(altTitleLabel).join(', ')}
                        </RecordRow>
                    )}
                    {series.authorStory && (
                        <RecordRow label={t`Story`}>
                          <CreatorNames role="author" names={series.authorStory} />
                        </RecordRow>
                    )}
                    {series.authorArt && (
                        <RecordRow label={t`Art`}>
                          <CreatorNames role="artist" names={series.authorArt} />
                        </RecordRow>
                    )}
                    {series.publisher && (
                        <RecordRow label={t`Publisher`}>
                          <CreatorNames role="studio" names={series.publisher} />
                        </RecordRow>
                    )}
                    {series.type && <RecordRow label={t`Type`}>{series.type}</RecordRow>}
                    {series.year && <RecordRow label={t`Year`}>{series.year}</RecordRow>}
                    <RecordRow label={t`Status`}>
                      {renderLabel(status.label)}
                      {series.lockedFields.includes('status') && <>{' '}<MetadataLock /></>}
                    </RecordRow>
                    {contentRating && <RecordRow label={t`Content rating`}>{renderLabel(contentRating.label)}</RecordRow>}
                    {series.totalVolumes != null && (
                        <RecordRow label={t`Volumes`}>
                          {series.totalVolumes}
                          {series.lockedFields.includes('totalVolumes') && <>{' '}<MetadataLock /></>}
                        </RecordRow>
                    )}
                    <RecordRow label={t`Chapters known`}>{series.knownChapterCount}</RecordRow>
                    {series.readTimeEstimate && (
                        <RecordRow label={t`Time to finish`}>
                          <ReadTimeEstimateText estimate={series.readTimeEstimate} />
                        </RecordRow>
                    )}
                    {series.rootFolderPath && (
                        <RecordRow label={t`Folder`}>
                          <MonoValue value={series.rootFolderPath} copy c="var(--ink-3)" />
                        </RecordRow>
                    )}
                  </div>
                </Panel>
              </div>
            </div>

            <SeriesScrobbleSection seriesId={seriesId} />
            <RelatedSeriesSection seriesId={seriesId} />
            <SimilarSeriesSection seriesId={seriesId} />
          </Stack>
        </Tabs.Panel>

        <Modal
            opened={downloadAllConfirmOpen}
            onClose={() => setDownloadAllConfirmOpen(false)}
            title={plural(missingWanted, {
              one: 'Download # wanted chapter?',
              other: 'Download # wanted chapters?',
            })}
            centered
        >
          <Stack gap="sm">
            <Text mt="sm" size="sm">
              {plural(missingWanted, {
                one: 'This will add # chapter to the download queue.',
                other: 'This will add # chapters to the download queue.',
              })}
            </Text>
            <Group justify="flex-end">
              <Button variant="default" onClick={() => setDownloadAllConfirmOpen(false)}>
                <Trans>Cancel</Trans>
              </Button>
              <Button
                  loading={searchMissing.isPending}
                  onClick={() => {
                    setDownloadAllConfirmOpen(false)
                    queueAllWanted()
                  }}
              >
                {plural(missingWanted, { one: 'Download # chapter', other: 'Download # chapters' })}
              </Button>
            </Group>
          </Stack>
        </Modal>

        <Modal
            opened={nextCountOpen}
            onClose={() => setNextCountOpen(false)}
            title={t`Download next chapters`}
            centered
        >
          <Stack
              component="form"
              gap="sm"
              onSubmit={(e: FormEvent) => {
                e.preventDefault()
                queueNext(Math.max(1, Number(nextCount) || 1))
              }}
          >
            <NumberInput
                label={t`How many`}
                description={
                  <Plural value={missingWanted} one="# wanted chapter is missing" other="# wanted chapters are missing" />
                }
                min={1}
                value={nextCount}
                onChange={setNextCount}
                data-autofocus
            />
            <Group justify="flex-end">
              <Button variant="default" onClick={() => setNextCountOpen(false)}>
                <Trans>Cancel</Trans>
              </Button>
              <Button type="submit" loading={downloadNext.isPending}>
                <Trans>Download</Trans>
              </Button>
            </Group>
          </Stack>
        </Modal>

        <ReleaseSearchModal
            seriesId={seriesId}
            opened={releaseModalOpen}
            onClose={() => setReleaseModalOpen(false)}
        />

        <RenameSeriesModal
            seriesId={seriesId}
            opened={renameModalOpen}
            onClose={() => setRenameModalOpen(false)}
        />

        <MetadataHistoryModal
            seriesId={seriesId}
            opened={metadataHistoryOpen}
            onClose={() => setMetadataHistoryOpen(false)}
        />

        {canEditMetadata && (
            <EditMetadataModal
                series={series}
                opened={editMetadataOpen}
                onClose={() => setEditMetadataOpen(false)}
            />
        )}

        <Modal opened={moveModalOpen} onClose={() => setMoveModalOpen(false)} title={t`Move series`} centered>
          <Stack gap="md">
            <Text size="sm" c="var(--ink-3)">
              <Trans>Re-triggers a Kavita scan of both locations either way.</Trans>{' '}
              <Trans>Blocked while a download for this series is in flight, unless Maki isn't touching the files
                itself.</Trans>
            </Text>
            <Select
                label={t`Destination root folder`}
                placeholder={t`Pick a root folder`}
                data={(rootFolders ?? [])
                    .filter((f) => f.id !== series.rootFolderId)
                    .map((f) => ({ value: String(f.id), label: f.path }))}
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
                  loading={moveSeries.isPending}
                  disabled={!moveTarget}
                  onClick={() =>
                      moveTarget &&
                      moveSeries.mutate(
                          { seriesId, rootFolderId: Number(moveTarget), moveFiles },
                          {
                            onSuccess: () => {
                              notify.ok(staticT`Series moved`)
                              setMoveModalOpen(false)
                            },
                          },
                      )
                  }
              >
                <Trans>Move</Trans>
              </Button>
            </Group>
          </Stack>
        </Modal>


        <Tabs.Panel value="chapters">
          <Stack className="series-detail-chapters" gap="lg">
            <div className="series-detail-chapter-head" data-sticky={selectMode ? undefined : true}>
              <Group
                  className="series-detail-chapter-toolbar"
                  justify="space-between"
                  wrap="wrap"
                  gap="sm"
              >
                <Group gap="xs" align="baseline">
                  <Title order={2} size="h3"><Trans>Chapters</Trans></Title>
                  {chapters && (
                      <Text size="sm" c="var(--ink-3)" className="tnum">
                        {progress.have}/{progress.total}
                      </Text>
                  )}
                  {chapters && readTracking && progress.have > 0 && (
                      <Badge size="sm" variant="light" color="var(--ok)" className="tnum">
                        <Trans>{readFilterCount} read</Trans>
                      </Badge>
                  )}
                </Group>
                {chapters && chapters.length > 0 && (
                    <Group gap="xs" wrap="wrap">
                      {isMobile ? (
                          <Select
                              size="xs"
                              aria-label={t`Filter chapters`}
                              data={chapterFilterData}
                              value={chapterFilter}
                              onChange={(value) => value && setChapterFilter(value)}
                              allowDeselect={false}
                              w={150}
                          />
                      ) : (
                          <SegmentedControl
                              aria-label={t`Filter chapters`}
                              size="xs"
                              value={chapterFilter}
                              onChange={setChapterFilter}
                              data={chapterFilterData}
                          />
                      )}
                      <Tooltip label={chapterSort === 'asc' ? t`Oldest first` : t`Newest first`} withArrow>
                        <ActionIcon
                            size="input-xs"
                            variant="default"
                            aria-label={chapterSort === 'asc' ? t`Sort newest first` : t`Sort oldest first`}
                            onClick={toggleChapterSort}
                        >
                          {chapterSort === 'asc'
                              ? <IconSortAscendingNumbers size={16} />
                              : <IconSortDescendingNumbers size={16} />}
                        </ActionIcon>
                      </Tooltip>
                      <Select
                          size="xs"
                          aria-label={t`Chapters per page`}
                          data={chapterPageSizeOptions}
                          value={chapterPageSizePreference}
                          onChange={setChapterPageSize}
                          allowDeselect={false}
                          w={isMobile ? 100 : 108}
                      />
                      {!selectMode && (
                          <Button
                              size="xs"
                              variant="default"
                              leftSection={<IconListCheck size={14} />}
                              onClick={() => setSelectMode(true)}
                          >
                            <Trans>Select</Trans>
                          </Button>
                      )}
                    </Group>
                )}
              </Group>

              {!isMobile && chapterSearchInput}
            </div>
            {isMobile && chapterSearchInput}

            {/* Two closed issues came from people who had a file on disk and could not find how to
                tell Maki which chapters it holds. Point at it from the tab they were looking at. */}
            {!selectMode && canEditMetadata && unlinkedFilesOnDisk > 0 && (
                <Paper className="series-detail-chapter-hint" withBorder p="xs" radius="lg">
                  <Group justify="space-between" wrap="wrap" gap="xs">
                    <Group gap="xs" wrap="nowrap" align="flex-start" style={{ flex: 1, minWidth: 240 }}>
                      <IconLink size={16} style={{ flexShrink: 0, marginTop: 2, color: 'var(--warn)' }} />
                      <Text size="sm" c="var(--ink-2)">
                        <Plural
                            value={unlinkedFilesOnDisk}
                            one="# file in the series folder isn't linked to any chapter."
                            other="# files in the series folder aren't linked to any chapter."
                        />{' '}
                        <Trans>Relink reads each volume file to find the chapters inside it. Anything it can't place you can link by hand.</Trans>
                      </Text>
                    </Group>
                    <Group gap="xs">
                      <Button size="xs" variant="light" leftSection={<IconWand size={14} />} onClick={() => setRelinkOpen(true)}>
                        <Trans>Relink files</Trans>
                      </Button>
                      <Button size="xs" variant="subtle" leftSection={<IconLink size={14} />} onClick={() => changeTab('files')}>
                        <Trans>Link by hand</Trans>
                      </Button>
                    </Group>
                  </Group>
                </Paper>
            )}

            {!selectMode && canDownload && pendingProposal && (
                <TorrentProposalCard proposal={pendingProposal} banner />
            )}

            {selectMode && (
                <Paper className="series-detail-chapter-selection" withBorder p="xs" radius="lg">
                  <Group justify="space-between" wrap="wrap" gap="xs">
                    <Group gap="xs">
                      <ActionIcon hiddenFrom="sm" variant="default" onClick={exitSelectMode} aria-label={t`Done`}>
                        <IconX size={15} />
                      </ActionIcon>
                      <Text size="sm" c="var(--ink-3)" className="tnum">
                        <Trans>{selectedCount} selected</Trans>
                      </Text>
                      <Menu shadow="md" position="bottom-start" withinPortal>
                        <Menu.Target>
                          <Button size="xs" variant="subtle" rightSection={<IconChevronDown size={14} />}>
                            <Trans>Select all</Trans>
                          </Button>
                        </Menu.Target>
                        <Menu.Dropdown>
                          {/* Every item works over the rows the filter is showing, same as a shift-range,
                        so "Specials" under the Missing filter means the specials you can see. */}
                          <Menu.Item
                              className="tnum"
                              onClick={() => selectAll(() => true)}
                          >
                            <Trans>All ({visibleAllCount})</Trans>
                          </Menu.Item>
                          <Menu.Item
                              className="tnum"
                              disabled={visibleMain.length === 0}
                              onClick={() => selectAll((c) => !isSpecial(c))}
                          >
                            <Trans>Main ({visibleMainCount})</Trans>
                          </Menu.Item>
                          <Menu.Item
                              className="tnum"
                              disabled={visibleSpecials.length === 0}
                              onClick={() => selectAll(isSpecial)}
                          >
                            <Trans>Specials ({visibleSpecialsCount})</Trans>
                          </Menu.Item>
                          <Menu.Divider />
                          <Menu.Item
                              disabled={selected.size === 0}
                              onClick={() => {
                                selectAnchor.current = null
                                setSelected(new Set())
                              }}
                          >
                            <Trans>Clear</Trans>
                          </Menu.Item>
                        </Menu.Dropdown>
                      </Menu>
                      <Text size="xs" c="var(--ink-3)" visibleFrom="sm">
                        <Trans>Click a row to select, shift-click for a range</Trans>
                      </Text>
                    </Group>
                    <Group gap="xs">
                      {chapterPrimaryActions.map((a) => {
                        const button = (
                          <Button
                              key={a.key}
                              visibleFrom="sm"
                              size="xs"
                              variant="light"
                              leftSection={a.icon}
                              disabled={selected.size === 0}
                              data-disabled={a.disabled || undefined}
                              aria-disabled={a.disabled || undefined}
                              loading={a.loading}
                              onClick={a.run}
                          >
                            {a.label}
                          </Button>
                        )
                        return a.key === 'download' ? (
                            <Tooltip key={a.key} label={archivedReason} withArrow disabled={!archived} events={TOOLTIP_EVENTS}>
                              {button}
                            </Tooltip>
                        ) : (
                            button
                        )
                      })}
                      {(chapterPrimaryActions.length > 0 || chapterMoreGroups.length > 0) && (
                          <Menu position="bottom-end" withinPortal>
                            <Menu.Target>
                              <Button
                                  size="xs"
                                  variant="default"
                                  rightSection={<IconChevronDown size={14} />}
                                  disabled={selected.size === 0}
                                  loading={chapterMoreBusy}
                                  className={chapterMoreGroups.length === 0 ? 'library-bulk-more-phone' : undefined}
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
                              {chapterPrimaryActions.map((a) => (
                                  <Menu.Item key={a.key} hiddenFrom="sm" leftSection={a.icon} disabled={a.disabled} onClick={a.run}>
                                    {a.label}
                                  </Menu.Item>
                              ))}
                              {chapterMoreGroups.map((group, i) => (
                                  <Fragment key={group[0].key}>
                                    {(i > 0 || chapterPrimaryActions.length > 0) && (
                                        <Menu.Divider hiddenFrom={i === 0 ? 'sm' : undefined} />
                                    )}
                                    {group.map((a) => (
                                        <Menu.Item key={a.key} leftSection={a.icon} onClick={a.run}>
                                          {a.label}
                                        </Menu.Item>
                                    ))}
                                  </Fragment>
                              ))}
                            </Menu.Dropdown>
                          </Menu>
                      )}
                      {canDelete && (
                          <>
                            <Divider orientation="vertical" className="library-bulk-divider" />
                            <Menu position="bottom-end" withinPortal>
                              <Menu.Target>
                                <Button
                                    size="xs"
                                    variant="light"
                                    color="var(--danger)"
                                    leftSection={<IconTrash size={15} />}
                                    rightSection={<IconChevronDown size={14} />}
                                    disabled={selected.size === 0}
                                >
                                  <Trans>Delete</Trans>
                                </Button>
                              </Menu.Target>
                              <Menu.Dropdown>
                                <Menu.Item leftSection={<IconTrash size={15} />} onClick={() => setDeleteFileIds([...selected])}>
                                  <Trans>Delete files</Trans>
                                </Menu.Item>
                                <Menu.Item leftSection={<IconRowRemove size={15} />} onClick={() => setDeleteChaptersModalOpen(true)}>
                                  <Trans>Remove chapters</Trans>
                                </Menu.Item>
                              </Menu.Dropdown>
                            </Menu>
                          </>
                      )}
                      <Button
                          visibleFrom="sm"
                          size="xs"
                          variant="default"
                          leftSection={<IconX size={15} />}
                          onClick={exitSelectMode}
                      >
                        <Trans>Done</Trans>
                      </Button>
                    </Group>
                  </Group>
                </Paper>
            )}

            <Modal
                opened={deleteChaptersModalOpen}
                onClose={closeRemoveChapters}
                title={t`Remove chapters?`}
                centered
            >
              <Stack gap="md">
                <Text size="sm" c="var(--ink-3)">
                  <Plural
                    value={selectedCount}
                    one="This permanently removes # chapter row, not just its file link, along with everyone's read history for it."
                    other="This permanently removes # chapter rows, not just their file link, along with everyone's read history for them."
                  />{' '}
                  <Plural
                    value={binDays}
                    one="Backing files go to the recycle bin for # day."
                    other="Backing files go to the recycle bin for # days."
                  />{' '}
                  <Trans>Use this to clean up chapters pulled in by a wrong source match.</Trans>{' '}
                  <Trans>Fix or remove the source mapping first, or a refresh will bring them right back.</Trans>
                </Text>
                <Text size="sm" c="var(--danger)">
                  <Trans>Removed chapter rows and their read history cannot be restored.</Trans>
                </Text>
                <Group justify="flex-end">
                  <Button variant="default" onClick={closeRemoveChapters}>
                    <Trans>Cancel</Trans>
                  </Button>
                  <Button
                      color="var(--danger-fill)"
                      leftSection={<IconTrash size={16} />}
                      loading={deleteChapters.isPending}
                      disabled={binRefused === 'chapters'}
                      onClick={() => removeChapters()}
                  >
                    <Trans>Remove</Trans>
                  </Button>
                </Group>
                {binRefused === 'chapters' && (
                    <PermanentDeleteFallback loading={deleteChapters.isPending} onConfirm={() => removeChapters(true)} />
                )}
              </Stack>
            </Modal>

            <Modal
                opened={deleteFileIds !== null}
                onClose={closeDeleteFiles}
                title={t`Delete files?`}
                centered
            >
              <Stack gap="md">
                {deleteFilesPlan && deleteFilesPlan.files === 0 ? (
                    <Text size="sm" c="var(--ink-3)">
                      <Trans>None of these chapters has a file on disk.</Trans>
                    </Text>
                ) : (
                    <Text size="sm" c="var(--ink-3)">
                      <Plural
                          value={deleteFilesPlan?.files ?? 0}
                          one="Moves # file to the recycle bin."
                          other="Moves # files to the recycle bin."
                      />{' '}
                      <Plural
                          value={binDays}
                          one="It can be restored from Settings for # day."
                          other="It can be restored from Settings for # days."
                      />{' '}
                      <Trans>The chapters stay in the list with their read history, and Maki won't download
                        them again unless you ask.</Trans>
                    </Text>
                )}
                {deleteFilesPlan && deleteFilesPlan.others > 0 && (
                    <Text size="sm" c="var(--warn)">
                      <Plural
                          value={deleteFilesPlan.others}
                          one="# more chapter is in the same volume file and loses it too."
                          other="# more chapters are in the same volume files and lose them too."
                      />
                    </Text>
                )}
                <Group justify="flex-end">
                  <Button variant="default" onClick={closeDeleteFiles}>
                    <Trans>Cancel</Trans>
                  </Button>
                  <Button
                      color="var(--danger-fill)"
                      leftSection={<IconTrash size={16} />}
                      disabled={!deleteFilesPlan || deleteFilesPlan.files === 0 || binRefused === 'files'}
                      loading={deleteChapterFiles.isPending}
                      onClick={() => deleteFilesOf()}
                  >
                    <Trans>Delete files</Trans>
                  </Button>
                </Group>
                {binRefused === 'files' && (
                    <PermanentDeleteFallback loading={deleteChapterFiles.isPending} onConfirm={() => deleteFilesOf(true)} />
                )}
              </Stack>
            </Modal>

            <RelinkFilesModal seriesId={seriesId} opened={relinkOpen} onClose={() => setRelinkOpen(false)} />

            <LinkChaptersModal
                seriesId={seriesId}
                chapterIds={linkChapterIds ?? []}
                opened={linkChapterIds !== null}
                onClose={() => {
                  setLinkChapterIds(null)
                  if (selectMode) exitSelectMode()
                }}
            />

            {(chapters === undefined && chaptersFailed) || chapters?.length === 0 ? (
                <Text c="var(--ink-3)" size="sm">
                  <Trans>No chapters known.</Trans> <Trans>Link a source and refresh.</Trans>
                </Text>
            ) : chapters !== undefined && renderedRows.rows.length === 0 ? (
                <Text c="var(--ink-3)" size="sm">
                  <Trans>No chapters match this search and filter.</Trans>
                </Text>
            ) : (
                <Stack gap="sm">
                  <Paper
                      className={`series-detail-chapter-table chapter-rows-${chapterLayout}`}
                      withBorder
                      radius="lg"
                      style={{ overflow: 'hidden' }}
                  >
                    <Box
                        pos="relative"
                        ref={setChapterTable}
                        style={{ '--chapter-marker-slot': `${markerSlot}px` } as React.CSSProperties}
                    >
                      <Table.ScrollContainer minWidth={chapterRowsAsList ? 0 : chapterTableMinWidth}>
                        <Table
                            className="chapter-table"
                            highlightOnHover
                            verticalSpacing="xs"
                            aria-busy={chapterRowsLoading || undefined}
                        >
                          <Table.Thead>
                            <Table.Tr>
                              <Table.Th w={CHAPTER_WANTED_COLUMN_WIDTH}><Trans>Wanted</Trans></Table.Th>
                              <Table.Th w={chapterColumnWidths.chapter}>
                                <Trans>Chapter</Trans>
                                {!chapterRowsAsList && (
                                    <div {...chapterColumnHandle('chapter')} aria-label={t`Resize Chapter column`} />
                                )}
                              </Table.Th>
                              <Table.Th>
                                {/* Less the cell's padding. The table's min-width alone doesn't stop the
                                    browser squeezing Title when the other columns grow. */}
                                <Box miw={CHAPTER_TITLE_MIN_WIDTH - 32}><Trans>Title</Trans></Box>
                              </Table.Th>
                              <Table.Th w={chapterColumnWidths.released} className="chapter-released">
                                <Trans>Released</Trans>
                                {!chapterRowsAsList && (
                                    <div {...chapterColumnHandle('released', 'start')} aria-label={t`Resize Released column`} />
                                )}
                              </Table.Th>
                              <Table.Th w={chapterColumnWidths.source}>
                                <Trans>Source</Trans>
                                {!chapterRowsAsList && (
                                    <div {...chapterColumnHandle('source', 'start')} aria-label={t`Resize Source column`} />
                                )}
                              </Table.Th>
                              <Table.Th w={chapterColumnWidths.status}>
                                <Trans>Status</Trans>
                                {!chapterRowsAsList && (
                                    <div {...chapterColumnHandle('status', 'start')} aria-label={t`Resize Status column`} />
                                )}
                              </Table.Th>
                              <Table.Th w={CHAPTER_ACTIONS_COLUMN_WIDTH}><VisuallyHidden><Trans>Actions</Trans></VisuallyHidden></Table.Th>
                            </Table.Tr>
                          </Table.Thead>
                          <Table.Tbody>
                            {chapterRowsLoading ? (
                                <ChapterRowSkeletons count={pagedRows.length > 0 ? Math.min(pagedRows.length, 12) : 8} />
                            ) : (
                                <ChunkedRows rows={shownRows}>
                                  {(rows) => rows.map((row) => {
                                  if (row.kind === 'span') {
                                    return (
                                        <ChapterSpanRow
                                            key={`span:${row.span.key}`}
                                            span={row.span}
                                            rows={row.rows}
                                            readProgress={readProgress}
                                            hasAnimeMarkers={hasAnimeMarkers}
                                            readTracking={readTracking}
                                            canEditMetadata={canEditMetadata}
                                            wantedPending={setChaptersWanted.isPending}
                                            actions={spanRowActions}
                                        />
                                    )
                                  }
                                  const c = row.chapter
                                  const markers = c.number === null ? undefined : animeMarkers.get(c.number)
                                  return (
                                      <ChapterRow
                                          key={c.id}
                                          chapter={c}
                                          progress={readProgress.get(c.id)}
                                          queueItem={queueByChapterId.get(c.id)}
                                          selectMode={selectMode}
                                          selected={selectMode && selected.has(c.id)}
                                          hasAnimeMarkers={hasAnimeMarkers}
                                          markers={markers}
                                          foldedSpans={markers ? foldedSpans : undefined}
                                          spanForMarker={spanForMarker}
                                          canEditMetadata={canEditMetadata}
                                          canDownload={canDownload}
                                          canDelete={canDelete}
                                          canFindBetterCopy={enabledMappings > 1}
                                          sourceToolsDisabledReason={archivedReason}
                                          canSaveOffline={canSaveOffline}
                                          offline={offlineRecords.get(c.id)}
                                          offlineSaving={offlineProgress.get(c.id)}
                                          wantedPending={toggleWanted.isPending && toggleWanted.variables?.chapterId === c.id}
                                          readPending={setRead.isPending && setRead.variables?.chapterId === c.id}
                                          upgradePending={upgradeChapterNow.isPending}
                                          upgradingThis={upgradeChapterNow.isPending && upgradeChapterNow.variables === c.id}
                                          fileOrigin={fileOrigin}
                                          renderLabel={renderLabel}
                                          actions={chapterRowActions}
                                      />
                                  )
                                })}
                                </ChunkedRows>
                            )}
                          </Table.Tbody>
                        </Table>
                      </Table.ScrollContainer>

                      {/* Drawn over the table, never inside it: the layer ignores pointer events so rows stay
              clickable through it, and only the lines themselves take clicks back. */}
                      <div className="chapter-span-overlay" aria-hidden={spanLines.length === 0}>
                        {spanLines.map((line) => {
                          const { label: lineLabel } = line
                          return (
                            <button
                                key={line.key}
                                type="button"
                                className="chapter-span-line"
                                style={{ top: line.top, height: line.height, left: line.left }}
                                data-open-start={line.openStart ? '' : undefined}
                                data-open-ended={line.openEnded ? '' : undefined}
                                title={t`${lineLabel}. Click to collapse.`}
                                aria-label={t`Collapse ${lineLabel}`}
                                onClick={() => toggleSpanFold(line.key)}
                            />
                          )
                        })}
                      </div>
                    </Box>
                  </Paper>
                  {chapterPageCount > 1 && (
                      <Group justify="space-between" gap="xs" wrap="wrap">
                        <Text size="xs" c="var(--ink-3)" className="tnum">
                          <Trans>Chapters {currentPageLabel} of {visibleAllCount} matching</Trans>
                        </Text>
                        <Pagination
                            size="sm"
                            value={currentChapterPage}
                            total={chapterPageCount}
                            siblings={isMobile ? 0 : 1}
                            boundaries={1}
                            getItemProps={(page) => {
                              const range = chapterPageLabels[page - 1]
                              return { children: range, 'aria-label': t`Chapters ${range}` }
                            }}
                            onChange={(page) => {
                              setChapterPage(page)
                              selectAnchor.current = null
                              chapterTable?.scrollIntoView({ behavior: scrollBehavior(), block: 'start' })
                            }}
                        />
                      </Group>
                  )}
                </Stack>
            )}
          </Stack>
        </Tabs.Panel>

        <Tabs.Panel value="files">
          <div className="series-detail-files">
            <SeriesFilesSection seriesId={seriesId} />
          </div>
        </Tabs.Panel>

        {/* This action lives in the hero, so its dialog must not be deactivated with any tab panel. */}
        <ArchiveSeriesDialog
          opened={archiveModalOpen}
          onClose={() => {
            setArchiveModalOpen(false)
            setBinRefused(null)
          }}
          title={seriesTitle}
          seriesId={series.id}
          removeFiles={archiveRemoveFiles}
          onRemoveFilesChange={setArchiveRemoveFiles}
          canRemoveFiles={canDelete}
          loading={archiveSeries.isPending}
          onConfirm={() => archiveCurrent()}
          onConfirmPermanent={binRefused === 'archive' ? () => archiveCurrent(true) : undefined}
        />

        <RemoveArchivedFilesDialog
          opened={removeFilesModalOpen}
          onClose={() => {
            setRemoveFilesModalOpen(false)
            setBinRefused(null)
          }}
          title={t`Remove the files of ${seriesTitle}?`}
          loading={removeArchivedFiles.isPending}
          onConfirm={() => removeArchivedSeriesFiles()}
          onConfirmPermanent={binRefused === 'archiveFiles' ? () => removeArchivedSeriesFiles(true) : undefined}
        />

        <RestoreSeriesDialog
          opened={restoreModalOpen}
          onClose={() => setRestoreModalOpen(false)}
          seriesId={series.id}
          canDownload={canDownload}
          loading={restoreSeries.isPending}
          onRestore={restoreArchived}
        />

        <RemoveSeriesDialog
          opened={deleteSeriesModalOpen}
          onClose={() => {
            setDeleteSeriesModalOpen(false)
            setBinRefused(null)
          }}
          title={<Trans>Remove series?</Trans>}
          deleteFiles={deleteSeriesFiles}
          onDeleteFilesChange={setDeleteSeriesFiles}
          loading={deleteSeries.isPending}
          onConfirm={() => removeSeries()}
          onConfirmPermanent={binRefused === 'series' ? () => removeSeries(true) : undefined}
        >
          <Trans>This removes "{seriesTitle}" and its chapters from Maki.</Trans>
        </RemoveSeriesDialog>

        {conversionHistory && (
          <UndoConversionModal
            opened={undoConversionOpen}
            onClose={() => setUndoConversionOpen(false)}
            seriesId={seriesId}
            history={conversionHistory}
          />
        )}

        <Modal
            opened={requestModalOpen}
            onClose={() => setRequestModalOpen(false)}
            title={t`Request chapters of ${seriesTitle}`}
        >
          <RequestForm
              chapterStart={requestStart}
              chapterEnd={requestEnd}
              note={requestNote}
              onChapterStart={setRequestStart}
              onChapterEnd={setRequestEnd}
              onNote={setRequestNote}
              pending={createRequest.isPending}
              label={t`Send request`}
              onSubmit={() =>
                  createRequest.mutate(
                      {
                        kind: 'Chapters',
                        seriesId,
                        chapterStart: requestStart === '' ? null : requestStart,
                        chapterEnd: requestEnd === '' ? null : requestEnd,
                        note: requestNote.trim() || null,
                      },
                      {
                        onSuccess: () => {
                          setRequestModalOpen(false)
                          notify.ok(staticT`Requested, an admin will see it on the Requests page`)
                        },
                      },
                  )
              }
          />
        </Modal>

        {/* Keyed on the chapter so each open starts a fresh comparison rather than reusing the
            previous chapter's panels. */}
        {pickChapter && (
            <SourceCompareModal
                key={pickChapter.id}
                seriesId={seriesId}
                mode="pick"
                chapter={pickChapter}
                opened
                onClose={() => setPickChapter(null)}
            />
        )}

        <UpgradeNowResultModal
            opened={upgradeNowModalOpen}
            result={upgradeNowResult}
            onClose={() => setUpgradeNowModalOpen(false)}
        />
      </Tabs>
      {luckyPill}
    </SurfaceFrame>
  )
}

/**
 * The reading-time estimate's two variable clauses (pace, and whether the sample is series-specific)
 * spelled out per combination, so each stays one full sentence rather than a translated fragment
 * stitched onto untranslated ones.
 */
function ReadTimeEstimateText({ estimate }: { estimate: ReadTimeEstimate }) {
  const { seconds, style, remainingChapters, sampleChapters, seriesSpecific } = estimate
  const readingTime = formatReadingTime(seconds)
  const chaptersLeft = plural(remainingChapters, { one: '# chapter left', other: '# chapters left' })
  const basis = seriesSpecific
    ? plural(sampleChapters, {
        one: 'based on # chapter from this series',
        other: 'based on # chapters from this series',
      })
    : plural(sampleChapters, { one: 'based on # similar read', other: 'based on # similar reads' })
  return (
      <Stack gap={1}>
        <Text size="sm" fw={650} className="tnum">
          <Trans>About {readingTime}</Trans>
        </Text>
        <Text size="xs" c="var(--ink-3)">
          {style === 'scrolling' ? (
              <Trans>{chaptersLeft} at your scrolling pace, {basis}.</Trans>
          ) : (
              <Trans>{chaptersLeft} at your paged pace, {basis}.</Trans>
          )}
        </Text>
      </Stack>
  )
}

/** A comma-separated credit, each name linking to that creator's page. */
function CreatorNames({
                        role,
                        names,
                      }: {
  role: 'author' | 'artist' | 'studio'
  names: string
}) {
  const values = names.split(',').map((n) => n.trim()).filter(Boolean)
  return (
      <Text size="sm" c="var(--ink-2)">
        {values.map((value, i) => (
            <span key={`${i}:${value}`}>
          {i > 0 && ', '}
              <Anchor component={Link} to={`/creator/${encodeURIComponent(value)}?role=${role}`} inherit>
            {value}
          </Anchor>
        </span>
        ))}
      </Text>
  )
}

/** One labelled line in the Metadata panel. Separated by a hairline, not boxed. */
function RecordRow({ label, children }: { label: string; children: ReactNode }) {
  return (
      <div className="series-record">
        <Text size="xs" c="var(--ink-4)">
          {label}
        </Text>
        <Text size="sm" c="var(--ink-2)" component="div">
          {children}
        </Text>
      </div>
  )
}
