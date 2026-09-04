import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type RefObject } from 'react'
import type { ReactNode } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import {
  ActionIcon,
  Alert,
  Anchor,
  Badge,
  Box,
  Button,
  Center,
  Checkbox,
  Divider,
  Group,
  Loader,
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
  IconEyeOff,
} from '@tabler/icons-react'
import { useMediaQuery } from '@mantine/hooks'
import { notifications } from '@mantine/notifications'
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import {
  useChapters,
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
  useQueue,
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
import type { ChapterDto } from '../api/types'
import { useAuth } from '../auth/AuthProvider'
import { LinkChaptersModal } from '../components/LinkChaptersModal'
import { MetadataLinks } from '../components/MetadataLinks'
import { RelatedSeriesSection } from '../components/RelatedSeriesSection'
import { SimilarSeriesSection } from '../components/SimilarSeriesSection'
import { ReleaseSearchModal } from '../components/ReleaseSearchModal'
import { RenameSeriesModal } from '../components/RenameSeriesModal'
import { RequestForm } from '../components/RequestForm'
import { SeriesActionsMenu } from '../components/series/SeriesActionsMenu'
import { SeriesHero } from '../components/series/SeriesHero'
import { SeriesFilesSection } from '../components/SeriesFilesSection'
import { SeriesTagsEditor } from '../components/SeriesTagsEditor'
import { SeriesScrobbleSection } from '../components/SeriesScrobbleSection'
import { SourceMappingsSection } from '../components/SourceMappingsSection'
import {
  contentRatingVisual,
  queueStatusVisual,
  seriesProgressVisual,
  seriesStatusVisual,
} from '../components/ui/status'

function chapterLabel(c: ChapterDto): string {
  if (c.isOneShot || c.number === null) return c.title ?? 'One-shot'
  // Prefer the volume the backing file actually is; fall back to metadata volume.
  const volNum = c.fileVolume ?? (c.volume !== null ? String(c.volume) : null)
  const vol = volNum !== null ? `Vol.${volNum} ` : ''
  return `${vol}Ch.${c.number}`
}

/** A special is a decimal-numbered chapter (10.5 omake etc.). */
const isSpecial = (c: ChapterDto) => c.number !== null && c.number % 1 !== 0

const TABS = ['details', 'chapters', 'files'] as const
type Tab = (typeof TABS)[number]

/** How many provider tags show before the rest go behind a toggle. */
/** Tags shown when there is no second column to measure against (stacked, below `md`). */
const PROVIDER_TAG_LIMIT = 14
/** Never fewer than this while fitting, however short the right column is. */
const PROVIDER_TAG_MIN = 10

const DESKTOP_CHAPTER_PAGE_SIZE = 75
const MOBILE_CHAPTER_PAGE_SIZE = 30
const LARGE_WANTED_DOWNLOAD_THRESHOLD = 50

type AnimeMarker = { label: string; kind: 'start' | 'end' }

/**
 * AnimeStart/AnimeEnd are free-text from MangaBaka, e.g.
 * "Vol 1, Chap 1 (S1) / Vol 31, Chap 270 (Film + OVA) / Vol 35, Chap 315 (S2)". Matches every
 * "Chap N ... (label)" run anywhere in the string rather than splitting on " / " first, so it
 * also survives entries with no chapter anchor at all ("Alternate Setting with an original
 * ending") and trailing notes glued onto the last segment ("... (Shippuden) Chap 239-244 adapted
 * in EP 119-120"): neither of those has a "Chap N (" to match, so they're silently skipped.
 */
function parseAnimeMarkers(text: string | null | undefined, kind: 'start' | 'end'): Map<number, AnimeMarker[]> {
  const map = new Map<number, AnimeMarker[]>()
  if (!text) return map
  const re = /Chap\s*(\d+(?:\.\d+)?)[^()/]*\(([^)]+)\)/gi
  const reOnce = /Chap\s*(\d+(?:\.\d+)?)[^()/]*/gi
  let match: RegExpExecArray | null
  while ((match = re.exec(text))) {
    const chapterNum = parseFloat(match[1])
    const label = match[2].trim()
    const list = map.get(chapterNum) ?? []
    list.push({ label, kind })
    map.set(chapterNum, list)
  }
  // If no "(label)" was found, fall back to the first chapter number found and give it a default label.
  if (map.size === 0 && (match = reOnce.exec(text))) {
    const chapterNum = parseFloat(match[1])
    const label = "S1"
    const list = map.get(chapterNum) ?? []
    list.push({ label, kind })
    map.set(chapterNum, list)
  }
  return map
}

function mergeAnimeMarkers(
  start: string | null | undefined,
  end: string | null | undefined,
): Map<number, AnimeMarker[]> {
  const combined = new Map<number, AnimeMarker[]>()
  for (const source of [parseAnimeMarkers(start, 'start'), parseAnimeMarkers(end, 'end')]) {
    for (const [num, list] of source) {
      combined.set(num, [...(combined.get(num) ?? []), ...list])
    }
  }
  return combined
}

/** Widest the stacked stripe in the Chapter cell gets. Beyond this a lane draws no line. */
const MAX_SPAN_LANES = 3


type AnimeSpan = {
  key: string
  label: string
  from: number
  /** Inclusive. A season still airing has no end marker and runs to the last known chapter. */
  to: number
  /** True when `to` was inferred rather than read off an end marker. */
  openEnded: boolean
  lane: number
}

type RenderedRow =
  | { kind: 'chapter'; chapter: ChapterDto }
  | { kind: 'span'; span: AnimeSpan; rows: ChapterDto[] }

/**
 * Pairs the point markers into ranges, so a season reads as a run of chapters rather than as two
 * badges 270 rows apart.
 *
 * MangaBaka writes the same label on both sides in practice ("Chap 1 (S1)" in AnimeStart, "Chap
 * 270 (S1)" in AnimeEnd), so labels are matched first; a start whose label matches nothing falls
 * back to the next unconsumed end after it, which is what an entry with mismatched or missing
 * labels degrades to. A start with no end at all is a currently-airing season and runs to the last
 * chapter known. An end with no start is left alone — it stays the point badge it already was.
 */
