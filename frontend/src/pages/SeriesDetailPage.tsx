import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import type { CSSProperties, ReactNode } from 'react'
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
  Checkbox,
  Divider,
  Group,
  Menu,
  NumberInput,
  Modal,
  Pagination,
  Paper,
  Progress,
  Radio,
  SegmentedControl,
  Select,
  Stack,
  Switch,
  Table,
  Tabs,
  Text,
  TextInput,
  Title,
  Tooltip,
} from '@mantine/core'
import {
  IconAlertTriangle,
  IconBook,
  IconChevronDown,
  IconCircleCheck,
  IconDownload,
  IconEye,
  IconEyeCheck,
  IconLink,
  IconLinkOff,
  IconListCheck,
  IconMinus,
  IconSearch,
  IconSend,
  IconTrash,
  IconX,
  IconDeviceTv,
  IconDotsVertical,
  IconPhotoSearch,
  IconEyeOff,
} from '@tabler/icons-react'
import { useMediaQuery } from '@mantine/hooks'
import { notifications } from '@mantine/notifications'
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import {
  useChapters,
  useSourceMappings,
  useSources,
  useDeleteSeries,
  useMoveSeries,
  useRefreshMetadata,
  useRefreshSeries,
  useRescanSeries,
  useRootFolders,
  useSearchChapter,
  useDownloadChapters,
  useDownloadNext,
  useSearchMissing,
  useSeriesDetail,
  useSetChaptersWanted,
  useSetIncognito,
  useSetSeriesNotificationMode,
  useSetMonitorMode,
  useSetRating,
  useToggleChapterWanted,
  useUnlinkChapters,
  useDeleteChapters,
  useQueue, useSeriesFiles,
  useRecommendationDetail,
} from '../api/hooks'
import {
  useContinueReading,
  useReadTracking,
  useSeriesReadProgress,
  useSetChapterRead,
  useSetChaptersState,
  type ChapterProgressDto,
  type ChapterReadState,
} from '../api/reader'
import { useCreateSeriesRequest } from '../api/requests'
import { altTitleLabel, readableTitles } from '../api/titles'
import type { ChapterDto } from '../api/types'
import { useAuth } from '../auth/AuthProvider'
import { queueErrorMessage } from '../api/queue'
import { useLabel } from '../i18n-context'
import { usePageLabel } from '../lib/navHistory'
import { AnimeCoverageBar } from '../components/AnimeCoverageBar'
import { LinkChaptersModal } from '../components/LinkChaptersModal'
import { MetadataLinks } from '../components/MetadataLinks'
import { RelatedSeriesSection } from '../components/RelatedSeriesSection'
import { TagBuckets } from '../components/TagBuckets'
import { SimilarSeriesSection } from '../components/SimilarSeriesSection'
import { ReleaseSearchModal } from '../components/ReleaseSearchModal'
import { RenameSeriesModal } from '../components/RenameSeriesModal'
import { RequestForm } from '../components/RequestForm'
import { SeriesActionsMenu } from '../components/series/SeriesActionsMenu'
import { SeriesHero, SeriesHeroSkeleton } from '../components/series/SeriesHero'
import { SeriesFilesSection } from '../components/SeriesFilesSection'
import { SeriesTagsEditor } from '../components/SeriesTagsEditor'
import { SeriesScrobbleSection } from '../components/SeriesScrobbleSection'
import { SourceMappingsSection } from '../components/SourceMappingsSection'
import { SourceCompareModal } from '../components/SourceCompareModal'
import type { PickChapter } from '../components/SourceCompareModal'
import { formatDate, formatReadingTime } from '../format'
import {
  contentRatingVisual,
  queueStatusVisual,
  seriesProgressVisual,
  seriesStatusVisual,
} from '../components/ui/status'
import { readStored, writeStored } from '../components/ui/viewPrefs'
import { SurfaceFrame } from '../components/ui/SurfaceFrame'
import { EmptyState } from '../components/ui/EmptyState'
import { useShellTitle } from '../lib/shellTitle'
import { buildAnimeSpans, mergeAnimeMarkers, type AnimeSpan } from '../lib/animeCoverage'

function chapterLabel(c: ChapterDto): string {
  if (c.isOneShot || c.number === null) return c.title ?? staticT`One-shot`
  // Prefer the volume the backing file actually is; fall back to metadata volume.
  const volNum = c.fileVolume ?? (c.volume !== null ? String(c.volume) : null)
  const chapterNumber = c.number
  return volNum !== null ? staticT`Vol.${volNum} Ch.${chapterNumber}` : staticT`Ch.${chapterNumber}`
}

/** A special is a decimal-numbered chapter (10.5 omake etc.). */
const isSpecial = (c: ChapterDto) => c.number !== null && c.number % 1 !== 0

const TABS = ['details', 'chapters', 'files'] as const
type Tab = (typeof TABS)[number]

const CHAPTER_PAGE_SIZE_STORAGE_KEY = 'series-chapter-page-size'
const CHAPTER_PAGE_SIZES = ['10', '25', '50', '75', '100', 'all'] as const
type ChapterPageSize = (typeof CHAPTER_PAGE_SIZES)[number]

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

type ReadTimeEstimate = {
  seconds: number
  remainingChapters: number
  style: 'scrolling' | 'paged'
  sampleChapters: number
  seriesSpecific: boolean
}

/** "Ch.1–270", or "Ch.315+" for a season with no end marker yet. */
const spanRangeLabel = (span: AnimeSpan) => {
  const { from, to, openEnded } = span
  return openEnded ? staticT`Ch.${from}+` : staticT`Ch.${from}–${to}`
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
  missing: (c) => !c.hasFile,
  downloaded: (c) => c.hasFile,
  specials: isSpecial,
  // A one-shot has no number and counts as main, matching NewChapterMonitorMode.MainOnly.
  main: (c) => !isSpecial(c),
}

interface ReadState {
  read: boolean
  /** A resume position exists and the chapter isn't finished. */
  inProgress: boolean
  /** Read according to Kavita rather than read here, so no page position is known. */
  external: boolean
  /** Ticked off without being read. Counts as read everywhere, but never reached the stats log. */
  watched: boolean
}

