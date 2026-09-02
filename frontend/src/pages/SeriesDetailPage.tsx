import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
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
  Flex,
  Group,
  Loader,
  Menu,
  Modal,
  Paper,
  Progress,
  Radio,
  Rating,
  SegmentedControl,
  Select,
  Stack,
  Switch,
  Table,
  Text,
  Title,
  Tooltip,
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
  IconFileText,
  IconFolderSymlink,
  IconLink,
  IconLinkOff,
  IconListCheck,
  IconPhoto,
  IconRefresh,
  IconScan,
  IconSearch,
  IconSend,
  IconTrash,
  IconX,
  IconDeviceTv,
  IconDotsVertical,
  IconEyeOff,
  IconBell
} from '@tabler/icons-react'
import { notifications } from '@mantine/notifications'
import { Link, useNavigate, useParams } from 'react-router-dom'
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
  useSearchMissing,
  useSeriesDetail,
  useSetChaptersMonitored,
  useSetIncognito,
  useSetSeriesNotificationMode,
  useSetMonitorMode,
  useSetRating,
  useToggleChapterMonitor,
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
import { SeriesFilesSection } from '../components/SeriesFilesSection'
import { SeriesTagsEditor } from '../components/SeriesTagsEditor'
import { SeriesScrobbleSection } from '../components/SeriesScrobbleSection'
import { SourceMappingsSection } from '../components/SourceMappingsSection'
import { contentRatingVisual, queueStatusVisual, seriesStatusVisual } from '../components/ui/status'
import { INCOGNITO_OPTIONS } from '../components/ui/incognito'
import { SERIES_NOTIFICATION_OPTIONS } from '../components/ui/seriesNotifications'

function chapterLabel(c: ChapterDto): string {
  if (c.isOneShot || c.number === null) return c.title ?? 'One-shot'
  // Prefer the volume the backing file actually is; fall back to metadata volume.
  const volNum = c.fileVolume ?? (c.volume !== null ? String(c.volume) : null)
  const vol = volNum !== null ? `Vol.${volNum} ` : ''
  return `${vol}Ch.${c.number}`
}

/** A special is a decimal-numbered chapter (10.5 omake etc.). */
const isSpecial = (c: ChapterDto) => c.number !== null && c.number % 1 !== 0

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