function buildAnimeSpans(
  markers: Map<number, AnimeMarker[]>,
  lastChapterNumber: number,
): AnimeSpan[] {
  const starts: { num: number; label: string }[] = []
  const ends: { num: number; label: string; used: boolean }[] = []
  for (const [num, list] of markers) {
    for (const m of list) {
      if (m.kind === 'start') starts.push({ num, label: m.label })
      else ends.push({ num, label: m.label, used: false })
    }
  }
  starts.sort((a, b) => a.num - b.num)
  ends.sort((a, b) => a.num - b.num)

  const spans: AnimeSpan[] = []
  for (const start of starts) {
    const norm = start.label.trim().toLowerCase()
    const end =
      ends.find((e) => !e.used && e.num >= start.num && e.label.trim().toLowerCase() === norm) ??
      ends.find((e) => !e.used && e.num >= start.num)
    if (end) end.used = true

    const to = end?.num ?? lastChapterNumber
    // A start past the last known chapter, or an end that lands on the start, spans nothing worth
    // drawing a line for.
    if (to <= start.num) continue

    spans.push({
      key: `${start.label}:${start.num}-${to}`,
      label: start.label,
      from: start.num,
      to,
      openEnded: end === undefined,
      lane: 0,
    })
  }

  // Lanes: an enclosing span takes the outer one, so a film sitting inside a season's range draws
  // beside it rather than on top of it. Anything past the cap keeps its point badges and no line.
  spans.sort((a, b) => a.from - b.from || b.to - a.to)
  const laneEnds: number[] = []
  const laid: AnimeSpan[] = []
  for (const span of spans) {
    let lane = laneEnds.findIndex((end) => end < span.from)
    if (lane === -1) {
      if (laneEnds.length >= MAX_SPAN_LANES) continue
      lane = laneEnds.length
    }
    laneEnds[lane] = span.to
    laid.push({ ...span, lane })
  }
  return laid
}

/** "Ch.1–270", or "Ch.315+" for a season with no end marker yet. */
const spanRangeLabel = (span: AnimeSpan) =>
  span.openEnded ? `Ch.${span.from}+` : `Ch.${span.from}–${span.to}`

/** Mirrors the monitor-mode Select's own labels, for the toast after a change. */
const MONITOR_MODE_LABELS: Record<string, string> = {
  All: 'all chapters',
  MainOnly: 'main (no specials)',
  Smart: 'smart',
  None: 'none',
}