/**
 * Read state of one chapter row, straight off its `ChapterProgress` row, the only source of truth
 * for read state. Nothing is inferred from the series' high-water mark: that mark is forward-only
 * and covers every chapter numbered below it, so one stale Kavita read made a whole run of never
 * opened chapters look read, with no way to correct it.
 *
 * `completed` is the sticky read flag; `pageIndex` is a resume position that may move backwards, so
 * a row with a position but no completion is in progress. A row carrying `unreadAt` is a tombstone
 * left by an explicit mark-unread and is plainly unread: its zero position is not progress.
 */
function readStateOf(p: ChapterProgressDto | undefined): ReadState {
  if (!p || p.unreadAt !== null) {
    return { read: false, inProgress: false, external: false, watched: false }
  }

  return {
    read: p.completed,
    inProgress: !p.completed && p.pageIndex > 0,
    external: p.external,
    watched: p.watched,
  }
}

export default function SeriesDetailPage() {
  const renderLabel = useLabel()
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
  const tab = TABS.includes(requestedTab as Tab) ? (requestedTab as Tab) : 'details'
  const changeTab = (value: string | null) => {
    if (!value) return
    const next = new URLSearchParams(searchParams)
    if (value === 'details') next.delete('tab')
    else next.set('tab', value)
    setSearchParams(next, { replace: true })
  }
  const isMobile = useMediaQuery('(max-width: 47.99em)')
  const { data: series, isLoading } = useSeriesDetail(seriesId)
  // So that a series reached from another one (Similar, Related) offers a back link that names it
  // rather than the generic "Series".
  usePageLabel(series?.title)
  const { data: chapters } = useChapters(seriesId)

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
  const fileOrigin = (name: string, releaseName: string | null) => {
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
  }
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
  const readTracking = useReadTracking()
  const { data: progressRows } = useSeriesReadProgress(seriesId)
  const { data: continueAt } = useContinueReading(seriesId)
  const { data: files} = useSeriesFiles(seriesId, true)
  const setRead = useSetChapterRead(seriesId)
  const readProgress = useMemo(
      () => new Map((progressRows ?? []).map((p) => [p.chapterId, p])),
      [progressRows],
  )
  const readStateFor = useCallback(
      (c: ChapterDto) => readStateOf(readProgress.get(c.id)),
      [readProgress],
  )
  const { data: queue } = useQueue()
  const queueByChapterId = useMemo(
      () => new Map((queue?.items ?? []).filter((q) => q.seriesId === seriesId).map((q) => [q.chapterId, q])),
      [queue, seriesId],
  )
  /**
   * Read-aware filters, kept separate from `chapterFilters` because they need the progress map.
   * Both only consider downloaded chapters: a missing chapter is neither read nor "left to read".
   */
  const filters = useMemo<Record<string, (c: ChapterDto) => boolean>>(
      () => ({
        ...chapterFilters,
        unread: (c: ChapterDto) => c.hasFile && !readStateFor(c).read,
        read: (c: ChapterDto) => c.hasFile && readStateFor(c).read,
      }),
      [readStateFor],
  )
  const deleteSeries = useDeleteSeries()
  const refresh = useRefreshSeries()
  const refreshMetadata = useRefreshMetadata()
  const rescan = useRescanSeries()
  const moveSeries = useMoveSeries()
  const { data: rootFolders } = useRootFolders()
  const [moveModalOpen, setMoveModalOpen] = useState(false)
  const [renameModalOpen, setRenameModalOpen] = useState(false)
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
  const setIncognito = useSetIncognito()
  const setNotificationMode = useSetSeriesNotificationMode()
  const setRating = useSetRating()
  const unlinkChapters = useUnlinkChapters()
  const setChaptersWanted = useSetChaptersWanted()
  const deleteChapters = useDeleteChapters()
  const [releaseModalOpen, setReleaseModalOpen] = useState(false)
  const [chapterFilter, setChapterFilter] = useState('all')
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
  const [linkModalOpen, setLinkModalOpen] = useState(false)
  const [deleteChaptersModalOpen, setDeleteChaptersModalOpen] = useState(false)
  const [deleteSeriesModalOpen, setDeleteSeriesModalOpen] = useState(false)
  const [deleteSeriesFiles, setDeleteSeriesFiles] = useState(false)

  // Without DownloadChapters the two buttons that queue downloads become one that asks an admin to.
  const { can } = useAuth()
  const canDownload = can('DownloadChapters')
  const createRequest = useCreateSeriesRequest()
  // Already in cache: the sources section below this page fetches the same query. Two enabled
  // mappings is the floor for "find better copy" having anything to show.
  const { data: sourceMappings } = useSourceMappings(seriesId)
  const enabledMappings = (sourceMappings ?? []).filter((m) => m.enabled).length
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

  /**
   * The rows the table is currently showing. Shift-ranges and "Select all" both work over this
   * rather than the full chapter list: with a filter active, a range drawn between two visible
   * rows would otherwise sweep in every hidden chapter numbered between them.
   */
  const visibleChapters = useMemo(() => {
    const query = chapterSearch.trim().toLocaleLowerCase()
    const numberQuery = query.match(/^(?:ch(?:apter)?\.?\s*)?(\d+(?:\.\d+)?)$/)?.[1]
    return (chapters ?? [])
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
  }, [chapters, filters, chapterFilter, chapterSearch, i18n.locale])

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
  const missingWanted = useMemo(
      () => (chapters ?? []).filter((c) => c.wanted && !c.hasFile).length,
      [chapters],
  )

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
  /** Whether the fold seed below has already run for the series currently on screen. */
  const seededFoldsFor = useRef<number | null>(null)

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
    if (seededFoldsFor.current === seriesId) return
    seededFoldsFor.current = seriesId

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

  const toggleSpanFold = useCallback((key: string) => {
    setFoldedSpans((current) => {
      const next = new Set(current)
      if (!next.delete(key)) next.add(key)
      return next
    })
  }, [])

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

  useEffect(() => {
    setChapterPage(1)
    selectAnchor.current = null
  }, [chapterFilter, chapterSearch, chapterPageSizePreference])

  useEffect(() => {
    if (chapterPage > chapterPageCount) setChapterPage(chapterPageCount)
  }, [chapterPage, chapterPageCount])

  /** One entry per displayed row; a folded span is a single step carrying every chapter it hides. */
  const rangeUnits = useMemo(
      () =>
          pagedRows.map((r) =>
              r.kind === 'chapter' ? { ids: [r.chapter.id] } : { ids: r.rows.map((c) => c.id) },
          ),
      [pagedRows],
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
        // Clip to the visible run, below the table header. Either badge can be on another
        // page (or hidden by a filter), including both on a middle page of a long season.
        const top = startEl
            ? startEl.getBoundingClientRect().bottom - wrapRect.top + 2
            : cells[0].getBoundingClientRect().top - wrapRect.top
        const bottom = endEl
            ? endEl.getBoundingClientRect().top - wrapRect.top - 2
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
          openStart: !startEl,
          openEnded: !endEl,
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
  }, [chapterTable, animeSpans, foldedSpans, pagedRows, markerSlot])

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

  /** The single row a folded span collapses to: the range, what's in it, and what to do with it. */
  const renderSpanRow = (span: AnimeSpan, rows: ChapterDto[]) => {
    const { label: spanLabel } = span
    const total = rows.length
    const downloaded = rows.filter((c) => c.hasFile)
    const downloadedCount = downloaded.length
    const states = downloaded.map(readStateFor)
    const watchedCount = states.filter((st) => st.watched).length
    const readCount = states.filter((st) => st.read && !st.watched).length
    const done = watchedCount + readCount
    const ids = rows.map((c) => c.id)

    // A folded span stands in for every chapter under it, so its switch has to speak for all of
    // them. When they disagree there is no honest on/off to show: the track gets a half-filled
    // look and the thumb a dash, and clicking resolves the whole range to wanted (the same way a
    // tri-state checkbox settles), which is why `checked` is "all of them" and not "any of them".
    const wantedCount = rows.filter((c) => c.wanted).length
    const allWanted = wantedCount === rows.length
    const mixed = wantedCount > 0 && !allWanted

    return (
        <Table.Tr key={`span:${span.key}`} className="chapter-span-row">
          <Table.Td onClick={(e) => e.stopPropagation()}>
            <Tooltip
                label={
                  mixed
                      ? t`${wantedCount} of ${total} chapters wanted · click to want all`
                      : allWanted
                          ? t`All ${total} chapters wanted`
                          : t`None of the ${total} chapters wanted`
                }
                withArrow
            >
              <Switch
                  size="xs"
                  checked={allWanted}
                  classNames={mixed ? { track: 'chapter-span-wanted-mixed' } : undefined}
                  thumbIcon={mixed ? <IconMinus size={10} stroke={3} /> : undefined}
                  aria-label={t`Wanted for ${spanLabel}: ${wantedCount} of ${total} chapters`}
                  disabled={setChaptersWanted.isPending}
                  onChange={(e) => applyWanted(ids, e.currentTarget.checked)}
              />
            </Tooltip>
          </Table.Td>
          <Table.Td className={hasAnimeMarkers ? 'chapter-cell' : undefined}>
            <Group gap={6} wrap="nowrap">
              <Badge
                  size="sm"
                  color="blue"
                  variant="light"
                  leftSection={<IconDeviceTv size={12} />}
                  className="chapter-span-badge"
                  onClick={() => toggleSpanFold(span.key)}
              >
                {span.label}
              </Badge>
              <Text size="sm" fw={550} className="tnum">
                {spanRangeLabel(span)}
              </Text>
            </Group>
          </Table.Td>
          <Table.Td>
            <Text size="sm" c="var(--ink-3)" className="tnum">
              <Trans>{total} chapters · {downloadedCount} downloaded</Trans>
              {watchedCount > 0 && (
                  <>
                    {' · '}
                    <Trans>{watchedCount} watched</Trans>
                  </>
              )}
              {readCount > 0 && (
                  <>
                    {' · '}
                    <Trans>{readCount} read</Trans>
                  </>
              )}
              {/* Spelled out only when the range disagrees with itself: the switch alone can show
                that state but not its size, and hovering for a tooltip is a poor way to find out. */}
              {mixed && (
                  <>
                    {' · '}
                    <Trans>{wantedCount} wanted</Trans>
                  </>
              )}
            </Text>
          </Table.Td>
          <Table.Td />
          <Table.Td />
          <Table.Td>
            {downloaded.length > 0 && (
                <Progress
                    value={(done / downloaded.length) * 100}
                    color={watchedCount > readCount ? 'violet' : 'teal'}
                    size="sm"
                    radius="xl"
                />
            )}
          </Table.Td>
          <Table.Td onClick={(e) => e.stopPropagation()}>
            <Group gap={2} wrap="nowrap" justify="flex-end">
              {readTracking && (
                  <Menu shadow="md" position="bottom-end" withinPortal>
                    <Menu.Target>
                      <ActionIcon variant="subtle" color="gray" aria-label={t`Actions for ${spanLabel}`}>
                        <IconDotsVertical size={17} />
                      </ActionIcon>
                    </Menu.Target>
                    <Menu.Dropdown>
                      <Menu.Item
                          leftSection={<IconDeviceTv size={15} />}
                          onClick={() => applyReadState(ids, 'watched')}
                      >
                        <Trans>Mark watched</Trans>
                      </Menu.Item>
                      <Menu.Item
                          leftSection={<IconEyeCheck size={15} />}
                          onClick={() => applyReadState(ids, 'read')}
                      >
                        <Trans>Mark read</Trans>
                      </Menu.Item>
                      <Menu.Item
                          leftSection={<IconEyeOff size={15} />}
                          onClick={() => applyReadState(ids, 'unread')}
                      >
                        <Trans>Mark unread</Trans>
                      </Menu.Item>
                      <Menu.Divider />
                      <Menu.Item
                          leftSection={<IconListCheck size={15} />}
                          onClick={() => {
                            setSelectMode(true)
                            selectAnchor.current = null
                            setSelected(new Set(ids))
                          }}
                      >
                        <Trans>Select these chapters</Trans>
                      </Menu.Item>
                    </Menu.Dropdown>
                  </Menu>
              )}
              <Tooltip label={t`Expand`} withArrow>
                <ActionIcon
                    variant="subtle"
                    color="gray"
                    onClick={() => toggleSpanFold(span.key)}
                    aria-label={t`Expand ${spanLabel}`}
                >
                  <IconChevronDown size={17} />
                </ActionIcon>
              </Tooltip>
            </Group>
          </Table.Td>
        </Table.Tr>
    )
  }

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

  // The top bar takes the series name once the hero heading has scrolled out of view.
  const [heroTitleHidden, setHeroTitleHidden] = useState(false)
  const heroTitleShown = series !== undefined
  useEffect(() => {
    const el = document.querySelector('.series-hero-title')
    if (!el) return
    const observer = new IntersectionObserver(([entry]) => setHeroTitleHidden(!entry.isIntersecting), {
      rootMargin: '-58px 0px 0px 0px',
    })
    observer.observe(el)
    return () => observer.disconnect()
  }, [heroTitleShown])
  useShellTitle(heroTitleHidden && series ? series.displayTitle : null)

  if (isLoading) {
    return (
        <SurfaceFrame width="full" pageStyle="editorial">
          <SeriesHeroSkeleton />
        </SurfaceFrame>
    )
  }

  if (!series) {
    return (
        <EmptyState
            title={t`Series not found`}
            description={t`It may have been removed from the library.`}
            actionLabel={t`Back to library`}
            actionTo="/library"
        />
    )
  }

  const status = seriesStatusVisual(series.status)
  const contentRating = contentRatingVisual(series.contentRating)
  const altTitles = readableTitles(series.altTitles, i18n.locale)
  const seriesTitle = series.title
  // Errors are reported globally (see main.tsx); only success needs saying here. `info` is for
  // outcomes that aren't failures but aren't wins either — a download action that found nothing
  // left to queue, which would otherwise report a cheerful "Queued 0".
  const notify = {
    ok: (message: string) => notifications.show({ message, color: 'green' }),
    info: (message: string) => notifications.show({ message, color: 'yellow' }),
  }
  const wantedFilterCount = chapters?.filter(chapterFilters.wanted).length ?? 0
  const missingFilterCount = chapters?.filter(chapterFilters.missing).length ?? 0
  const downloadedFilterCount = chapters?.filter(chapterFilters.downloaded).length ?? 0
  const unreadFilterCount = chapters?.filter(filters.unread).length ?? 0
  const specialsFilterCount = chapters?.filter(chapterFilters.specials).length ?? 0
  const mainFilterCount = chapters?.filter(chapterFilters.main).length ?? 0
  const readFilterCount = chapters?.filter(filters.read).length ?? 0
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
        ...(readTracking && progress.have > 0
            ? [{ value: 'unread', label: t`Unread (${unreadFilterCount})` }]
            : []),
        // Without a special to hide, "Main" is "All" under a second name.
        ...(specialsFilterCount > 0 ? [{ value: 'main', label: t`Main (${mainFilterCount})` }] : []),
        { value: 'specials', label: t`Specials (${specialsFilterCount})` },
      ]
      : []

  const queueNext = (count: number) => {
    setNextCountOpen(false)
    downloadNext.mutate(
        { seriesId, count },
        {
          onSuccess: (r) =>
              r.queued > 0
                  ? notify.ok(plural(r.queued, { one: 'Queued # chapter', other: 'Queued # chapters' }))
                  : notify.info(staticT`Nothing left to queue. Every wanted chapter is on disk or already queued.`),
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
                      <Button.Group>
                        <Button
                            variant="default"
                            size="md"
                            radius="md"
                            leftSection={<IconDownload size={17} />}
                            loading={searchMissing.isPending || downloadNext.isPending}
                            onClick={requestQueueAllWanted}
                        >
                          {/* The count is the point of the label: it says what the click will actually
                        queue, so nobody has to open the Chapters tab to find out. */}
                          {missingWanted > 0 ? (
                              <Trans>Download {missingWanted} wanted</Trans>
                          ) : (
                              <Trans>Download all wanted</Trans>
                          )}
                        </Button>
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
                      </Button.Group>
                      <Button
                          variant="default"
                          size="md"
                          radius="md"
                          leftSection={<IconSearch size={17} />}
                          onClick={() => setReleaseModalOpen(true)}
                      >
                        <Trans>Search releases</Trans>
                      </Button>
                    </>
                ) : (
                    <Button
                        variant="default"
                        size="md"
                        radius="md"
                        leftSection={<IconSend size={17} />}
                        onClick={() => {
                          setRequestStart('')
                          setRequestEnd('')
                          setRequestNote('')
                          setRequestModalOpen(true)
                        }}
                    >
                      <Trans>Request chapters</Trans>
                    </Button>
                )}
                <SeriesActionsMenu
                    monitorMode={series.monitorNewItems}
                    incognito={series.incognito}
                    notificationMode={series.notificationMode}
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
                    onSetIncognito={(mode) =>
                        setIncognito.mutate(
                            { seriesId, mode },
                            {
                              onSuccess: (r) => {
                                const { incognito } = r
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
                                const { notificationMode } = r
                                notify.ok(staticT`Notifications: ${notificationMode}`)
                              },
                            },
                        )
                    }
                    canRemove={can('DeleteSeries')}
                    onRemove={() => setDeleteSeriesModalOpen(true)}
                />
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
                  <span className="series-tab-count tnum">{files ? files.length : "?"}</span>
                </Tabs.Tab>
              </Tabs.List>
            }
        />

        <Tabs.Panel value="details">
          <Stack className="series-detail-overview" gap="lg">
            <div className="series-split">
              <Paper className="series-detail-synopsis" withBorder radius="lg" p="lg">
                <Title order={3} fz={17}>
                  <Trans>Synopsis</Trans>
                </Title>
                {series.overview ? (
                    <Text size="sm" mt="sm" c="var(--ink-3)" style={{ lineHeight: 1.66, maxWidth: '100ch' }}>
                      {series.overview}
                    </Text>
                ) : (
                    <Text size="sm" mt="sm" c="var(--ink-3)">
                      <Trans>No synopsis yet.</Trans> <Trans>Refresh metadata to fetch one.</Trans>
                    </Text>
                )}

                {(series.animeStart || series.animeEnd) && (
                    <>
                      <Divider my="md" color="var(--hairline)" />
                      <Title order={4} fz={14} mb={10}>
                        <Trans>Anime coverage</Trans>
                      </Title>
                      <AnimeCoverageBar
                          start={series.animeStart}
                          end={series.animeEnd}
                          totalChapters={series.totalChapters ?? lastChapterNumber}
                          readChapter={highestReadChapter}
                      />
                    </>
                )}

                <Divider my="md" color="var(--hairline)" />

                <Stack gap="md">
                  {series.genres.length > 0 && (
                      <div>
                        <Title order={4} fz={14} mb={10}>
                          <Trans>Genres</Trans>
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
                        <Title order={4} fz={14} mb={10}>
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
                        <Title order={4} fz={14} mb={10}>
                          <Trans>Open on</Trans>
                        </Title>
                        <MetadataLinks links={series.links} />
                      </div>
                  )}
                </Stack>
              </Paper>

              <div className="series-split-row">
                <Paper className="series-detail-source-panel" withBorder radius="lg" p="lg">
                  {series.numberingClash && (
                      <Alert
                          mb="md"
                          color="yellow"
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
                  <SourceMappingsSection
                      seriesId={seriesId}
                      seriesTitle={series.title}
                      matching={series.sourceMatchPending}
                  />
                </Paper>
                <Paper className="series-detail-metadata-panel" withBorder radius="lg" p="lg">
                  <Title order={3} fz={17} mb="sm">
                    <Trans>Metadata</Trans>
                  </Title>
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
                    <RecordRow label={t`Status`}>{renderLabel(status.label)}</RecordRow>
                    {contentRating && <RecordRow label={t`Content rating`}>{renderLabel(contentRating.label)}</RecordRow>}
                    {series.totalVolumes != null && (
                        <RecordRow label={t`Volumes`}>{series.totalVolumes}</RecordRow>
                    )}
                    <RecordRow label={t`Chapters known`}>{series.knownChapterCount}</RecordRow>
                    {series.readTimeEstimate && (
                        <RecordRow label={t`Time to finish`}>
                          <ReadTimeEstimateText estimate={series.readTimeEstimate} />
                        </RecordRow>
                    )}
                    {series.rootFolderPath && (
                        <RecordRow label={t`Folder`}>
                          <Text size="sm" c="var(--ink-3)" ff="monospace" style={{ wordBreak: 'break-all' }}>
                            {series.rootFolderPath}
                          </Text>
                        </RecordRow>
                    )}
                  </div>
                </Paper>
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
            title={t`Download ${missingWanted} wanted chapters?`}
            centered
        >
          <Stack gap="sm">
            <Text mt="sm" size="sm">
              <Trans>This will add {missingWanted} chapters to the download queue.</Trans>
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
                <Trans>Download {missingWanted} chapters</Trans>
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
          <Stack gap="sm">
            <NumberInput
                label={t`How many`}
                description={t`${missingWanted} wanted chapter(s) are missing`}
                min={1}
                value={nextCount}
                onChange={setNextCount}
                data-autofocus
            />
            <Group justify="flex-end">
              <Button variant="default" onClick={() => setNextCountOpen(false)}>
                <Trans>Cancel</Trans>
              </Button>
              <Button
                  loading={downloadNext.isPending}
                  onClick={() => queueNext(Math.max(1, Number(nextCount) || 1))}
              >
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
            {/* Chapters */}
            <Group className="series-detail-chapter-toolbar" justify="space-between" wrap="wrap" gap="sm">
              <Group gap="xs" align="baseline">
                <Title order={3}><Trans>Chapters</Trans></Title>
                {chapters && (
                    <Text size="sm" c="var(--ink-3)" className="tnum">
                      {progress.have}/{progress.total}
                    </Text>
                )}
                {chapters && readTracking && progress.have > 0 && (
                    <Badge size="sm" variant="light" color="teal" className="tnum">
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
                            size="xs"
                            value={chapterFilter}
                            onChange={setChapterFilter}
                            data={chapterFilterData}
                        />
                    )}
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

            {chapters && chapters.length > 0 && (
                <TextInput
                    size="sm"
                    value={chapterSearch}
                    onChange={(event) => setChapterSearch(event.currentTarget.value)}
                    leftSection={<IconSearch size={16} />}
                    rightSection={
                      chapterSearch ? (
                          <ActionIcon
                              size="sm"
                              variant="subtle"
                              color="gray"
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
            )}

            {selectMode && (
                <Paper className="series-detail-chapter-selection" withBorder p="xs" radius="lg">
                  <Group justify="space-between" wrap="wrap" gap="xs">
                    <Group gap="xs">
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
                      {canDownload && (
                          <Button
                              size="xs"
                              variant="light"
                              leftSection={<IconDownload size={15} />}
                              disabled={selected.size === 0}
                              loading={downloadChapters.isPending}
                              onClick={() =>
                                  downloadChapters.mutate([...selected], {
                                    onSuccess: (r) =>
                                        r.queued > 0
                                            ? notify.ok(
                                                plural(r.queued, { one: 'Queued # chapter', other: 'Queued # chapters' }),
                                            )
                                            : notify.info(
                                                r.error ?? staticT`Nothing to queue, those chapters are already on disk`,
                                            ),
                                  })
                              }
                          >
                            <Trans>Download</Trans>
                          </Button>
                      )}
                      <Button
                          size="xs"
                          variant="light"
                          leftSection={<IconEye size={15} />}
                          disabled={selected.size === 0}
                          loading={setChaptersWanted.isPending && setChaptersWanted.variables?.wanted === true}
                          onClick={() => applyWanted([...selected], true)}
                      >
                        <Trans>Want</Trans>
                      </Button>
                      <Button
                          size="xs"
                          variant="light"
                          color="gray"
                          leftSection={<IconEyeOff size={15} />}
                          disabled={selected.size === 0}
                          loading={setChaptersWanted.isPending && setChaptersWanted.variables?.wanted === false}
                          onClick={() => applyWanted([...selected], false)}
                      >
                        <Trans>Don't want</Trans>
                      </Button>
                      {readTracking && (
                          <>
                            <Button
                                size="xs"
                                variant="light"
                                color="violet"
                                leftSection={<IconDeviceTv size={15} />}
                                disabled={selected.size === 0}
                                loading={setChaptersState.isPending && setChaptersState.variables?.state === 'watched'}
                                onClick={() => applyReadState([...selected], 'watched')}
                            >
                              <Trans>Mark watched</Trans>
                            </Button>
                            <Button
                                size="xs"
                                variant="light"
                                color="teal"
                                leftSection={<IconEyeCheck size={15} />}
                                disabled={selected.size === 0}
                                loading={setChaptersState.isPending && setChaptersState.variables?.state === 'read'}
                                onClick={() => applyReadState([...selected], 'read')}
                            >
                              <Trans>Mark read</Trans>
                            </Button>
                            <Button
                                size="xs"
                                variant="light"
                                color="gray"
                                leftSection={<IconEyeOff size={15} />}
                                disabled={selected.size === 0}
                                loading={setChaptersState.isPending && setChaptersState.variables?.state === 'unread'}
                                onClick={() => applyReadState([...selected], 'unread')}
                            >
                              <Trans>Mark unread</Trans>
                            </Button>
                          </>
                      )}
                      <Button
                          size="xs"
                          variant="light"
                          leftSection={<IconLink size={15} />}
                          disabled={selected.size === 0}
                          onClick={() => setLinkModalOpen(true)}
                      >
                        <Trans>Link to file</Trans>
                      </Button>
                      <Button
                          size="xs"
                          variant="light"
                          color="yellow"
                          leftSection={<IconLinkOff size={15} />}
                          disabled={selected.size === 0}
                          loading={unlinkChapters.isPending}
                          onClick={() =>
                              unlinkChapters.mutate([...selected], {
                                onSuccess: (r) => {
                                  notify.ok(
                                      plural(r.unlinked, { one: 'Unlinked # chapter', other: 'Unlinked # chapters' }),
                                  )
                                  exitSelectMode()
                                },
                              })
                          }
                      >
                        <Trans>Unlink</Trans>
                      </Button>
                      <Button
                          size="xs"
                          variant="light"
                          color="red"
                          leftSection={<IconTrash size={15} />}
                          disabled={selected.size === 0}
                          onClick={() => setDeleteChaptersModalOpen(true)}
                      >
                        <Trans>Delete</Trans>
                      </Button>
                      <Button
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
                onClose={() => setDeleteChaptersModalOpen(false)}
                title={t`Delete chapters?`}
                centered
            >
              <Stack gap="md">
                <Text size="sm" c="var(--ink-3)">
                  <Trans>This permanently removes {selectedCount} chapter row(s), not just their file link,
                    along with any backing file on disk.</Trans>{' '}
                  <Trans>Use this to clean up chapters pulled in by a wrong source match.</Trans>{' '}
                  <Trans>Fix or remove the source mapping first, or a refresh will bring them right back.</Trans>
                </Text>
                <Text size="sm" c="red">
                  <Trans>This action cannot be undone.</Trans>
                </Text>
                <Group justify="flex-end">
                  <Button variant="default" onClick={() => setDeleteChaptersModalOpen(false)}>
                    <Trans>Cancel</Trans>
                  </Button>
                  <Button
                      color="red"
                      leftSection={<IconTrash size={16} />}
                      loading={deleteChapters.isPending}
                      onClick={() =>
                          deleteChapters.mutate([...selected], {
                            onSuccess: (r) => {
                              notify.ok(
                                  plural(r.deleted, { one: 'Deleted # chapter', other: 'Deleted # chapters' }),
                              )
                              setDeleteChaptersModalOpen(false)
                              exitSelectMode()
                            },
                          })
                      }
                  >
                    <Trans>Delete</Trans>
                  </Button>
                </Group>
              </Stack>
            </Modal>

            <LinkChaptersModal
                seriesId={seriesId}
                chapterIds={[...selected]}
                opened={linkModalOpen}
                onClose={() => {
                  setLinkModalOpen(false)
                  exitSelectMode()
                }}
            />

            {!chapters || chapters.length === 0 ? (
                <Text c="var(--ink-3)" size="sm">
                  <Trans>No chapters known.</Trans> <Trans>Link a source and refresh.</Trans>
                </Text>
            ) : renderedRows.rows.length === 0 ? (
                <Text c="var(--ink-3)" size="sm">
                  <Trans>No chapters match this search and filter.</Trans>
                </Text>
            ) : (
                <Stack gap="sm">
                  <Paper className="series-detail-chapter-table" withBorder radius="lg" style={{ overflow: 'hidden' }}>
                    <Box
                        pos="relative"
                        ref={setChapterTable}
                        style={{ '--chapter-marker-slot': `${markerSlot}px` } as React.CSSProperties}
                    >
                      <Table.ScrollContainer minWidth={isMobile ? 0 : 700}>
                        <Table className="chapter-table" highlightOnHover verticalSpacing="xs">
                          <Table.Thead>
                            <Table.Tr>
                              <Table.Th w={52}><Trans>Wanted</Trans></Table.Th>
                              <Table.Th w={170}><Trans>Chapter</Trans></Table.Th>
                              <Table.Th><Trans>Title</Trans></Table.Th>
                              <Table.Th w={120}><Trans>Released</Trans></Table.Th>
                              <Table.Th w={110}><Trans>Source</Trans></Table.Th>
                              <Table.Th w={240}><Trans>Status</Trans></Table.Th>
                              <Table.Th w={124} />
                            </Table.Tr>
                          </Table.Thead>
                          <Table.Tbody>
                            {pagedRows.map((row) => {
                              if (row.kind === 'span') {
                                return renderSpanRow(row.span, row.rows)
                              }
                              const c = row.chapter
                              const chapterLbl = chapterLabel(c)
                              const chapterNumber = c.number
                              const fileVolume = c.fileVolume
                              const { read, inProgress, external, watched } = readStateFor(c)
                              const rowProgress = readProgress.get(c.id)
                              const pageIndex = rowProgress ? rowProgress.pageIndex + 1 : null
                              const pageCount = rowProgress ? rowProgress.pageCount : null
                              const queueItem = queueByChapterId.get(c.id)
                              const isSelected = selectMode && selected.has(c.id)
                              return (
                                  <Table.Tr
                                      key={c.id}
                                      opacity={c.wanted || c.hasFile ? 1 : 0.55}
                                      className={[
                                        watched
                                            ? 'chapter-row-watched'
                                            : read
                                                ? 'chapter-row-read'
                                                : inProgress
                                                    ? 'chapter-row-reading'
                                                    : '',
                                        selectMode ? 'chapter-row-selectable' : '',
                                        isSelected ? 'chapter-row-selected' : '',
                                      ]
                                          .filter(Boolean)
                                          .join(' ') || undefined}
                                      onClick={selectMode ? (e) => clickChapterRow(c.id, e.shiftKey) : undefined}
                                      aria-selected={selectMode ? isSelected : undefined}
                                  >
                                    {/* The controls in this cell stay live in select mode, so its clicks mustn't
                        bubble up and toggle the row as well. Same for the actions cell. */}
                                    <Table.Td onClick={(e) => e.stopPropagation()}>
                                      <Switch
                                          size="xs"
                                          checked={c.wanted}
                                          aria-label={t`Want ${chapterLbl}`}
                                          onChange={(e) =>
                                              toggleWanted.mutate({ chapterId: c.id, wanted: e.currentTarget.checked })
                                          }
                                      />
                                    </Table.Td>
                                    {/* The marker slot is reserved on every row, not just the ones carrying a badge:
                        the season lines run down that slot, and a label free to grow into it on the
                        other 200 rows would have the line drawn straight through it. */}
                                    <Table.Td className={hasAnimeMarkers ? 'chapter-cell' : undefined} data-chapter-number={c.number ?? undefined}>
                                      <Group gap={6} wrap="nowrap">
                                        {c.fileVolume !== null && !c.isOneShot && c.number !== null && (
                                            <Tooltip label={t`Contained in a volume/compilation file`} withArrow>
                                              <Badge size="sm" color="indigo" variant="light" className="tnum">
                                                <Trans>Vol.{fileVolume}</Trans>
                                              </Badge>
                                            </Tooltip>
                                        )}
                                        <Text size="sm" fw={550} className="tnum">
                                          {c.isOneShot || c.number === null
                                              ? chapterLabel(c)
                                              : c.fileVolume !== null
                                                  ? <Trans>Ch.{chapterNumber}</Trans>
                                                  : chapterLabel(c)}
                                        </Text>
                                      </Group>
                                      {hasAnimeMarkers && c.number !== null && (
                                          <div className="chapter-span-markers">
                                            {(animeMarkers.get(c.number) ?? []).map((marker, i) => {
                                              // A marker that starts or ends an included span doubles as its fold
                                              // control and as the anchor the overlay measures its line from;
                                              // everything else (an unpaired end, or one bumped by the 3-lane cap) is
                                              // a plain informational badge, same as always.
                                              const span = spanForMarker(c.number!, marker.kind)
                                              const { label: markerLabel } = marker
                                              return (
                                                  <Tooltip
                                                      key={i}
                                                      label={
                                                          span
                                                              ? marker.kind === 'start'
                                                                  ? foldedSpans.has(span.key)
                                                                      ? t`${markerLabel} Anime adaptation starts here · click to expand`
                                                                      : t`${markerLabel} Anime adaptation starts here · click to collapse`
                                                                  : foldedSpans.has(span.key)
                                                                      ? t`${markerLabel} Anime adaptation ends here · click to expand`
                                                                      : t`${markerLabel} Anime adaptation ends here · click to collapse`
                                                              : marker.kind === 'start'
                                                                  ? t`${markerLabel} Anime adaptation starts here`
                                                                  : t`${markerLabel} Anime adaptation ends here`
                                                      }
                                                      withArrow
                                                  >
                                                    <Badge
                                                        size="sm"
                                                        color={marker.kind === 'start' ? 'blue' : 'red'}
                                                        variant="light"
                                                        className={`chapter-span-marker${span ? ' chapter-span-badge' : ''}`}
                                                        ref={
                                                          span
                                                              ? (el: HTMLDivElement | null) =>
                                                                  setMarkerRef(`${span.key}:${marker.kind}`, el)
                                                              : undefined
                                                        }
                                                        onClick={span ? () => toggleSpanFold(span.key) : undefined}
                                                    >
                                                      {marker.label}
                                                    </Badge>
                                                  </Tooltip>
                                              )
                                            })}
                                          </div>
                                      )}
                                    </Table.Td>
                                    <Table.Td>
                                      <Text size="sm" c="var(--ink-3)" lineClamp={1}>
                                        {c.title}
                                      </Text>
                                    </Table.Td>
                                    <Table.Td>
                                      <Text size="sm" c="var(--ink-3)" className="tnum">
                                        {c.releaseDate ? formatDate(c.releaseDate) : '-'}
                                      </Text>
                                    </Table.Td>
                                    <Table.Td>
                                      {/* Where the file on disk actually came from, which is what makes a source
                          comparison actionable: the winner is often not what you already have. */}
                                      {!c.hasFile || !c.fileSourceName ? (
                                          <Text size="sm" c="var(--ink-3)">
                                            -
                                          </Text>
                                      ) : (
                                          (() => {
                                            const origin = fileOrigin(c.fileSourceName, c.fileReleaseName)
                                            return (
                                                <Tooltip label={origin.hint} withArrow disabled={!origin.hint}>
                                                  <Badge size="sm" variant={origin.scraped ? 'light' : 'outline'} color="gray">
                                                    {origin.label}
                                                  </Badge>
                                                </Tooltip>
                                            )
                                          })()
                                      )}
                                    </Table.Td>
                                    <Table.Td>
                                      {/* Wraps rather than clipping: a re-read chapter carries three badges. */}
                                      <Group gap={6} wrap="wrap">
                                        {queueItem ? (
                                            (() => {
                                              const visual = queueStatusVisual(queueItem.status)
                                              const failure = queueErrorMessage(queueItem, renderLabel)
                                              return (
                                                  <Tooltip
                                                    label={failure ?? renderLabel(visual.label)}
                                                    withArrow
                                                    disabled={failure === null}
                                                  >
                                                    <Group gap={6} wrap="nowrap">
                                                      {queueItem.pagesTotal > 0 && (
                                                          <Progress
                                                              value={(queueItem.pagesDone / queueItem.pagesTotal) * 100}
                                                              w={72}
                                                              radius="xl"
                                                              animated={queueItem.status === 'Downloading'}
                                                              color={queueItem.status === 'Failed' ? 'red' : 'brand'}
                                                          />
                                                      )}
                                                      <Badge
                                                          size="sm"
                                                          color={visual.color}
                                                          variant="light"
                                                          leftSection={<visual.Icon size={12} />}
                                                          className="tnum"
                                                      >
                                                        {queueItem.pagesTotal > 0
                                                            ? `${renderLabel(visual.label)} ${queueItem.pagesDone}/${queueItem.pagesTotal}`
                                                            : renderLabel(visual.label)}
                                                      </Badge>
                                                    </Group>
                                                  </Tooltip>
                                              )
                                            })()
                                        ) : c.hasFile ? (
                                            <Badge size="sm" color="teal" variant="light" leftSection={<IconCircleCheck size={12} />}>
                                              <Trans>Downloaded</Trans>
                                            </Badge>
                                        ) : (
                                            <Badge size="sm" color="gray" variant="light">
                                              <Trans>Missing</Trans>
                                            </Badge>
                                        )}
                                        {read && (
                                            <Tooltip
                                                label={
                                                  watched
                                                      ? t`Marked watched, not read. Doesn't count toward reading stats`
                                                      : external
                                                          ? t`Read in Kavita`
                                                          : t`Read in Maki`
                                                }
                                                withArrow
                                            >
                                              <Badge
                                                  size="sm"
                                                  color={watched ? 'violet' : 'teal'}
                                                  variant={watched || external ? 'light' : 'filled'}
                                                  leftSection={
                                                    watched ? <IconDeviceTv size={12} /> : <IconEyeCheck size={12} />
                                                  }
                                              >
                                                {watched ? <Trans>Watched</Trans> : <Trans>Read</Trans>}
                                              </Badge>
                                            </Tooltip>
                                        )}
                                        {/* Shown alongside Read when a finished chapter is being re-read. */}
                                        {inProgress && (
                                            <Badge size="sm" color="blue" variant="light" className="tnum">
                                              {/* pageCount is 0 on rows imported from Kavita: the reader fills it
                                  in on first open, so show a plain label until then. */}
                                              {rowProgress && rowProgress.pageCount > 0 ? (
                                                  <Trans>Page {pageIndex}/{pageCount}</Trans>
                                              ) : (
                                                  <Trans>Reading</Trans>
                                              )}
                                            </Badge>
                                        )}
                                      </Group>
                                    </Table.Td>
                                    <Table.Td onClick={(e) => e.stopPropagation()}>
                                      <Group gap={2} wrap="nowrap" justify="flex-end">
                                        {c.hasFile && (
                                            <>
                                              <Tooltip label={read ? t`Mark unread` : t`Mark read`} withArrow>
                                                <ActionIcon
                                                    variant={read ? 'light' : 'subtle'}
                                                    color={read ? 'teal' : 'gray'}
                                                    onClick={() => setRead.mutate({ chapterId: c.id, read: !read })}
                                                    aria-label={t`Toggle read state of ${chapterLbl}`}
                                                >
                                                  {!read ? <IconEye size={17} /> : <IconEyeOff size={17} />}
                                                </ActionIcon>
                                              </Tooltip>
                                              <Tooltip label={t`Read`} withArrow>
                                                <ActionIcon
                                                    component={Link}
                                                    to={`/read/${c.id}`}
                                                    variant="subtle"
                                                    color="brand"
                                                    aria-label={t`Read ${chapterLbl}`}
                                                >
                                                  <IconBook size={17} />
                                                </ActionIcon>
                                              </Tooltip>
                                            </>
                                        )}
                                        {!c.hasFile && canDownload && (
                                            <Tooltip label={t`Download this chapter`} withArrow>
                                              <ActionIcon
                                                  variant="subtle"
                                                  color="brand"
                                                  onClick={() =>
                                                      search.mutate(c.id, {
                                                        onSuccess: () => notify.ok(staticT`Queued ${chapterLbl}`),
                                                      })
                                                  }
                                                  aria-label={t`Download ${chapterLbl}`}
                                              >
                                                <IconDownload size={17} />
                                              </ActionIcon>
                                            </Tooltip>
                                        )}
                                        {canDownload && c.hasFile && c.number !== null && enabledMappings > 1 && (
                                            <Menu shadow="md" position="bottom-end" withinPortal>
                                              <Menu.Target>
                                                <ActionIcon
                                                    variant="subtle"
                                                    color="gray"
                                                    aria-label={t`More actions for ${chapterLbl}`}
                                                >
                                                  <IconDotsVertical size={17} />
                                                </ActionIcon>
                                              </Menu.Target>
                                              <Menu.Dropdown>
                                                <Menu.Item
                                                    leftSection={<IconPhotoSearch size={14} />}
                                                    onClick={() =>
                                                        setPickChapter({
                                                          id: c.id,
                                                          number: c.number!,
                                                          label: chapterLbl,
                                                          currentSourceName: c.fileSourceName,
                                                        })
                                                    }
                                                >
                                                  <Trans>Find better copy</Trans>
                                                </Menu.Item>
                                              </Menu.Dropdown>
                                            </Menu>
                                        )}
                                      </Group>
                                    </Table.Td>
                                  </Table.Tr>
                              )
                            })}
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
                                title={t`${lineLabel} · click to collapse`}
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
                          <Trans>Chapters {currentPageLabel} · {visibleAllCount} matching</Trans>
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
                              chapterTable?.scrollIntoView({ behavior: 'smooth', block: 'start' })
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
        <Modal
            opened={deleteSeriesModalOpen}
            onClose={() => setDeleteSeriesModalOpen(false)}
            title={t`Remove series?`}
            centered
        >
          <Stack gap="md">
            <Text size="sm" c="var(--ink-3)">
              <Trans>This removes "{seriesTitle}" and its chapters from Maki.</Trans>
            </Text>
            <Checkbox
                label={t`Also delete files on disk`}
                checked={deleteSeriesFiles}
                onChange={(e) => setDeleteSeriesFiles(e.currentTarget.checked)}
            />
            <Text size="sm" c="red">
              <Trans>This action cannot be undone.</Trans>
            </Text>
            <Group justify="flex-end">
              <Button variant="default" onClick={() => setDeleteSeriesModalOpen(false)}>
                <Trans>Cancel</Trans>
              </Button>
              <Button
                  color="red"
                  leftSection={<IconTrash size={16} />}
                  loading={deleteSeries.isPending}
                  onClick={() =>
                      deleteSeries.mutate(
                          { id: series.id, deleteFiles: deleteSeriesFiles },
                          {
                            onSuccess: () => {
                              notify.ok(staticT`Series removed`)
                              navigate('/library')
                            },
                          },
                      )
                  }
              >
                <Trans>Remove</Trans>
              </Button>
            </Group>
          </Stack>
        </Modal>

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
      </Tabs>
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
  return (
      <Stack gap={1}>
        <Text size="sm" fw={650} className="tnum">
          <Trans>About {readingTime}</Trans>
        </Text>
        <Text size="xs" c="var(--ink-3)">
          {style === 'scrolling' ? (
              seriesSpecific ? (
                  <Trans>
                    Scrolling pace · {remainingChapters} chapters left · based on {sampleChapters} chapters from
                    this series
                  </Trans>
              ) : (
                  <Trans>
                    Scrolling pace · {remainingChapters} chapters left · based on {sampleChapters} similar reads
                  </Trans>
              )
          ) : seriesSpecific ? (
              <Trans>
                Paged pace · {remainingChapters} chapters left · based on {sampleChapters} chapters from this
                series
              </Trans>
          ) : (
              <Trans>
                Paged pace · {remainingChapters} chapters left · based on {sampleChapters} similar reads
              </Trans>
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
            <span key={value}>
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