const chapterFilters: Record<string, (c: ChapterDto) => boolean> = {
  all: () => true,
  monitored: (c) => c.monitored,
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
  const toggleMonitor = useToggleChapterMonitor()
  const searchMissing = useSearchMissing()
  const setMonitorMode = useSetMonitorMode()
  const setIncognito = useSetIncognito()
  const setNotificationMode = useSetSeriesNotificationMode()
  const setRating = useSetRating()
  const unlinkChapters = useUnlinkChapters()
  const setChaptersMonitored = useSetChaptersMonitored()
  const deleteChapters = useDeleteChapters()
  const [releaseModalOpen, setReleaseModalOpen] = useState(false)
  const [chapterFilter, setChapterFilter] = useState('all')
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
  const visibleChapters = useMemo(
    () => (chapters ?? []).filter(filters[chapterFilter] ?? filters.all),
    [chapters, filters, chapterFilter],
  )

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

  const progress = useMemo(() => {
    const list = chapters ?? []
    const have = list.filter((c) => c.hasFile).length
    const monitored = list.filter((c) => c.monitored || c.hasFile).length
    // With nothing monitored and nothing downloaded this would read "0 / 0" while the Chapters
    // tab below lists every known chapter as missing. Show what's known instead, and don't let
    // the bar imply progress against a total the user isn't actually tracking.
    const unmonitored = monitored === 0 && list.length > 0
    const tracked = monitored || list.length
    return {
      have,
      tracked,
      unmonitored,
      pct: !unmonitored && tracked > 0 ? (have / tracked) * 100 : 0,
    }
  }, [chapters])

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

  const chapterTableRef = useRef<HTMLDivElement>(null)
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

  /** One entry per rendered row; a folded span is a single step carrying every chapter it hides. */
  const rangeUnits = useMemo(
    () =>
      renderedRows.rows.map((r) =>
        r.kind === 'chapter' ? { ids: [r.chapter.id] } : { ids: r.rows.map((c) => c.id) },
      ),
    [renderedRows],
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
    const wrap = chapterTableRef.current
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
  }, [animeSpans, foldedSpans, renderedRows, markerSlot])

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

  /** The single row a folded span collapses to: the range, what's in it, and what to do with it. */
  const renderSpanRow = (span: AnimeSpan, rows: ChapterDto[]) => {
    const downloaded = rows.filter((c) => c.hasFile)
    const states = downloaded.map(readStateFor)
    const watchedCount = states.filter((st) => st.watched).length
    const readCount = states.filter((st) => st.read && !st.watched).length
    const done = watchedCount + readCount
    const ids = rows.map((c) => c.id)

    return (
      <Table.Tr key={`span:${span.key}`} className="chapter-span-row">
        <Table.Td />
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
  // Errors are reported globally (see main.tsx); only success needs saying here.
  const notify = {
    ok: (message: string) => notifications.show({ message, color: 'green' }),
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
    <Stack gap="lg">
      <Anchor component={Link} to="/library" c="dimmed" size="sm" w="fit-content">
        <Group gap={4} wrap="nowrap">
          <IconArrowLeft size={15} />
          Library
        </Group>
      </Anchor>

      {/* Hero */}
      <Box className="detail-hero">
        {series.coverUrl && (
          <div
            className="detail-hero-backdrop"
            style={{ backgroundImage: `url(${series.coverUrl})` }}
          />
        )}
        <div className="detail-hero-veil" />
        {/* Two columns, until there isn't room for two. Below `xs` the cover is hidden anyway, so
            side-by-side leaves a column holding nothing but the Read button while the title and
            every badge wrap inside the ~180px left over. `column-reverse` puts that button under
            the title rather than above it, without moving the cover out of the DOM order that the
            wider layout reads left to right. */}
        <Flex
          align="flex-start"
          direction={{ base: 'column-reverse', xs: 'row' }}
          gap="md"
          p={{ base: 'md', sm: 'xl' }}
          style={{ position: 'relative' }}
        >
          <Stack w={{ base: '100%', xs: 'auto' }}>
            {series.coverUrl && (
            <Box
              visibleFrom="xs"
              style={{
                width: 190,
                flexShrink: 0,
                borderRadius: 12,
                overflow: 'hidden',
                boxShadow: '0 16px 40px -12px rgba(0,0,0,.7)',
                border: '1px solid var(--border)',
              }}
            >
              <img
                src={series.coverUrl}
                alt={series.title}
                style={{ width: '100%', aspectRatio: '2/3', objectFit: 'cover', display: 'block' }}
              />
            </Box>
          )}
          {continueAt && (
          <Button
            component={Link}
            to={`/read/${continueAt.chapterId}`}
            leftSection={<IconBook size={16} />}
          >
            {continueAt.page > 0 ? 'Continue reading' : 'Read'} {nextChapter}
          </Button>
        )}
          </Stack>
          <Stack gap="sm" style={{ flex: 1, minWidth: 0 }}>
            <div>
              <Title order={1}>{series.title}</Title>
              {series.originalTitle && series.originalTitle !== series.title && (
                <Group gap={6} wrap="nowrap">
                  <Text c="dimmed" size="lg">
                    {series.originalTitle}
                  </Text>
                </Group>
              )}
              <Group gap={6}>
                {series.altTitles.map((t, i) => (
                    <Text key={t} c="dimmed" size="sm">
                      {t}{i < series.altTitles.length - 1 ? ', ' : ''}
                    </Text>
                  ))}
                </Group>
            </div>

            <Group gap="xs">
              <Badge color={status.color} variant="light" leftSection={<status.Icon size={12} />}>
                {status.label}
              </Badge>
              {contentRating && (
                <Badge color={contentRating.color} variant="light" leftSection={<contentRating.Icon size={12} />}>
                  {contentRating.label}
                </Badge>
              )}
              {series.hasAnime && (
                  <Badge leftSection={<IconDeviceTv size={12} />}>
                    {series.animeName || 'Anime'}
                  </Badge>
              )}
              <Badge variant="default">{series.type}</Badge>
              {series.year && <Badge variant="default">{series.year}</Badge>}
              {series.genres.slice(0, 6).map((g) => (
                <Badge key={g} variant="default" color="gray" fw={500}>
                  {g}
                </Badge>
              ))}
            </Group>

            <SeriesTagsEditor seriesId={series.id} tagIds={series.tagIds} />

            <Group gap="xs" align="center">
              <Text size="xs" c="dimmed" fw={600} tt="uppercase" style={{ letterSpacing: '0.05em' }}>
                Your rating
              </Text>
              <Rating
                count={5}
                fractions={2}
                value={series.rating ? series.rating / 2 : 0}
                onChange={(v) => submitRating(Math.round(v * 2) || null)}
              />
              {series.rating && (
                <>
                  <Text size="xs" c="dimmed" className="tnum">
                    {series.rating}/10
                  </Text>
                  <Tooltip label="Clear rating" withArrow>
                    <ActionIcon
                      size="sm"
                      variant="subtle"
                      color="gray"
                      onClick={() => submitRating(null)}
                      aria-label="Clear rating"
                    >
                      <IconX size={14} />
                    </ActionIcon>
                  </Tooltip>
                </>
              )}
            </Group>

            {(series.authorStory || series.authorArt || series.publisher) && (
              <Group gap="xs">
                {series.authorStory && (
                  <CreditLine label="Story" role="author" names={series.authorStory} />
                )}
                {series.authorArt && series.authorArt !== series.authorStory && (
                  <CreditLine label="Art" role="artist" names={series.authorArt} />
                )}
                {series.publisher && (
                  <CreditLine label="Publisher" role="studio" names={series.publisher} />
                )}
              </Group>
            )}

            {series.links.length > 0 && <MetadataLinks links={series.links} />}

            {series.rootFolderPath && (
              <Group gap={6} wrap="nowrap" c="dimmed">
                <IconFolderSymlink size={14} style={{ flexShrink: 0 }} />
                <Text size="xs" c="dimmed" ff="monospace" style={{ wordBreak: 'break-all' }}>
                  {series.rootFolderPath}
                </Text>
              </Group>
            )}

            {series.overview && (
              <Text size="sm" lineClamp={4} maw={720} c="gray.4">
                {series.overview}
              </Text>
            )}

            {series.animeStart && (
              <Text size="sm" c="dimmed">
                Anime aired from{' '}
                <Text span fw={600} c="gray.3" className="tnum">
                  {series.animeStart}
                </Text>
              </Text>
            )}
            {series.animeEnd && (
              <Text size="sm" c="dimmed">
                Anime aired until{' '}
                <Text span fw={600} c="gray.3" className="tnum">
                  {series.animeEnd}
                </Text>
              </Text>
            )}

            {/* Progress */}
            <Box maw={420} mt={4}>
              <Group justify="space-between" mb={4}>
                <Text size="xs" c="dimmed" fw={600} tt="uppercase" style={{ letterSpacing: '0.05em' }}>
                  Downloaded
                </Text>
                <Text size="xs" c="dimmed" className="tnum">
                  {progress.have} / {progress.tracked}
                  {progress.unmonitored && ' known, none monitored'}
                  {/* Spelled out as chapter numbers, not folded into the fraction above it: that
                      fraction counts rows (which include specials), so "80 / 80 of 136" would be
                      comparing two different things. */}
                  {sourceGap && (
                    <Text span c="yellow.5">
                      {' '}
                      · up to ch. {sourceGap.highest} of {sourceGap.total}
                    </Text>
                  )}
                </Text>
              </Group>
              <Progress
                value={progress.pct}
                // Never green while the sources are short of the full run: "all downloaded" and
                // "you have the whole series" are different claims, and the green tick is exactly
                // what makes someone unmonitor a series that's still missing its tail.
                color={
                  sourceGap
                    ? 'yellow'
                    : !progress.unmonitored && progress.have >= progress.tracked && progress.tracked > 0
                      ? 'teal'
                      : 'brand'
                }
                radius="xl"
              />
              {sourceGap && (
                <Group gap={6} mt={8} wrap="nowrap" align="flex-start">
                  <IconAlertTriangle
                    size={14}
                    style={{ color: 'var(--warn)', flexShrink: 0, marginTop: 2 }}
                  />
                  <Text size="xs" c="dimmed">
                    Your sources only reach chapter{' '}
                    <Text span fw={600} c="gray.3" className="tnum">
                      {sourceGap.highest}
                    </Text>
                    , but MangaBaka lists{' '}
                    <Text span fw={600} c="gray.3" className="tnum">
                      {sourceGap.total}
                    </Text>
                    . Roughly {sourceGap.missing} chapter{sourceGap.missing === 1 ? '' : 's'} can't be
                    downloaded from the sources linked here. Link another source to close the gap.
                  </Text>
                </Group>
              )}
            </Box>

            {readTracking && series.readChapterCount != null && progress.have > 0 && (
              <Box maw={420}>
                <Group justify="space-between" mb={4}>
                  <Text size="xs" c="dimmed" fw={600} tt="uppercase" style={{ letterSpacing: '0.05em' }}>
                    Read
                  </Text>
                  <Text size="xs" c="dimmed" className="tnum">
                    {series.readChapterCount} / {progress.have}
                  </Text>
                </Group>
                <Progress
                  value={Math.min(100, (series.readChapterCount / progress.have) * 100)}
                  color="var(--info)"
                  radius="xl"
                />
              </Box>
            )}
          </Stack>
        </Flex>
      </Box>

      {/* Action toolbar */}
      <Group gap="xs" wrap="wrap">
        <Button
          variant="light"
          leftSection={<IconRefresh size={16} />}
          loading={refresh.isPending}
          onClick={() =>
            refresh.mutate(seriesId, {
              onSuccess: (r) => notify.ok(`Refreshed, ${r.newChapters} new chapter(s)`),
            })
          }
        >
          Refresh chapters
        </Button>
        {canDownload ? (
          <>
            <Button
              variant="light"
              color="grape"
              leftSection={<IconSearch size={16} />}
              loading={searchMissing.isPending}
              onClick={() =>
                searchMissing.mutate(seriesId, {
                  onSuccess: (r) => notify.ok(`Queued ${r.queued} missing chapter(s)`),
                })
              }
            >
              Search missing
            </Button>
            <Button variant="light" color="cyan" leftSection={<IconDownload size={16} />} onClick={() => setReleaseModalOpen(true)}>
              Search releases
            </Button>
          </>
        ) : (
          <Button
            variant="light"
            color="grape"
            leftSection={<IconSend size={16} />}
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
        <Button
          variant="default"
          leftSection={<IconPhoto size={16} />}
          loading={refreshMetadata.isPending}
          onClick={() =>
            refreshMetadata.mutate(seriesId, {
              onSuccess: () => notify.ok('Metadata and poster refreshed'),
            })
          }
        >
          Metadata
        </Button>
        <Button
          variant="default"
          leftSection={<IconScan size={16} />}
          loading={rescan.isPending}
          onClick={() =>
            rescan.mutate(seriesId, {
              onSuccess: (r) =>
                notify.ok(
                  `Rescanned: ${r.newFiles} new, ${r.relinked} relinked, ${r.removed} removed`,
                ),
            })
          }
        >
          Rescan files
        </Button>
        <Button
          variant="default"
          leftSection={<IconFolderSymlink size={16} />}
          onClick={() => {
            setMoveTarget(null)
            setMoveFiles(true)
            setMoveModalOpen(true)
          }}
        >
          Move
        </Button>
        <Button
          variant="default"
          leftSection={<IconFileText size={16} />}
          onClick={() => setRenameModalOpen(true)}
        >
          Rename files
        </Button>

        <Tooltip
          label="Which chapters are monitored - applies now and to chapters released later"
          withArrow
          multiline
          w={240}
        >
          <Select
            leftSection={<IconEye size={15} />}
            w={210}
            data={[
              { value: 'All', label: 'Monitor: all chapters' },
              { value: 'Smart', label: 'Monitor: smart'},
              { value: 'MainOnly', label: 'Monitor: main (no specials)' },
              { value: 'None', label: 'Monitor: none' },
            ]}
            value={series.monitorNewItems}
            disabled={setMonitorMode.isPending}
            comboboxProps={{ withinPortal: true }}
            onChange={(mode) =>
              mode &&
              setMonitorMode.mutate(
                { seriesId, mode },
                {
                  onSuccess: (r) => notify.ok(`Monitoring ${r.monitored}/${r.total} chapter(s)`),
                },
              )
            }
          />
        </Tooltip>

        <Tooltip
          label="Scrobble only: skip tracker pushes. Full: also excluded from Rewind stats and reading history"
          withArrow
          multiline
          w={260}
        >
          <Select
            leftSection={<IconEyeOff size={15} />}
            w={200}
            data={INCOGNITO_OPTIONS.map((o) => ({
              value: o.value,
              label: `Incognito: ${o.label.toLowerCase()}`,
            }))}
            value={series.incognito}
            disabled={setIncognito.isPending}
            comboboxProps={{ withinPortal: true }}
            onChange={(mode) =>
              mode &&
              setIncognito.mutate(
                { seriesId, mode },
                {
                  onSuccess: (r) => notify.ok(`Incognito: ${r.incognito}`),
                },
              )
            }
          />
        </Tooltip>

        <Tooltip
          label="While reading: only tells you about new chapters while you're partway through. Muted: nothing from this series at all"
          withArrow
          multiline
          w={280}
        >
          <Select
            leftSection={<IconBell size={15} />}
            w={215}
            data={SERIES_NOTIFICATION_OPTIONS.map((o) => ({
              value: o.value,
              label: `Notify: ${o.label.toLowerCase()}`,
            }))}
            value={series.notificationMode}
            disabled={setNotificationMode.isPending}
            comboboxProps={{ withinPortal: true }}
            onChange={(mode) =>
              mode &&
              setNotificationMode.mutate(
                { seriesId, mode },
                {
                  onSuccess: (r) => notify.ok(`Notifications: ${r.notificationMode}`),
                },
              )
            }
          />
        </Tooltip>

        <Button
          variant="subtle"
          color="red"
          leftSection={<IconTrash size={16} />}
          ml="auto"
          onClick={() => setDeleteSeriesModalOpen(true)}
        >
          Remove
        </Button>
      </Group>

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

      <SourceMappingsSection
        seriesId={seriesId}
        seriesTitle={series.title}
        matching={series.sourceMatchPending}
      />

      <RelatedSeriesSection seriesId={seriesId} />
      <SimilarSeriesSection seriesId={seriesId} />

      {/* Chapters */}
      <Group justify="space-between" wrap="wrap" gap="sm">
        <Group gap="xs" align="baseline">
          <Title order={3}>Chapters</Title>
          {chapters && (
            <Text size="sm" c="dimmed" className="tnum">
              {progress.have}/{progress.tracked}
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
            <SegmentedControl
              size="xs"
              value={chapterFilter}
              onChange={setChapterFilter}
              data={[
                { value: 'all', label: `All` },
                { value: 'monitored', label: `Monitored (${chapters.filter(chapterFilters.monitored).length})` },
                { value: 'missing', label: `Missing (${chapters.filter(chapterFilters.missing).length})` },
                { value: 'downloaded', label: `Have (${chapters.filter(chapterFilters.downloaded).length})` },
                // Only when read progress is meaningful: with no tracking at all "Unread" would
                // just duplicate "Have" and read a series as entirely unread.
                ...(readTracking && progress.have > 0
                  ? [{ value: 'unread', label: `Unread (${chapters.filter(filters.unread).length})` }]
                  : []),
                { value: 'specials', label: `Specials (${chapters.filter(chapterFilters.specials).length})` },
              ]}
            />
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
              <Button
                size="xs"
                variant="light"
                leftSection={<IconEye size={15} />}
                disabled={selected.size === 0}
                loading={setChaptersMonitored.isPending && setChaptersMonitored.variables?.monitored === true}
                onClick={() =>
                  setChaptersMonitored.mutate(
                    { chapterIds: [...selected], monitored: true },
                    { onSuccess: (r) => notify.ok(`Monitoring ${r.updated} chapter(s)`) },
                  )
                }
              >
                Monitor
              </Button>
              <Button
                size="xs"
                variant="light"
                color="gray"
                leftSection={<IconEyeOff size={15} />}
                disabled={selected.size === 0}
                loading={setChaptersMonitored.isPending && setChaptersMonitored.variables?.monitored === false}
                onClick={() =>
                  setChaptersMonitored.mutate(
                    { chapterIds: [...selected], monitored: false },
                    { onSuccess: (r) => notify.ok(`Unmonitored ${r.updated} chapter(s)`) },
                  )
                }
              >
                Unmonitor
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
                color="orange"
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
      ) : (
        <Box
          pos="relative"
          ref={chapterTableRef}
          style={{ '--chapter-marker-slot': `${markerSlot}px` } as React.CSSProperties}
        >
        <Table.ScrollContainer minWidth={670}>
          <Table highlightOnHover verticalSpacing="xs">
            <Table.Thead>
              <Table.Tr>
                <Table.Th w={52}>Watch</Table.Th>
                <Table.Th w={170}>Chapter</Table.Th>
                <Table.Th>Title</Table.Th>
                <Table.Th w={120}>Released</Table.Th>
                <Table.Th w={110}>Source</Table.Th>
                <Table.Th w={240}>Status</Table.Th>
                <Table.Th w={92} />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {renderedRows.rows.map((row) => {
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
                  opacity={c.monitored || c.hasFile ? 1 : 0.55}
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
                      checked={c.monitored}
                      onChange={(e) =>
                        toggleMonitor.mutate({ chapterId: c.id, monitored: e.currentTarget.checked })
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
      )}

      <SeriesScrobbleSection seriesId={seriesId} />

      <SeriesFilesSection seriesId={seriesId} />

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
    </Stack>
  )
}

function CreditLine({
  label,
  role,
  names,
}: {
  label: string
  role: 'author' | 'artist' | 'studio'
  names: string
}) {
  const values = names.split(',').map((n) => n.trim()).filter(Boolean)
  return (
    <Text size="sm" c="dimmed">
      {label}:{' '}
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