const chapterFilters: Record<string, (c: ChapterDto) => boolean> = {
  all: () => true,
  wanted: (c) => c.wanted,
  missing: (c) => !c.hasFile,
  downloaded: (c) => c.hasFile,
  specials: isSpecial,
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
  const { data: chapters } = useChapters(seriesId)

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
      return { label: 'Imported', scraped: false, hint: 'Brought in from disk, not downloaded by Maki' }
    }
    if (name.startsWith('torrent:')) {
      const indexer = name.slice('torrent:'.length)
      const hint = releaseName ? `Grabbed from ${indexer}: ${releaseName}` : `Grabbed from ${indexer}`
      return { label: 'Torrent', scraped: false, hint }
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
  const [chapterPage, setChapterPage] = useState(1)
  const [selectMode, setSelectMode] = useState(false)
  const [selected, setSelected] = useState<Set<number>>(new Set())
  const [linkModalOpen, setLinkModalOpen] = useState(false)
  const [deleteChaptersModalOpen, setDeleteChaptersModalOpen] = useState(false)
  const [deleteSeriesModalOpen, setDeleteSeriesModalOpen] = useState(false)
  const [deleteSeriesFiles, setDeleteSeriesFiles] = useState(false)
  const [showAllTags, setShowAllTags] = useState(false)
  // The two columns of the Details split, plus the tag block inside the left one. See
  // useProviderTagFit: the tag list is sized to whatever vertical slack the right column leaves.
  const detailsLeftRef = useRef<HTMLDivElement>(null)
  const detailsRightRef = useRef<HTMLDivElement>(null)
  const tagWrapRef = useRef<HTMLDivElement>(null)
  const tagListRef = useRef<HTMLDivElement>(null)
  const tagFit = useProviderTagFit(
    detailsLeftRef,
    detailsRightRef,
    tagWrapRef,
    tagListRef,
    series?.metadataTags.length ?? 0,
  )

  // Without DownloadChapters the two buttons that queue downloads become one that asks an admin to.
  const { can } = useAuth()
  const canDownload = can('DownloadChapters')
  const createRequest = useCreateSeriesRequest()
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
  }, [chapters, filters, chapterFilter, chapterSearch])

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

  /**
   * How far the linked sources fall short of the chapter count MangaBaka reports.
   *
   * Without this a series reads "41 / 41" once every chapter the sources carry is downloaded,
   * which looks finished, so it's easy to unmonitor a series that's actually missing its tail.
   * The gap is deliberately kept out of the progress fraction: those chapters can't be fetched
   * from the linked sources, so counting them would just make the bar unreachable instead.
   *
   * Compared by highest chapter NUMBER, never the row count: sources list specials and one-shots
   * MangaBaka doesn't count, so a count reads "ahead" (365 rows against a reported 119) on a
   * series that is really three chapters short.
   */
  const sourceGap = useMemo(() => {
    const total = series?.totalChapters
    const numbered = (chapters ?? []).map((c) => c.number).filter((n): n is number => n !== null)
    if (!total || numbered.length === 0) return null

    const highest = Math.max(...numbered)
    if (highest >= total) return null

    return { highest, total, missing: Math.floor(total - highest) }
  }, [series, chapters])

  const animeMarkers = useMemo(
    () => mergeAnimeMarkers(series?.animeStart, series?.animeEnd),
    [series?.animeStart, series?.animeEnd],
  )

  const animeSpans = useMemo(() => {
    const numbered = (chapters ?? []).map((c) => c.number).filter((n): n is number => n !== null)
    if (numbered.length === 0) return [] as AnimeSpan[]
    return buildAnimeSpans(animeMarkers, Math.max(...numbered))
  }, [animeMarkers, chapters])

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
    { key: string; label: string; top: number; height: number; left: number; openEnded: boolean }[]
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

  const chapterPageSize = isMobile ? MOBILE_CHAPTER_PAGE_SIZE : DESKTOP_CHAPTER_PAGE_SIZE
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

        if (numbers.length === 0) return `Items ${index * chapterPageSize + 1}–${index * chapterPageSize + rows.length}`
        const first = Math.min(...numbers)
        const last = Math.max(...numbers)
        return first === last ? String(first) : `${first}–${last}`
      }),
    [renderedRows.rows, chapterPageCount, chapterPageSize],
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
  }, [chapterFilter, chapterSearch, chapterPageSize])

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
      let slot = 0
      for (const group of wrap.querySelectorAll<HTMLElement>('.chapter-span-markers')) {
        slot = Math.max(slot, group.offsetWidth)
      }
      // Reserving the slot widens the Chapter column, which moves the badges the lines are drawn
      // off. `markerSlot` is therefore a dependency of this effect, not just an output of it: the
      // pass that changes it re-runs and re-measures against the settled layout. Without that the
      // lines keep their pre-reflow x and sit over the chapter labels.
      setMarkerSlot((current) => (current === slot ? current : slot))

      const next: typeof spanLines = []
      for (const span of animeSpans) {
        if (foldedSpans.has(span.key)) continue
        const startEl = markerRefs.current.get(`${span.key}:start`)
        if (!startEl) continue

        const startRect = startEl.getBoundingClientRect()
        if (startRect.width === 0) continue

        const endEl = markerRefs.current.get(`${span.key}:end`)
        const top = startRect.bottom - wrapRect.top + 2
        // No end badge means the season is still airing, or a filter hid the chapter it sits on.
        // Either way the run visibly keeps going, so the line does too.
        const bottom = endEl ? endEl.getBoundingClientRect().top - wrapRect.top - 2 : wrapRect.height
        if (bottom - top < 4) continue

        // Centred on the marker's *slot*, not on the badge inside it. Every slot is the width of
        // the series' widest label and centres its badge, so this is one shared x for every season
        // — a badge's own centre would drift with its label's width. Taking it off the slot rather
        // than assuming the badge is centred in it also keeps the anchor right on the rare chapter
        // that carries two markers (one season ending where the next begins).
        const holder = startEl.closest('.chapter-span-markers') ?? startEl
        const holderRect = holder.getBoundingClientRect()

        next.push({
          key: span.key,
          label: span.label,
          top,
          height: bottom - top,
          left: holderRect.left - wrapRect.left + holderRect.width / 2,
          openEnded: !endEl,
        })
      }

      // Same-value bail-out: this runs from a ResizeObserver, and setting state that re-renders
      // into an observed subtree is how those turn into loops.
      setSpanLines((current) =>
        current.length === next.length &&
        current.every((c, i) =>
          c.key === next[i].key && c.top === next[i].top && c.height === next[i].height && c.left === next[i].left,
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
          const verb = state === 'watched' ? 'Marked watched' : state === 'read' ? 'Marked read' : 'Marked unread'
          notify.ok(`${verb}: ${r.updated} chapter(s)`)
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
          notify.ok(wanted ? `Want ${r.updated} chapter(s)` : `No longer want ${r.updated} chapter(s)`),
      },
    )
  }

  /** The single row a folded span collapses to: the range, what's in it, and what to do with it. */
  const renderSpanRow = (span: AnimeSpan, rows: ChapterDto[]) => {
    const downloaded = rows.filter((c) => c.hasFile)
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
                ? `${wantedCount} of ${rows.length} chapters wanted · click to want all`
                : allWanted
                  ? `All ${rows.length} chapters wanted`
                  : `None of the ${rows.length} chapters wanted`
            }
            withArrow
          >
            <Switch
              size="xs"
              checked={allWanted}
              classNames={mixed ? { track: 'chapter-span-wanted-mixed' } : undefined}
              thumbIcon={mixed ? <IconMinus size={10} stroke={3} /> : undefined}
              aria-label={`Wanted for ${span.label}: ${wantedCount} of ${rows.length} chapters`}
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
          <Text size="sm" c="dimmed" className="tnum">
            {rows.length} chapters · {downloaded.length} downloaded
            {watchedCount > 0 && ` · ${watchedCount} watched`}
            {readCount > 0 && ` · ${readCount} read`}
            {/* Spelled out only when the range disagrees with itself: the switch alone can show
                that state but not its size, and hovering for a tooltip is a poor way to find out. */}
            {mixed && ` · ${wantedCount} wanted`}
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
                  <ActionIcon variant="subtle" color="gray" aria-label={`Actions for ${span.label}`}>
                    <IconDotsVertical size={17} />
                  </ActionIcon>
                </Menu.Target>
                <Menu.Dropdown>
                  <Menu.Item
                    leftSection={<IconDeviceTv size={15} />}
                    onClick={() => applyReadState(ids, 'watched')}
                  >
                    Mark watched
                  </Menu.Item>
                  <Menu.Item
                    leftSection={<IconEyeCheck size={15} />}
                    onClick={() => applyReadState(ids, 'read')}
                  >
                    Mark read
                  </Menu.Item>
                  <Menu.Item
                    leftSection={<IconEyeOff size={15} />}
                    onClick={() => applyReadState(ids, 'unread')}
                  >
                    Mark unread
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
                    Select these chapters
                  </Menu.Item>
                </Menu.Dropdown>
              </Menu>
            )}
            <Tooltip label="Expand" withArrow>
              <ActionIcon
                variant="subtle"
                color="gray"
                onClick={() => toggleSpanFold(span.key)}
                aria-label={`Expand ${span.label}`}
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
    [chapters, continueAt?.chapterId]
  )

  if (isLoading) {
    return (
      <Center py={80}>
        <Loader />
      </Center>
    )
  }

  if (!series) {
    return <Text c="red">Series not found.</Text>
  }

  const status = seriesStatusVisual(series.status)
  const contentRating = contentRatingVisual(series.contentRating)
  // Errors are reported globally (see main.tsx); only success needs saying here. `info` is for
  // outcomes that aren't failures but aren't wins either — a download action that found nothing
  // left to queue, which would otherwise report a cheerful "Queued 0".
  const notify = {
    ok: (message: string) => notifications.show({ message, color: 'green' }),
    info: (message: string) => notifications.show({ message, color: 'yellow' }),
  }
  const chapterFilterData = chapters
    ? [
        { value: 'all', label: 'All' },
        { value: 'wanted', label: `Wanted (${chapters.filter(chapterFilters.wanted).length})` },
        { value: 'missing', label: `Missing (${chapters.filter(chapterFilters.missing).length})` },
        { value: 'downloaded', label: `Have (${chapters.filter(chapterFilters.downloaded).length})` },
        ...(readTracking && progress.have > 0
          ? [{ value: 'unread', label: `Unread (${chapters.filter(filters.unread).length})` }]
          : []),
        { value: 'specials', label: `Specials (${chapters.filter(chapterFilters.specials).length})` },
      ]
    : []

  const queueNext = (count: number) => {
    setNextCountOpen(false)
    downloadNext.mutate(
      { seriesId, count },
      {
        onSuccess: (r) =>
          r.queued > 0
            ? notify.ok(`Queued ${r.queued} chapter(s)`)
            : notify.info('Nothing left to queue — every wanted chapter is on disk or already queued'),
      },
    )
  }

  const queueAllWanted = () =>
    searchMissing.mutate(seriesId, {
      onSuccess: (r) => notify.ok(`Queued ${r.queued} missing chapter(s)`),
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
          notify.ok(rating === null ? 'Rating cleared' : `Rated ${rating}/10`),
      },
    )

  return (
    <Tabs
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
                {`${continueAt.page > 0 ? 'Continue reading' : 'Read'}${nextChapter ? ` ${nextChapter}` : ''}`}
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
                    {missingWanted > 0 ? `Download ${missingWanted} wanted` : 'Download all wanted'}
                  </Button>
                  <Menu position="bottom-end" withinPortal>
                    <Menu.Target>
                      <Button
                        variant="default"
                        size="md"
                        radius="md"
                        px={10}
                        aria-label="More download options"
                        disabled={searchMissing.isPending || downloadNext.isPending}
                      >
                        <IconChevronDown size={16} />
                      </Button>
                    </Menu.Target>
                    <Menu.Dropdown>
                      <Menu.Label>Download the next</Menu.Label>
                      {[10, 25].map((n) => (
                        <Menu.Item key={n} onClick={() => queueNext(n)}>
                          Next {n} chapters
                        </Menu.Item>
                      ))}
                      <Menu.Item onClick={() => setNextCountOpen(true)}>Next...</Menu.Item>
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
                  Search releases
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
                Request chapters
              </Button>
            )}
            <SeriesActionsMenu
              monitorMode={series.monitorNewItems}
              incognito={series.incognito}
              notificationMode={series.notificationMode}
              busy={refresh.isPending || refreshMetadata.isPending || rescan.isPending}
              onRefreshChapters={() =>
                refresh.mutate(seriesId, {
                  onSuccess: (r) => notify.ok(`Refreshed, ${r.newChapters} new chapter(s)`),
                })
              }
              onRefreshMetadata={() =>
                refreshMetadata.mutate(seriesId, {
                  onSuccess: () => notify.ok('Metadata and poster refreshed'),
                })
              }
              onRescan={() =>
                rescan.mutate(seriesId, {
                  onSuccess: (r) =>
                    notify.ok(
                      `Rescanned: ${r.newFiles} new, ${r.relinked} relinked, ${r.removed} removed`,
                    ),
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
                    onSuccess: (r) =>
                      notify.ok(`Monitoring: ${MONITOR_MODE_LABELS[r.mode] ?? r.mode}`),
                  },
                )
              }
              onSetIncognito={(mode) =>
                setIncognito.mutate(
                  { seriesId, mode },
                  { onSuccess: (r) => notify.ok(`Incognito: ${r.incognito}`) },
                )
              }
              onSetNotify={(mode) =>
                setNotificationMode.mutate(
                  { seriesId, mode },
                  { onSuccess: (r) => notify.ok(`Notifications: ${r.notificationMode}`) },
                )
              }
              onRemove={() => setDeleteSeriesModalOpen(true)}
            />
          </>
        }
        tabs={
          <Tabs.List>
            <Tabs.Tab value="details">Details</Tabs.Tab>
            <Tabs.Tab value="chapters">
              Chapters
              <span className="series-tab-count tnum">{series.knownChapterCount}</span>
            </Tabs.Tab>
            <Tabs.Tab value="files">
              Files
              <span className="series-tab-count tnum">{series.chapterFileCount}</span>
            </Tabs.Tab>
          </Tabs.List>
        }
      />

      <Tabs.Panel value="details">
        <Stack gap="lg">
          <div className="series-split">
            <Paper withBorder radius="lg" p="lg" ref={detailsLeftRef}>
              <Title order={3} fz={17}>
                Synopsis
              </Title>
              {series.overview ? (
                <Text size="sm" mt="sm" c="var(--ink-3)" style={{ lineHeight: 1.66, maxWidth: '66ch' }}>
                  {series.overview}
                </Text>
              ) : (
                <Text size="sm" mt="sm" c="dimmed">
                  No synopsis yet. Refresh metadata to fetch one.
                </Text>
              )}

              {(series.animeStart || series.animeEnd) && (
                <>
                  <Divider my="md" color="var(--hairline)" />
                  <Title order={4} fz={14} mb={10}>
                    Anime coverage
                  </Title>
                  <Stack gap={7}>
                    {series.animeStart && (
                      <Group gap={12} align="baseline" wrap="nowrap">
                        <Text size="xs" c="var(--ink-4)" w={92} style={{ flexShrink: 0 }}>
                          Aired from
                        </Text>
                        <Text size="sm" c="var(--ink-2)" className="tnum">
                          {series.animeStart}
                        </Text>
                      </Group>
                    )}
                    {series.animeEnd && (
                      <Group gap={12} align="baseline" wrap="nowrap">
                        <Text size="xs" c="var(--ink-4)" w={92} style={{ flexShrink: 0 }}>
                          Aired until
                        </Text>
                        <Text size="sm" c="var(--ink-2)" className="tnum">
                          {series.animeEnd}
                        </Text>
                      </Group>
                    )}
                  </Stack>
                </>
              )}

              <Divider my="md" color="var(--hairline)" />

              <Stack gap="md">
                {series.genres.length > 0 && (
                  <div>
                    <Text size="xs" c="var(--ink-4)" mb={9}>
                      Genres
                    </Text>
                    <Group gap={7}>
                      {series.genres.map((g) => (
                        <Badge key={g} variant="default" fw={500}>
                          {g}
                        </Badge>
                      ))}
                    </Group>
                  </div>
                )}
                {series.metadataTags.length > 0 && (
                  <div>
                    <Text size="xs" c="var(--ink-4)" mb={9}>
                      Provider tags
                    </Text>
                    {/* MangaBaka hands back well over a hundred of these on a popular series, so
                        the block is clipped to a whole number of rows rather than shown in full.
                        How many rows is decided by useProviderTagFit, which spends whatever height
                        the right column has spare — the point of the cap was that a wall of tags
                        buries the panel, and a row that fits beside Linked sources buries nothing.
                        "Show more" lifts the clip; every tag is in the DOM either way. */}
                    <div
                      ref={tagWrapRef}
                      style={{
                        overflow: 'hidden',
                        maxHeight: showAllTags ? undefined : (tagFit.height ?? undefined),
                      }}
                    >
                      <Group gap={7} ref={tagListRef}>
                        {series.metadataTags.map((t) => (
                          <Badge key={t} variant="default" color="gray" fw={500}>
                            {t}
                          </Badge>
                        ))}
                      </Group>
                    </div>
                    {tagFit.hidden > 0 && (
                      <Anchor
                        component="button"
                        type="button"
                        size="xs"
                        c="var(--ink-4)"
                        mt={8}
                        display="block"
                        onClick={() => setShowAllTags((v) => !v)}
                      >
                        {showAllTags ? 'Show fewer' : `+${tagFit.hidden} more`}
                      </Anchor>
                    )}
                  </div>
                )}
                <SeriesTagsEditor seriesId={series.id} tagIds={series.tagIds} />
                {series.links.length > 0 && (
                  <div>
                    <Text size="xs" c="var(--ink-4)" mb={9}>
                      Open on
                    </Text>
                    <MetadataLinks links={series.links} />
                  </div>
                )}
              </Stack>
            </Paper>

            <Stack gap="lg" ref={detailsRightRef}>
            <Paper withBorder radius="lg" p="lg">
              <Title order={3} fz={17}>
                Progress
              </Title>

              {readTracking && series.readChapterCount != null && progress.have > 0 && (
                <Box mt="md">
                  <Group gap={9} c="var(--ink-3)">
                    <IconBook size={17} />
                    <Text size="sm" fw={600} c="var(--ink)">
                      Reading
                    </Text>
                  </Group>
                  <Progress
                    mt={12}
                    value={Math.min(100, (series.readChapterCount / progress.have) * 100)}
                    color="brand"
                    radius="xl"
                  />
                  <Group justify="space-between" mt={9}>
                    <Text size="sm" c="var(--ink-2)" className="tnum">
                      {series.readChapterCount} / {progress.have} chapters
                    </Text>
                    <Text size="sm" fw={600} c="var(--ink-2)" className="tnum">
                      {Math.round((series.readChapterCount / progress.have) * 100)}%
                    </Text>
                  </Group>
                  <Divider my="md" color="var(--hairline)" />
                </Box>
              )}

              <Box mt="md">
                <Group gap={9} c="var(--ink-3)">
                  <IconDownload size={17} />
                  <Text size="sm" fw={600} c="var(--ink)">
                    Downloads
                  </Text>
                </Group>
                <Progress
                  mt={12}
                  value={progress.pct}
                  // Never green while the sources are short of the full run: "all downloaded" and
                  // "you have the whole series" are different claims, and the green tick is exactly
                  // what makes someone unmonitor a series that's still missing its tail.
                  color={sourceGap ? 'yellow' : progress.complete ? 'teal' : 'blue'}
                  radius="xl"
                />
                <Group justify="space-between" mt={9}>
                  <Text size="sm" c="var(--ink-2)" className="tnum">
                    {progress.have} / {progress.total} chapters
                    {progress.nothingWanted && ' listed, none wanted'}
                  </Text>
                  <Text size="sm" fw={600} c="var(--ink-2)" className="tnum">
                    {Math.round(progress.pct)}%
                  </Text>
                </Group>
                {missingWanted > 0 && (
                  <Text size="xs" c="var(--ink-4)" mt={7} className="tnum">
                    {missingWanted} wanted, not fetched
                  </Text>
                )}
              </Box>

              {sourceGap && (
                <Alert
                  mt="md"
                  color="yellow"
                  variant="light"
                  radius="md"
                  icon={<IconAlertTriangle size={16} />}
                >
                  <Text size="xs" c="var(--ink-3)" style={{ lineHeight: 1.55 }}>
                    Your sources only reach chapter{' '}
                    <Text span fw={600} c="var(--ink)" className="tnum">
                      {sourceGap.highest}
                    </Text>
                    , but MangaBaka lists{' '}
                    <Text span fw={600} c="var(--ink)" className="tnum">
                      {sourceGap.total}
                    </Text>
                    . Roughly {sourceGap.missing} chapter{sourceGap.missing === 1 ? '' : 's'} can&apos;t
                    be downloaded from the sources linked here. Link another source to close the gap.
                  </Text>
                </Alert>
              )}
            </Paper>

            {series.numberingClash && (
              <Alert
                color="yellow"
                icon={<IconAlertTriangle size={18} />}
                title="Sources disagree on chapter numbering"
              >
                {(() => {
                  const [sub, whole] = series.numberingClash.split('|')
                  return (
                    <>
                      <Text span fw={600}>
                        {sub}
                      </Text>{' '}
                      lists sub-chapters (1.1, 1.2, …) where{' '}
                      <Text span fw={600}>
                        {whole}
                      </Text>{' '}
                      lists whole chapters for the same content, so both appear as separate rows below.
                      There is no safe automatic merge. Consider disabling one of the two source
                      mappings; the warning clears on the next refresh.
                    </>
                  )
                })()}
              </Alert>
            )}

            <Paper withBorder radius="lg" p="lg">
              <SourceMappingsSection
                seriesId={seriesId}
                seriesTitle={series.title}
                matching={series.sourceMatchPending}
              />
            </Paper>
            </Stack>
          </div>

          <Paper withBorder radius="lg" p="lg">
            <Title order={3} fz={17} mb="sm">
              Metadata
            </Title>
            <div className="series-records">
              {series.originalTitle && (
                <RecordRow label="Original title">{series.originalTitle}</RecordRow>
              )}
              {series.altTitles.length > 0 && (
                <RecordRow label="Alt titles">{series.altTitles.join(', ')}</RecordRow>
              )}
              {series.authorStory && (
                <RecordRow label="Story">
                  <CreatorNames role="author" names={series.authorStory} />
                </RecordRow>
              )}
              {series.authorArt && (
                <RecordRow label="Art">
                  <CreatorNames role="artist" names={series.authorArt} />
                </RecordRow>
              )}
              {series.publisher && (
                <RecordRow label="Publisher">
                  <CreatorNames role="studio" names={series.publisher} />
                </RecordRow>
              )}
              {series.type && <RecordRow label="Type">{series.type}</RecordRow>}
              {series.year && <RecordRow label="Year">{series.year}</RecordRow>}
              <RecordRow label="Status">{status.label}</RecordRow>
              {contentRating && <RecordRow label="Content rating">{contentRating.label}</RecordRow>}
              {series.totalVolumes != null && (
                <RecordRow label="Volumes">{series.totalVolumes}</RecordRow>
              )}
              <RecordRow label="Chapters known">{series.knownChapterCount}</RecordRow>
              {series.rootFolderPath && (
                <RecordRow label="Folder">
                  <Text size="sm" c="var(--ink-3)" ff="monospace" style={{ wordBreak: 'break-all' }}>
                    {series.rootFolderPath}
                  </Text>
                </RecordRow>
              )}
            </div>
          </Paper>

          <SeriesScrobbleSection seriesId={seriesId} />
          <RelatedSeriesSection seriesId={seriesId} />
          <SimilarSeriesSection seriesId={seriesId} />
        </Stack>
      </Tabs.Panel>

      <Modal
        opened={downloadAllConfirmOpen}
        onClose={() => setDownloadAllConfirmOpen(false)}
        title={`Download ${missingWanted} wanted chapters?`}
        centered
      >
        <Stack gap="sm">
          <Text mt="sm" size="sm">
            This will add {missingWanted} chapters to the download queue.
          </Text>
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setDownloadAllConfirmOpen(false)}>
              Cancel
            </Button>
            <Button
              loading={searchMissing.isPending}
              onClick={() => {
                setDownloadAllConfirmOpen(false)
                queueAllWanted()
              }}
            >
              Download {missingWanted} chapters
            </Button>
          </Group>
        </Stack>
      </Modal>

      <Modal
        opened={nextCountOpen}
        onClose={() => setNextCountOpen(false)}
        title="Download next chapters"
        centered
      >
        <Stack gap="sm">
          <NumberInput
            label="How many"
            description={`${missingWanted} wanted chapter(s) are missing`}
            min={1}
            value={nextCount}
            onChange={setNextCount}
            data-autofocus
          />
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setNextCountOpen(false)}>
              Cancel
            </Button>
            <Button
              loading={downloadNext.isPending}
              onClick={() => queueNext(Math.max(1, Number(nextCount) || 1))}
            >
              Download
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

      <Modal opened={moveModalOpen} onClose={() => setMoveModalOpen(false)} title="Move series" centered>
        <Stack gap="md">
          <Text size="sm" c="dimmed">
            Re-triggers a Kavita scan of both locations either way. Blocked while a download for
            this series is in flight, unless Maki isn't touching the files itself.
          </Text>
          <Select
            label="Destination root folder"
            placeholder="Pick a root folder"
            data={(rootFolders ?? [])
              .filter((f) => f.id !== series.rootFolderId)
              .map((f) => ({ value: String(f.id), label: f.path }))}
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
                label="Just point the series at the new root folder - I already moved the files"
              />
            </Stack>
          </Radio.Group>
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setMoveModalOpen(false)}>
              Cancel
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
                      notify.ok('Series moved')
                      setMoveModalOpen(false)
                    },
                  },
                )
              }
            >
              Move
            </Button>
          </Group>
        </Stack>
      </Modal>


      <Tabs.Panel value="chapters">
        <Stack gap="lg">
        {/* Chapters */}
        <Group justify="space-between" wrap="wrap" gap="sm">
          <Group gap="xs" align="baseline">
            <Title order={3}>Chapters</Title>
            {chapters && (
              <Text size="sm" c="dimmed" className="tnum">
                {progress.have}/{progress.total}
              </Text>
            )}
            {chapters && readTracking && progress.have > 0 && (
              <Badge size="sm" variant="light" color="teal" className="tnum">
                {chapters.filter(filters.read).length} read
              </Badge>
            )}
          </Group>
          {chapters && chapters.length > 0 && (
            <Group gap="xs" wrap="wrap">
              {isMobile ? (
                <Select
                  size="xs"
                  aria-label="Filter chapters"
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
              {!selectMode && (
                <Button
                  size="xs"
                  variant="default"
                  leftSection={<IconListCheck size={14} />}
                  onClick={() => setSelectMode(true)}
                >
                  Select
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
                  aria-label="Clear chapter search"
                  onClick={() => setChapterSearch('')}
                >
                  <IconX size={14} />
                </ActionIcon>
              ) : null
            }
            placeholder="Search by chapter number or title"
            aria-label="Search chapters"
          />
        )}

        {selectMode && (
          <Paper withBorder p="xs" radius="lg">
            <Group justify="space-between" wrap="wrap" gap="xs">
              <Group gap="xs">
                <Text size="sm" c="dimmed" className="tnum">
                  {selected.size} selected
                </Text>
                <Menu shadow="md" position="bottom-start" withinPortal>
                  <Menu.Target>
                    <Button size="xs" variant="subtle" rightSection={<IconChevronDown size={14} />}>
                      Select all
                    </Button>
                  </Menu.Target>
                  <Menu.Dropdown>
                    {/* Every item works over the rows the filter is showing, same as a shift-range,
                        so "Specials" under the Missing filter means the specials you can see. */}
                    <Menu.Item
                      className="tnum"
                      onClick={() => selectAll(() => true)}
                    >
                      All ({visibleChapters.length})
                    </Menu.Item>
                    <Menu.Item
                      className="tnum"
                      disabled={visibleMain.length === 0}
                      onClick={() => selectAll((c) => !isSpecial(c))}
                    >
                      Main ({visibleMain.length})
                    </Menu.Item>
                    <Menu.Item
                      className="tnum"
                      disabled={visibleSpecials.length === 0}
                      onClick={() => selectAll(isSpecial)}
                    >
                      Specials ({visibleSpecials.length})
                    </Menu.Item>
                    <Menu.Divider />
                    <Menu.Item
                      disabled={selected.size === 0}
                      onClick={() => {
                        selectAnchor.current = null
                        setSelected(new Set())
                      }}
                    >
                      Clear
                    </Menu.Item>
                  </Menu.Dropdown>
                </Menu>
                <Text size="xs" c="dimmed" visibleFrom="sm">
                  Click a row to select, shift-click for a range
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
                            ? notify.ok(`Queued ${r.queued} chapter(s)`)
                            : notify.info(r.error ?? 'Nothing to queue — those chapters are already on disk'),
                      })
                    }
                  >
                    Download
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
                  Want
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
                  Don't want
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
                      Mark watched
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
                      Mark read
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
                      Mark unread
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
                  Link to file
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
                        notify.ok(`Unlinked ${r.unlinked} chapter(s)`)
                        exitSelectMode()
                      },
                    })
                  }
                >
                  Unlink
                </Button>
                <Button
                  size="xs"
                  variant="light"
                  color="red"
                  leftSection={<IconTrash size={15} />}
                  disabled={selected.size === 0}
                  onClick={() => setDeleteChaptersModalOpen(true)}
                >
                  Delete
                </Button>
                <Button
                  size="xs"
                  variant="default"
                  leftSection={<IconX size={15} />}
                  onClick={exitSelectMode}
                >
                  Done
                </Button>
              </Group>
            </Group>
          </Paper>
        )}

        <Modal
          opened={deleteSeriesModalOpen}
          onClose={() => setDeleteSeriesModalOpen(false)}
          title="Remove series?"
          centered
        >
          <Stack gap="md">
            <Text size="sm" c="dimmed">
              This removes "{series.title}" and its chapters from Maki.
            </Text>
            <Checkbox
              label="Also delete files on disk"
              checked={deleteSeriesFiles}
              onChange={(e) => setDeleteSeriesFiles(e.currentTarget.checked)}
            />
            <Text size="sm" c="red">
              This action cannot be undone.
            </Text>
            <Group justify="flex-end">
              <Button variant="default" onClick={() => setDeleteSeriesModalOpen(false)}>
                Cancel
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
                        notify.ok('Series removed')
                        navigate('/library')
                      },
                    },
                  )
                }
              >
                Remove
              </Button>
            </Group>
          </Stack>
        </Modal>

        <Modal
          opened={deleteChaptersModalOpen}
          onClose={() => setDeleteChaptersModalOpen(false)}
          title="Delete chapters?"
          centered
        >
          <Stack gap="md">
            <Text size="sm" c="dimmed">
              This permanently removes {selected.size} chapter row(s), not just their file link,
              along with any backing CBZ file on disk. Use this to clean up chapters pulled in by a
              wrong source match. Fix or remove the source mapping first, or a refresh will bring
              them right back.
            </Text>
            <Text size="sm" c="red">
              This action cannot be undone.
            </Text>
            <Group justify="flex-end">
              <Button variant="default" onClick={() => setDeleteChaptersModalOpen(false)}>
                Cancel
              </Button>
              <Button
                color="red"
                leftSection={<IconTrash size={16} />}
                loading={deleteChapters.isPending}
                onClick={() =>
                  deleteChapters.mutate([...selected], {
                    onSuccess: (r) => {
                      notify.ok(`Deleted ${r.deleted} chapter(s)`)
                      setDeleteChaptersModalOpen(false)
                      exitSelectMode()
                    },
                  })
                }
              >
                Delete
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
          <Text c="dimmed" size="sm">
            No chapters known. Link a source and refresh.
          </Text>
        ) : renderedRows.rows.length === 0 ? (
          <Text c="dimmed" size="sm">
            No chapters match this search and filter.
          </Text>
        ) : (
          <Stack gap="sm">
          <Paper withBorder radius="lg" style={{ overflow: 'hidden' }}>
          <Box
            pos="relative"
            ref={setChapterTable}
            style={{ '--chapter-marker-slot': `${markerSlot}px` } as React.CSSProperties}
          >
          <Table.ScrollContainer minWidth={isMobile ? 0 : 670}>
            <Table className="chapter-table" highlightOnHover verticalSpacing="xs">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th w={52}>Wanted</Table.Th>
                  <Table.Th w={170}>Chapter</Table.Th>
                  <Table.Th>Title</Table.Th>
                  <Table.Th w={120}>Released</Table.Th>
                  <Table.Th w={110}>Source</Table.Th>
                  <Table.Th w={240}>Status</Table.Th>
                  <Table.Th w={92} />
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {pagedRows.map((row) => {
                  if (row.kind === 'span') {
                    return renderSpanRow(row.span, row.rows)
                  }
                  const c = row.chapter
                  const { read, inProgress, external, watched } = readStateFor(c)
                  const rowProgress = readProgress.get(c.id)
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
                        aria-label={`Want ${chapterLabel(c)}`}
                        onChange={(e) =>
                          toggleWanted.mutate({ chapterId: c.id, wanted: e.currentTarget.checked })
                        }
                      />
                    </Table.Td>
                    {/* The marker slot is reserved on every row, not just the ones carrying a badge:
                        the season lines run down that slot, and a label free to grow into it on the
                        other 200 rows would have the line drawn straight through it. */}
                    <Table.Td className={hasAnimeMarkers ? 'chapter-cell' : undefined}>
                      <Group gap={6} wrap="nowrap">
                        {c.fileVolume !== null && !c.isOneShot && c.number !== null && (
                          <Tooltip label="Contained in a volume/compilation file" withArrow>
                            <Badge size="sm" color="indigo" variant="light" className="tnum">
                              Vol.{c.fileVolume}
                            </Badge>
                          </Tooltip>
                        )}
                        <Text size="sm" fw={550} className="tnum">
                          {c.isOneShot || c.number === null
                            ? chapterLabel(c)
                            : c.fileVolume !== null
                              ? `Ch.${c.number}`
                              : chapterLabel(c)}
                        </Text>
                      </Group>
                      {c.number !== null && (animeMarkers.get(c.number) ?? []).length > 0 && (
                        <div className="chapter-span-markers">
                          {(animeMarkers.get(c.number) ?? []).map((marker, i) => {
                            // A marker that starts or ends an included span doubles as its fold
                            // control and as the anchor the overlay measures its line from;
                            // everything else (an unpaired end, or one bumped by the 3-lane cap) is
                            // a plain informational badge, same as always.
                            const span = spanForMarker(c.number!, marker.kind)
                            return (
                              <Tooltip
                                key={i}
                                label={
                                  marker.label +
                                  (marker.kind === 'start' ? ' Anime adaptation starts here' : ' Anime adaptation ends here') +
                                  (span ? ` · click to ${foldedSpans.has(span.key) ? 'expand' : 'collapse'}` : '')
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
                      <Text size="sm" c="dimmed" lineClamp={1}>
                        {c.title}
                      </Text>
                    </Table.Td>
                    <Table.Td>
                      <Text size="sm" c="dimmed" className="tnum">
                        {c.releaseDate ? new Date(c.releaseDate).toLocaleDateString() : '-'}
                      </Text>
                    </Table.Td>
                    <Table.Td>
                      {/* Where the file on disk actually came from, which is what makes a source
                          comparison actionable: the winner is often not what you already have. */}
                      {!c.hasFile || !c.fileSourceName ? (
                        <Text size="sm" c="dimmed">
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
                            return (
                              <Tooltip label={queueItem.errorMessage || visual.label} withArrow disabled={!queueItem.errorMessage}>
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
                                      ? `${visual.label} ${queueItem.pagesDone}/${queueItem.pagesTotal}`
                                      : visual.label}
                                  </Badge>
                                </Group>
                              </Tooltip>
                            )
                          })()
                        ) : c.hasFile ? (
                          <Badge size="sm" color="teal" variant="light" leftSection={<IconCircleCheck size={12} />}>
                            Downloaded
                          </Badge>
                        ) : (
                          <Badge size="sm" color="gray" variant="light">
                            Missing
                          </Badge>
                        )}
                        {read && (
                          <Tooltip
                            label={
                              watched
                                ? "Marked watched, not read. Doesn't count toward reading stats"
                                : external
                                  ? 'Read in Kavita'
                                  : 'Read in Maki'
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
                              {watched ? 'Watched' : 'Read'}
                            </Badge>
                          </Tooltip>
                        )}
                        {/* Shown alongside Read when a finished chapter is being re-read. */}
                        {inProgress && (
                          <Badge size="sm" color="blue" variant="light" className="tnum">
                              {/* pageCount is 0 on rows imported from Kavita: the reader fills it
                                  in on first open, so show a plain label until then. */}
                              {rowProgress && rowProgress.pageCount > 0
                                ? `Page ${rowProgress.pageIndex + 1}/${rowProgress.pageCount}`
                                : 'Reading'}
                            </Badge>
                        )}
                      </Group>
                    </Table.Td>
                    <Table.Td onClick={(e) => e.stopPropagation()}>
                      <Group gap={2} wrap="nowrap" justify="flex-end">
                        {c.hasFile && (
                          <>
                            <Tooltip label={read ? 'Mark unread' : 'Mark read'} withArrow>
                              <ActionIcon
                                variant={read ? 'light' : 'subtle'}
                                color={read ? 'teal' : 'gray'}
                                onClick={() => setRead.mutate({ chapterId: c.id, read: !read })}
                                aria-label={`Toggle read state of ${chapterLabel(c)}`}
                              >
                                {!read ? <IconEye size={17} /> : <IconEyeOff size={17} />}
                              </ActionIcon>
                            </Tooltip>
                            <Tooltip label="Read" withArrow>
                              <ActionIcon
                                component={Link}
                                to={`/read/${c.id}`}
                                variant="subtle"
                                color="brand"
                                aria-label={`Read ${chapterLabel(c)}`}
                              >
                                <IconBook size={17} />
                              </ActionIcon>
                            </Tooltip>
                          </>
                        )}
                        {!c.hasFile && canDownload && (
                          <Tooltip label="Download this chapter" withArrow>
                            <ActionIcon
                              variant="subtle"
                              color="brand"
                              onClick={() =>
                                search.mutate(c.id, {
                                  onSuccess: () => notify.ok(`Queued ${chapterLabel(c)}`),
                                })
                              }
                              aria-label={`Download ${chapterLabel(c)}`}
                            >
                              <IconDownload size={17} />
                            </ActionIcon>
                          </Tooltip>
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
            {spanLines.map((line) => (
              <button
                key={line.key}
                type="button"
                className="chapter-span-line"
                style={{ top: line.top, height: line.height, left: line.left }}
                data-open-ended={line.openEnded ? '' : undefined}
                title={`${line.label} · click to collapse`}
                aria-label={`Collapse ${line.label}`}
                onClick={() => toggleSpanFold(line.key)}
              />
            ))}
          </div>
          </Box>
          </Paper>
          {chapterPageCount > 1 && (
            <Group justify="space-between" gap="xs" wrap="wrap">
              <Text size="xs" c="dimmed" className="tnum">
                Chapters {chapterPageLabels[currentChapterPage - 1]} · {visibleChapters.length} matching
              </Text>
              <Pagination
                size="sm"
                value={currentChapterPage}
                total={chapterPageCount}
                siblings={isMobile ? 0 : 1}
                boundaries={1}
                getItemProps={(page) => ({
                  children: chapterPageLabels[page - 1],
                  'aria-label': `Chapters ${chapterPageLabels[page - 1]}`,
                })}
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
        <SeriesFilesSection seriesId={seriesId} />
      </Tabs.Panel>

      <Modal
        opened={requestModalOpen}
        onClose={() => setRequestModalOpen(false)}
        title={`Request chapters of ${series.title}`}
      >
        <RequestForm
          chapterStart={requestStart}
          chapterEnd={requestEnd}
          note={requestNote}
          onChapterStart={setRequestStart}
          onChapterEnd={setRequestEnd}
          onNote={setRequestNote}
          pending={createRequest.isPending}
          label="Send request"
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
                  notify.ok('Requested, an admin will see it on the Requests page')
                },
              },
            )
          }
        />
      </Modal>
    </Tabs>
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

/**
 * How much of the provider-tag list to show, so the left column ends up at least as tall as the
 * right one.
 *
 * The tag list is the only thing on this page whose length we get to choose, which makes it the
 * natural filler: Progress and Linked sources are as tall as they are, and a short left column
 * next to them leaves a visible notch. So rather than a fixed cap, the list grows into whatever
 * slack the right column leaves and gets trimmed back when it would overhang.
 *
 * Every tag stays in the DOM and the wrapper is clipped to a row boundary, rather than slicing the
 * array. Slicing needs the list rendered in full to know where the rows fall, so it costs a second
 * layout pass on every resize; clipping needs one measurement and then only a style change, and it
 * cannot cut a row in half.
 *
 * `leftWithoutTags` is the invariant the whole thing rests on: clipping the wrapper changes the
 * left column's height, so measuring the column directly would chase its own tail. Subtracting the
 * wrapper's current height gives a figure that does not move when the clip does, which is what
 * makes this settle in one pass instead of oscillating.
 */
function useProviderTagFit(
  leftRef: RefObject<HTMLElement | null>,
  rightRef: RefObject<HTMLElement | null>,
  wrapRef: RefObject<HTMLElement | null>,
  listRef: RefObject<HTMLElement | null>,
  tagCount: number,
) {
  const [fit, setFit] = useState<{ height: number | null; hidden: number }>({
    height: null,
    hidden: 0,
  })

  useLayoutEffect(() => {
    const left = leftRef.current
    const right = rightRef.current
    const wrap = wrapRef.current
    const list = listRef.current
    if (!left || !right || !wrap || !list || tagCount === 0) return

    const measure = () => {
      const chips = Array.from(list.children) as HTMLElement[]
      if (chips.length === 0) return

      // Same grid row means side by side; the right column starting lower means the split has
      // collapsed and there is nothing to match. Reading the layout beats duplicating the
      // breakpoint, which would then have to be kept in step with the stylesheet.
      const stacked = right.offsetTop > left.offsetTop
      const floor = stacked ? PROVIDER_TAG_LIMIT : PROVIDER_TAG_MIN
      const available = stacked ? 0 : right.offsetHeight - (left.offsetHeight - wrap.offsetHeight)

      // Rects, not offsetTop. offsetTop is measured from the nearest *positioned* ancestor, and
      // the wrapper is not one, so every chip reported its distance from somewhere far up the
      // panel: `bottom` came out in the hundreds against an `available` of a few dozen, the loop
      // always stopped at the floor, and the maxHeight it then set was larger than the whole tag
      // block. Nothing was clipped, yet the count still claimed rows were hidden.
      const listTop = list.getBoundingClientRect().top
      let height = 0
      let visible = 0
      for (let i = 0; i < chips.length; i++) {
        const bottom = chips[i].getBoundingClientRect().bottom - listTop
        if (i >= floor && bottom > available) break
        height = bottom
        visible = i + 1
      }

      const next =
        visible >= chips.length
          ? { height: null, hidden: 0 }
          : { height, hidden: chips.length - visible }
      // Bail when nothing moved: this runs from a ResizeObserver that the clip itself trips.
      setFit((prev) => (prev.height === next.height && prev.hidden === next.hidden ? prev : next))
    }

    measure()

    // Linked sources arrives async and the synopsis reflows on width, so both columns move after
    // first paint. Observing the list too catches a wrap change that shifts every row boundary.
    const observer = new ResizeObserver(measure)
    observer.observe(left)
    observer.observe(right)
    observer.observe(list)
    return () => observer.disconnect()
  }, [leftRef, rightRef, wrapRef, listRef, tagCount])

  return fit
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
