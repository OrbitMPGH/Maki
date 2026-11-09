import { Button, Center, Group, Stack, Switch, Text, VisuallyHidden } from '@mantine/core'
import { useQueryClient } from '@tanstack/react-query'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { IconCloudOff } from '../../lib/icons'
import { api, ApiError, isOfflineUnavailable } from '../../api/client'
import { Trans, useLingui } from '@lingui/react/macro'
import { useDocumentTitle } from '../../lib/documentTitle'
import { getConnectivity, useConnectivity } from '../../lib/connectivity'
import { settleQueuedProgress } from '../../pwa/progressQueue'
import {
  flushProgress,
  manifestServedOffline,
  useBookmarks,
  useReaderManifest,
  useToggleBookmark,
  type ReaderManifest,
} from '../../api/reader'
import NotesPanel from '../../components/notes/NotesPanel'
import { NotesDropdown } from '../../components/notes/NotesDropdown'
import { notesEscapeCloses } from '../../components/notes/notesEscape'
import { usePhoneSheet } from '../../components/notes/phone'
import ChapterBanner from './ChapterBanner'
import ChapterEnd from './ChapterEnd'
import NotesMargin from './NotesMargin'
import { RatingCheckpointPrompt } from '../../components/series/RatingCheckpointPrompt'
import { useDismissRatingCheckpoint, useRatingCheckpoint, useSetProvisionalRating } from '../../api/hooks'
import { notifications } from '../../lib/toast'
import { t as now } from '@lingui/core/macro'
import ShortcutSheet from './ShortcutSheet'
import ContinuousView from './ContinuousView'
import ServerEnhanceStatus, { useEnhancePhase } from './ServerEnhanceStatus'
import { serverModelFor, useServerEnhance, type AutoPages } from './useServerEnhance'
import PagedView from './PagedView'
import PageStrip from './PageStrip'
import ReaderToolbar from './ReaderToolbar'
import { OVERLAY_Z } from './ReaderSettings'
import { probeWebtoon } from './pageShape'
import { enhanceFor, toneFilter, useReaderPrefs, type ReaderPrefs } from './prefs'
import { usePageUrls, usePreload, useWarmChapter } from './usePageUrls'
import { useReaderProgress } from './useReaderProgress'
import { useReadingClock } from './useReadingClock'
import { useWakeLock } from './useWakeLock'
import { spreadIndexOf, usePageAspects, useSpreads } from './useSpreads'
import { serverChapterLabel } from '../../format'
import { scrollBehavior } from '../../lib/scrollBehavior'
import { useAutoScroll, useAutoScrollLevel } from './useAutoScroll'

const ZOOM_STEP = 0.25
const ZOOM_MAX = 4
const DOUBLE_TAP_ZOOM = 2
// Two taps closer together than this, and within this many pixels, are a double tap. A toolbar-toggling
// tap waits this long on touch screens so it can tell the two apart; page-turning taps never wait.
const DOUBLE_TAP_MS = 250
const DOUBLE_TAP_SLOP = 40
// How many pages from the end of a chapter the next chapter's first pages start loading.
const WARM_NEXT_WITHIN = 3
const CHROME_HIDE_MS = 4000
const NO_PAGES: ReadonlySet<number> = new Set()
const NO_AUTO_PAGES: AutoPages = { wanted: NO_PAGES, plain: NO_PAGES }
// A page turn this soon after moving to another chapter or the end screen is the tail of a double tap,
// a fling or a held key, and must not carry on through the next chapter boundary.
const CHAPTER_SETTLE_MS = 800
const FLUSH_WAIT_MS = 2000
// A screen step in continuous mode moves this share of the viewport, so the last lines read stay on
// screen at the top and the eye finds its place again.
const STRIP_STEP_SHARE = 0.85
// A step pressed while the last one is still gliding goes on from where that one is headed.
const STRIP_STEP_CHAIN_MS = 500
const STRIP_BOTTOM_EPSILON = 2

/**
 * Whether the chapter at `urls` reads as a webtoon, once known; undefined while the probe runs.
 * False whenever detection does not apply, so nothing waits on it.
 */
function useWebtoonGuess(urls: string[], active: boolean, from: number): boolean | undefined {
  const [guess, setGuess] = useState<{ urls: string[]; webtoon: boolean } | null>(null)
  useEffect(() => {
    if (!active || urls.length === 0) return
    const controller = new AbortController()
    void probeWebtoon(urls, from, controller.signal).then((webtoon) => {
      if (!controller.signal.aborted) setGuess({ urls, webtoon })
    })
    return () => controller.abort()
  }, [urls, active, from])
  if (!active) return false
  return guess?.urls === urls ? guess.webtoon : undefined
}

/**
 * The chromeless reader. Rendered outside the AppShell (see App.tsx) so it owns the whole
 * viewport, and always dark regardless of the theme preset, the same choice the Rewind overlay
 * makes, because page art has to sit on neutral black.
 */
export default function ReaderPage() {
  const { chapterId: param } = useParams()
  const chapterId = Number(param)
  const navigate = useNavigate()
  const { t } = useLingui()
  const queryClient = useQueryClient()
  const {
    data: manifest,
    error,
    isLoading,
    isError,
    isFetching,
    refetch,
  } = useReaderManifest(chapterId)
  useDocumentTitle(manifest ? `${manifest.seriesTitle} - ${serverChapterLabel(manifest.label)}` : null)
  const { prefs, update, keepForSeries, selection, setSelection, source, autoProfileId, profiles } =
    useReaderPrefs(manifest, !isFetching && !isError)
  // A layout or fit picked by hand during this visit to the series. Webtoon detection only stands
  // in for a choice nobody made, so either one ends it for the series.
  const [handPicked, setHandPicked] = useState<{ seriesId: number; mode: boolean; fit: boolean } | null>(null)
  const picked = handPicked != null && handPicked.seriesId === manifest?.seriesId ? handPicked : null

  const [page, setPage] = useState(0)
  // Which half of a split wide page is up, in reading order. Progress only ever sees `page`.
  const [half, setHalf] = useState<0 | 1>(0)
  // Bumped on every *explicit* jump (resume, toolbar scrub, page-strip click, Home/End) so
  // ContinuousView knows to scroll. Plain page updates from its own scroll tracking don't touch
  // this: scrolling to match a page the user just scrolled to would fight the scroll itself.
  const [seekVersion, setSeekVersion] = useState(0)
  const seekToPage = useCallback((index: number, toHalf: 0 | 1 = 0) => {
    setPage(index)
    setHalf(toHalf)
    setSeekVersion((v) => v + 1)
  }, [])
  /** The chapter whose saved position has been applied; gates every progress write. */
  const [resumedFor, setResumedFor] = useState<number | null>(null)
  /**
   * Where to resume once this device's queued progress for the chapter is accounted for. Offline, a
   * queued row is newer than anything the manifest says, so it wins.
   */
  const [queued, setQueued] = useState<{ chapterId: number; resumePage: number; finished: boolean } | null>(null)
  // Set just before navigating to a *previous* chapter: stepping backward off page 1 should land
  // on that chapter's last page, not wherever it was last resumed (page 1 for a completed one).
  const enterAtEndRef = useRef(false)
  const leavingRef = useRef(false)
  const lastTransitionRef = useRef(0)
  const changingChapterRef = useRef(false)
  const mountedRef = useRef(true)
  useEffect(() => {
    mountedRef.current = true
    return () => {
      mountedRef.current = false
    }
  }, [])
  // The chrome starts hidden and is summoned by a tap in the middle of the page: the art gets
  // the whole viewport until you ask for controls.
  const [chrome, setChrome] = useState(false)
  // One hold per source, so one letting go (the cursor leaving a bar) cannot release another
  // (the cursor arriving on the thumbnail strip).
  const [toolbarHeld, setToolbarHeld] = useState(false)
  const [stripHeld, setStripHeld] = useState(false)
  const chromeHeld = toolbarHeld || stripHeld
  const [fullscreen, setFullscreen] = useState(false)
  const [stripOpen, setStripOpen] = useState(false)
  const [zoom, setZoom] = useState(1)
  const [incognito, setIncognito] = useState(false)
  const [atEnd, setAtEnd] = useState(false)
  // The chapter this visit ran off the end of. Sticky, so "Stay here" and paging back cannot take
  // the completion back out of a write that has not gone out yet.
  const [finishedFor, setFinishedFor] = useState<number | null>(null)
  const [shortcutsOpen, setShortcutsOpen] = useState(false)
  const [notesOpen, setNotesOpen] = useState(false)
  const [surface, setSurface] = useState<HTMLDivElement | null>(null)
  const [autoScroll, setAutoScroll] = useState(false)
  const [autoScrollLevel, setAutoScrollLevel] = useAutoScrollLevel()
  // The thread an inbox link points at, tied to its chapter so paging on cannot carry it along.
  const [notesThread, setNotesThread] = useState<{ chapterId: number; rootId: number } | null>(null)
  const phoneSheet = usePhoneSheet()
  const [searchParams, setSearchParams] = useSearchParams()
  const notesParam = searchParams.get('notes')

  const pageCount = manifest?.pageCount ?? 0
  const urls = usePageUrls(chapterId, pageCount, manifest?.pageVersion)
  const thumbs = usePageUrls(chapterId, pageCount, manifest?.pageVersion, true)
  const { wide, measure } = usePageAspects(urls)

  const autoWanted = prefs.autoWebtoon && prefs.mode !== 'vertical' && !picked?.mode && pageCount > 0
  const webtoon = useWebtoonGuess(urls, autoWanted, manifest?.resumePage ?? 0)
  const autoVertical = autoWanted && webtoon === true
  const mode = autoVertical ? 'vertical' : prefs.mode
  // Fit height squeezes every slice of a strip to one screen; 1:1 is what the Webtoon profile uses.
  const fit = autoVertical && !picked?.fit && prefs.fit === 'height' ? 'original' : prefs.fit
  const view = useMemo<ReaderPrefs>(() => ({ ...prefs, mode, fit }), [prefs, mode, fit])
  const edit = useCallback(
    (patch: Partial<ReaderPrefs>) => {
      const seriesId = manifest?.seriesId
      const layout = 'mode' in patch || 'fit' in patch
      if (seriesId != null && layout) {
        setHandPicked((current) => {
          const same = current?.seriesId === seriesId ? current : null
          return { seriesId, mode: Boolean(same?.mode) || 'mode' in patch, fit: Boolean(same?.fit) || 'fit' in patch }
        })
      }
      // Overriding what detection chose is a choice for this series. Kept on the profile or the
      // global defaults, detection would switch the next visit straight back.
      if (layout && autoVertical) {
        keepForSeries({ mode, fit, ...patch, autoWebtoon: false })
        return
      }
      update(patch)
    },
    [manifest, update, keepForSeries, autoVertical, mode, fit],
  )
  const splitting = mode === 'paged' && prefs.splitWide === 'split'
  // A half only means something while splitting, so a page reached any other way starts on the first.
  useEffect(() => {
    if (!splitting) setHalf(0)
  }, [splitting])
  const onStripPage = useCallback((index: number) => {
    setPage(index)
    setHalf(0)
  }, [])

  const spreads = useSpreads(pageCount, wide, mode === 'double')
  const spreadIndex = useMemo(() => spreadIndexOf(spreads, page), [spreads, page])
  // `page` is a spread's first index; the position on record is the furthest page on screen.
  const shownTo = useMemo(() => Math.max(page, ...(spreads[spreadIndex] ?? [])), [spreads, spreadIndex, page])
  // "4-5" while a spread shows two pages, so the last spread can say it reached the final page.
  const pageNumber = shownTo > page ? `${page + 1}-${shownTo + 1}` : `${page + 1}`
  // The first half of a wide last page is not the end of the chapter: it completes once the second
  // half is up, so until then the position on record stops a page short.
  const heldBack = splitting && half === 0 && wide[shownTo] && shownTo >= pageCount - 1
  const recordedTo = heldBack ? Math.max(0, shownTo - 1) : shownTo
  // A one-page chapter has no page before it, so the hold has to be sent as `completed: false`.
  const holdCompletion = heldBack && recordedTo >= pageCount - 1

  const { data: bookmarks } = useBookmarks(chapterId)
  const toggleBookmark = useToggleBookmark(chapterId)
  // A toggle cannot be replayed safely, so bookmarks wait for the connection.
  const offline = useConnectivity() === 'offline'
  const bookmarkOffline = offline
  const bookmarkedPages = useMemo(
    () => new Set((bookmarks ?? []).map((b) => b.pageIndex)),
    [bookmarks],
  )
  const bookmarked = bookmarkedPages.has(page)

  // The preload window counts spreads, so double-page mode warms as many turns as single-page does.
  const preloadPages = useMemo(
    () =>
      mode === 'vertical' || prefs.preload <= 0
        ? []
        : spreads.slice(spreadIndex, spreadIndex + 1 + prefs.preload).flat(),
    [spreads, spreadIndex, mode, prefs.preload],
  )
  // The two spreads behind stay held as well, so turning back is as quick as turning forward.
  const heldPages = useMemo(
    () => (preloadPages.length === 0 ? [] : [...spreads.slice(Math.max(0, spreadIndex - 2), spreadIndex).flat(), ...preloadPages]),
    [preloadPages, spreads, spreadIndex],
  )
  usePreload(urls, heldPages, measure)

  const current = manifest?.chapterId === chapterId ? manifest : undefined
  const enhance = enhanceFor(prefs, manifest?.tier)

  // E jumps to Always and a second press goes back to the level it came from. Starting on Always,
  // there is nothing to go back to, so it goes Off.
  const enhanceBefore = useRef<{ seriesId: number; level: ReaderPrefs['enhance'] } | null>(null)
  useEffect(() => {
    if (prefs.enhance !== 'always') enhanceBefore.current = null
  }, [prefs.enhance])
  const toggleAlwaysEnhance = () => {
    const seriesId = manifest?.seriesId
    if (seriesId == null) return
    const before = enhanceBefore.current?.seriesId === seriesId ? enhanceBefore.current.level : null
    const level = prefs.enhance !== 'always' ? 'always' : (before ?? 'off')
    enhanceBefore.current = level === 'always' ? { seriesId, level: prefs.enhance } : null
    update({ enhance: level })
    const skipped = level !== 'off' && enhanceFor({ ...prefs, enhance: level }, manifest?.tier) === 'off'
    const message = skipped
      ? t`Enhance is set, but stays off for this chapter because of its quality.`
      : level === 'always'
        ? t`Enhance: Always`
        : level === 'auto'
          ? t`Enhance: Auto`
          : t`Enhance: Off`
    notifications.hide('reader-enhance')
    notifications.show({ id: 'reader-enhance', message, autoClose: 1500 })
  }
  const serverModel = current
    ? serverModelFor(enhance, prefs.enhanceMethod, current.enhanceServer, offline || manifestServedOffline(chapterId))
    : null
  // The strip sizes its own range from the pages' pixels, so the server follows it there.
  const [stripRange, setStripRange] = useState<{ pages: number[]; beyond: number }>({ pages: [], beyond: 0 })
  const onEnhanceRange = useCallback(
    (_pages: number[], beyond: number, server: number[]) => setStripRange({ pages: server, beyond }),
    [],
  )
  const enhanceWindow = useMemo(() => {
    if (!serverModel) return []
    if (mode !== 'vertical') return preloadPages.length > 0 ? preloadPages : (spreads[spreadIndex] ?? [])
    return stripRange.pages
  }, [serverModel, mode, preloadPages, spreads, spreadIndex, stripRange])
  // A window that runs past the last page carries on into the next chapter's first pages.
  const nextChapterId = current?.nextChapterId ?? null
  const beyondEnd =
    !serverModel || nextChapterId == null || resumedFor !== chapterId
      ? 0
      : mode === 'vertical'
        ? stripRange.beyond
        : prefs.preload > 0
          ? Math.max(0, spreadIndex + 1 + prefs.preload - spreads.length) * (mode === 'double' ? 2 : 1)
          : 0
  const enhanceNext = useMemo(
    () =>
      nextChapterId != null && beyondEnd > 0
        ? { chapterId: nextChapterId, pages: Array.from({ length: beyondEnd }, (_, index) => index) }
        : null,
    [nextChapterId, beyondEnd],
  )
  // On `auto` the server is asked only for pages a view has measured as wanted.
  const [autoPages, setAutoPages] = useState<AutoPages>(NO_AUTO_PAGES)
  const onStretched = useCallback((wanted: ReadonlySet<number>, plain: ReadonlySet<number>) => setAutoPages({ wanted, plain }), [])
  const wholeFrom = mode === 'vertical' ? page : (spreads[spreadIndex]?.[0] ?? page)
  const enhanceWhole = useMemo(
    () =>
      !current || resumedFor !== chapterId
        ? undefined
        : prefs.enhanceWholeChapter && serverModel
          ? { from: wholeFrom, pageCount }
          : null,
    [current, resumedFor, chapterId, prefs.enhanceWholeChapter, serverModel, wholeFrom, pageCount],
  )
  const server = useServerEnhance(
    chapterId,
    serverModel,
    enhanceWindow,
    current?.pageVersion,
    enhance === 'auto' ? autoPages : null,
    enhanceNext,
    enhanceWhole,
  )

  const serverStates = server?.states
  const enhancedPages = useMemo(
    () =>
      serverStates
        ? new Set([...serverStates].filter(([, state]) => state === 'ready').map(([index]) => index))
        : undefined,
    [serverStates],
  )
  const upscalingPages = useMemo(
    () =>
      serverStates
        ? new Set([...serverStates].filter(([, state]) => state === 'working').map(([index]) => index))
        : undefined,
    [serverStates],
  )
  const onScreen = useMemo(
    () => (atEnd ? [] : mode === 'vertical' ? [page] : (spreads[spreadIndex] ?? [page])),
    [atEnd, mode, page, spreads, spreadIndex],
  )
  const enhancePhase = useEnhancePhase(server, onScreen)

  const tone = useMemo(() => toneFilter(prefs), [prefs])
  useWakeLock(prefs.keepAwake)

  const invalidateProgress = useCallback(
    (seriesId: number) => {
      void queryClient.invalidateQueries({ queryKey: ['reader-progress', seriesId] })
      void queryClient.invalidateQueries({ queryKey: ['reader-continue', seriesId] })
      void queryClient.invalidateQueries({ queryKey: ['series'] })
      void queryClient.invalidateQueries({ queryKey: ['home', 'reading'] })
      void queryClient.invalidateQueries({ queryKey: ['progress'] })
      void queryClient.invalidateQueries({ queryKey: ['stats'] })
    },
    [queryClient],
  )
  const seriesId = manifest?.seriesId
  const onFlushed = useCallback(() => {
    if (seriesId != null) invalidateProgress(seriesId)
  }, [seriesId, invalidateProgress])

  // The position writer stays off until the chapter has resumed. `page` is 0 until then, and
  // writing that would overwrite the saved position with page 1, the very thing being resumed to.
  const tracking = resumedFor === manifest?.chapterId && !incognito
  const finished = finishedFor != null && finishedFor === manifest?.chapterId
  // Held back until this chapter's own resume has landed, so the warm-up never competes with the
  // pages being opened. A preload of 0 switches off warming the next chapter too.
  const nearEnd =
    atEnd ||
    (pageCount > 0 &&
      (mode === 'vertical'
        ? page >= pageCount - WARM_NEXT_WITHIN
        : spreadIndex >= spreads.length - WARM_NEXT_WITHIN))
  useWarmChapter(
    manifest?.nextChapterId ?? null,
    resumedFor === manifest?.chapterId && nearEnd && prefs.preload > 0,
    Math.max(2, prefs.preload),
  )
  // Lives here rather than inside the progress hook so a chapter change can hand its banked
  // seconds to the same flush that writes the position out.
  // On a phone the notes sheet covers the page, so nothing is being read while it is up.
  const clock = useReadingClock(tracking, notesOpen && phoneSheet)
  const { settle: settleProgress } = useReaderProgress(
    manifest?.chapterId,
    manifest?.seriesId,
    finished ? pageCount - 1 : recordedTo,
    pageCount,
    finished || (holdCompletion ? false : undefined),
    tracking,
    clock,
    onFlushed,
  )

  /**
   * Before resuming, lets the chapter's queued offline progress reach the server (briefly). While
   * offline, whatever is still waiting is folded in. Online it is left to the replay: the server
   * keeps a queued completion that arrives stale unless the chapter was marked unread since.
   */
  useEffect(() => {
    if (!manifest || isFetching || isError || resumedFor === manifest.chapterId) return
    if (queued?.chapterId === manifest.chapterId) return
    let cancelled = false
    const opened = manifest
    void (async () => {
      const { rows, sent } = await settleQueuedProgress(opened.chapterId)
      let base: ReaderManifest = opened
      // What went out changed the server's copy since this manifest was fetched.
      if (sent) {
        const fresh = await api<ReaderManifest>(`/reader/chapter/${opened.chapterId}`).catch(() => null)
        if (fresh && !cancelled) {
          base = fresh
          queryClient.setQueryData(['reader-manifest', opened.chapterId], fresh)
        }
      }
      if (cancelled) return
      const waiting = getConnectivity() === 'offline' ? rows : []
      setQueued({
        chapterId: opened.chapterId,
        resumePage: waiting.reduce((furthest, row) => Math.max(furthest, row.pageIndex), base.resumePage),
        finished: waiting.some((row) => row.completed),
      })
    })()
    return () => {
      cancelled = true
    }
  }, [manifest, isFetching, isError, resumedFor, queued, queryClient])

  /**
   * Resume where the chapter was left off, once per chapter, and only off a freshly fetched
   * manifest. React Query serves the cached one first on a reopen, and its `resumePage` is a
   * snapshot from the previous visit: applying it would jump to page 1 and then save that.
   */
  useEffect(() => {
    // A failed refetch leaves the cached manifest in place, and its resumePage is the previous
    // visit's snapshot: resuming from it would start writing that position back.
    if (!manifest || isFetching || isError || resumedFor === manifest.chapterId) return
    if (queued?.chapterId !== manifest.chapterId) return
    setResumedFor(manifest.chapterId)
    setQueued(null)
    const toEnd = enterAtEndRef.current
    enterAtEndRef.current = false
    const lastPage = Math.max(0, manifest.pageCount - 1)
    const saved = queued.resumePage <= lastPage ? Math.max(0, queued.resumePage) : 0
    seekToPage(toEnd ? lastPage : saved)
    setZoom(1)
    setAtEnd(false)
    // A completion still in the queue rides along on this visit's own writes, as if finished here.
    setFinishedFor(queued.finished ? manifest.chapterId : null)
    leavingRef.current = false
    if (changingChapterRef.current) lastTransitionRef.current = Date.now()
    changingChapterRef.current = false
  }, [manifest, isFetching, isError, resumedFor, queued, seekToPage])

  // An inbox link names a thread. It is read once and then dropped from the address, or paging to
  // the next chapter (this component stays mounted) would open the panel again.
  useEffect(() => {
    if (notesParam === null) return
    const rootId = Number(notesParam)
    if (Number.isInteger(rootId) && rootId > 0) {
      setNotesThread({ chapterId, rootId })
      setNotesOpen(true)
    }
    setSearchParams(
      (current) => {
        const next = new URLSearchParams(current)
        next.delete('notes')
        return next
      },
      { replace: true },
    )
  }, [notesParam, chapterId, setSearchParams])

  // Captured so it runs before the reader's own Escape, which leaves for the series page. The
  // shortcut sheet sits above the notes and takes Escape first. In a text field Escape only leaves
  // the field, and a composer holding unsaved text keeps the panel open.
  useEffect(() => {
    if (!notesOpen || shortcutsOpen) return
    const onEscape = (event: KeyboardEvent) => {
      if (event.key !== 'Escape' || event.isComposing) return
      event.preventDefault()
      const panel = document.querySelector<HTMLElement>('.notes-dropdown-fixed, .notes-drawer')
      if (!notesEscapeCloses(event, panel)) return
      setNotesOpen(false)
      setNotesThread(null)
    }
    window.addEventListener('keydown', onEscape, true)
    return () => window.removeEventListener('keydown', onEscape, true)
  }, [notesOpen, shortcutsOpen])

  const notedPageSet = useMemo(() => new Set(manifest?.notedPages ?? []), [manifest?.notedPages])

  // Own the viewport: no page scrolling behind the reader, and always-dark chrome.
  useEffect(() => {
    document.body.classList.add('reader-open')
    return () => document.body.classList.remove('reader-open')
  }, [])

  // Auto-hide, unless the toolbar is holding it open (cursor over a bar, or a menu is up):
  // yanking the controls out from under an open settings popover would close it mid-click.
  useEffect(() => {
    if (!chrome || chromeHeld) return
    const timer = setTimeout(() => setChrome(false), CHROME_HIDE_MS)
    return () => clearTimeout(timer)
  }, [chrome, chromeHeld, page])

  useEffect(() => {
    const onChange = () => setFullscreen(Boolean(document.fullscreenElement))
    document.addEventListener('fullscreenchange', onChange)
    return () => {
      document.removeEventListener('fullscreenchange', onChange)
      // Leaving the reader must not leave the whole app fullscreen.
      if (document.fullscreenElement) void document.exitFullscreen().catch(() => {})
    }
  }, [])

  const toggleFullscreen = useCallback(() => {
    // iPhone Safari has no element fullscreen; the method is missing there, not just rejected.
    if (!document.fullscreenEnabled) return
    if (document.fullscreenElement) {
      void document.exitFullscreen().catch(() => {})
    } else {
      void document.documentElement.requestFullscreen().catch(() => {})
    }
  }, [])

  /**
   * Moves to another chapter, flushing the current position first. `complete` is passed
   * explicitly on a forward exit so leaving the last page counts as read even when the
   * debounced write hasn't fired yet.
   */
  const goToChapter = useCallback(
    async (target: number | null, complete: boolean, toEnd = false) => {
      if (target === null || leavingRef.current) return
      leavingRef.current = true
      enterAtEndRef.current = toEnd
      // Same gate as the position writer: before the resume lands, `page` is 0 and not a position.
      if (manifest && tracking) {
        const done = complete || finished
        // Banked time belongs to the chapter being left, and the next chapter's clock starts
        // from nothing, so it has to go out with this write or it is lost.
        const seconds = clock.take()
        const { chapterId: leftId, seriesId } = manifest
        const at = done ? pageCount - 1 : recordedTo
        // Navigation waits for the write only up to a short grace period: a hung save must not
        // freeze the page turn. The chain keeps running after navigating, so the position still
        // lands, and settle-then-flush stays in order.
        const flushed = (async () => {
          await settleProgress()
          const completed = done || (holdCompletion ? false : undefined)
          await flushProgress(leftId, seriesId, at, completed, seconds, pageCount).catch(() => {})
          invalidateProgress(seriesId)
        })()
        await Promise.race([flushed, new Promise((resolve) => setTimeout(resolve, FLUSH_WAIT_MS))])
      }
      // Left the reader during the grace wait (Escape, browser back): do not pull the user back in.
      if (!mountedRef.current) return
      // ReaderPage stays mounted across /read/:chapterId changes, so a manifest cached from an
      // earlier visit to `target` would otherwise be served as-is (staleTime is Infinity) with its
      // now-stale resumePage. Drop it so the coming mount always fetches fresh.
      queryClient.removeQueries({ queryKey: ['reader-manifest', target] })
      changingChapterRef.current = true
      navigate(`/read/${target}`, { replace: true })
    },
    [
      manifest,
      navigate,
      recordedTo,
      holdCompletion,
      pageCount,
      queryClient,
      invalidateProgress,
      tracking,
      clock,
      finished,
      settleProgress,
    ],
  )

  const zoomIn = useCallback(() => setZoom((z) => Math.min(ZOOM_MAX, z + ZOOM_STEP)), [])
  const zoomOut = useCallback(() => setZoom((z) => Math.max(1, z - ZOOM_STEP)), [])
  const zoomReset = useCallback(() => setZoom(1), [])

  const reachEnd = useCallback(() => {
    lastTransitionRef.current = Date.now()
    setAtEnd(true)
    if (manifest && tracking) setFinishedFor(manifest.chapterId)
  }, [manifest, tracking])

  const settling = useCallback(() => Date.now() - lastTransitionRef.current < CHAPTER_SETTLE_MS, [])

  const next = useCallback(() => {
    // On the end screen the forward key is the "second press" it asks for.
    if (atEnd) {
      if (settling()) return
      if (manifest?.nextChapterId != null) void goToChapter(manifest.nextChapterId, true)
      return
    }
    if (splitting && half === 0 && wide[page]) {
      setHalf(1)
      return
    }
    const nextSpread = spreads[spreadIndex + 1]
    if (nextSpread) {
      seekToPage(nextSpread[0])
      return
    }
    if (manifest?.nextChapterId == null) {
      reachEnd()
      return
    }
    // Auto-advance means what it says: the page turn off the last page lands in the next chapter.
    // With it off, an interstitial instead: the chapter ends where you asked it to, and the jump
    // is a deliberate second press.
    if (settling()) return
    if (prefs.autoNextChapter) void goToChapter(manifest.nextChapterId, true)
    else reachEnd()
  }, [
    atEnd,
    spreads,
    spreadIndex,
    manifest,
    prefs.autoNextChapter,
    goToChapter,
    seekToPage,
    reachEnd,
    settling,
    splitting,
    half,
    wide,
    page,
  ])

  /** Continuous mode's equivalent of `next()` hitting the chapter boundary: no spreads to check,
   *  the strip only ever has one more chapter to reach for. */
  const continuousPastEnd = useCallback(() => {
    if (settling()) return false
    if (manifest?.nextChapterId == null || !prefs.autoNextChapter) {
      reachEnd()
      return false
    }
    void goToChapter(manifest.nextChapterId, true)
    return true
  }, [manifest, prefs.autoNextChapter, goToChapter, reachEnd, settling])

  const previous = useCallback(() => {
    if (atEnd) {
      setAtEnd(false)
      return
    }
    if (splitting && half === 1) {
      setHalf(0)
      return
    }
    const previousSpread = spreads[spreadIndex - 1]
    if (previousSpread) {
      // Stepping back onto a split page lands on the half read last.
      seekToPage(previousSpread[0], splitting && wide[previousSpread[0]] ? 1 : 0)
    } else if (manifest?.previousChapterId != null && !settling()) {
      void goToChapter(manifest.previousChapterId, false, true)
    }
  }, [spreads, spreadIndex, manifest, goToChapter, atEnd, seekToPage, settling, splitting, half, wide])

  /** Continuous mode's page turn: about a screen down or up, or on into the next or previous chapter at either end. */
  const stripStep = useRef<{ top: number; at: number } | null>(null)
  const stepStrip = useCallback(
    (direction: 1 | -1) => {
      if (!surface) return
      const max = surface.scrollHeight - surface.clientHeight
      const chained = stripStep.current && performance.now() - stripStep.current.at < STRIP_STEP_CHAIN_MS
      const from = chained ? stripStep.current!.top : surface.scrollTop
      if (direction > 0 && from >= max - STRIP_BOTTOM_EPSILON) {
        continuousPastEnd()
        return
      }
      if (direction < 0 && from <= 0) {
        if (manifest?.previousChapterId != null && !settling()) void goToChapter(manifest.previousChapterId, false, true)
        return
      }
      const top = Math.min(max, Math.max(0, from + direction * surface.clientHeight * STRIP_STEP_SHARE))
      stripStep.current = { top, at: performance.now() }
      surface.scrollTo({ top, behavior: scrollBehavior() })
    },
    [surface, continuousPastEnd, manifest, goToChapter, settling],
  )

  const toggleAutoScroll = useCallback(() => {
    setAutoScroll((on) => !on)
    // Starting it is a request to read, so the bars get out of the way at once.
    setChrome(false)
  }, [])

  useEffect(() => {
    if (mode !== 'vertical') setAutoScroll(false)
  }, [mode])

  // Paused whenever something sits over the page; the chrome hides itself after a few seconds, so a
  // tap in the middle is a pause that lets go on its own.
  useAutoScroll(
    surface,
    autoScroll && mode === 'vertical' && !atEnd && !chrome && !shortcutsOpen && !notesOpen,
    autoScrollLevel,
    continuousPastEnd,
  )

  // The listener is registered once and reads the latest handler, so it is not torn down and
  // re-added on every render (a page change in continuous mode re-renders on each scroll step).
  const onKey = useRef<(event: KeyboardEvent) => void>(() => {})
  onKey.current = (event: KeyboardEvent) => {
    if (event.defaultPrevented || event.metaKey || event.ctrlKey || event.altKey) return
    const target = event.target instanceof HTMLElement ? event.target : null
    if (target && ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName)) return
    // Keys inside the notes belong to them, not to the page behind.
    if (target?.closest('.notes-surface')) return
    // Space on a control reached by keyboard presses that control, it does not turn the page.
    if (
      event.key === ' ' &&
      target?.matches(':focus-visible') &&
      target.closest('button, a, [role="button"], [role="slider"], [contenteditable="true"]')
    ) {
      return
    }

    // The sheet is read, not driven: while it is up the only keys that do anything close it.
    if (shortcutsOpen) {
      if (event.key === 'Escape' || event.key === '?') {
        event.preventDefault()
        setShortcutsOpen(false)
      }
      return
    }

    // In right-to-left reading the left arrow advances; in left-to-right it goes back.
    const forwardKey = prefs.direction === 'rtl' ? 'ArrowLeft' : 'ArrowRight'
    const backKey = prefs.direction === 'rtl' ? 'ArrowRight' : 'ArrowLeft'

    // Caps Lock and Shift upper-case letters; the named keys (ArrowLeft, Home) are longer than 1.
    const key = event.key.length === 1 ? event.key.toLowerCase() : event.key

    switch (key) {
      case forwardKey:
        event.preventDefault()
        next()
        break
      case backKey:
        event.preventDefault()
        previous()
        break
      case ' ':
      case 'PageDown':
      case 'PageUp': {
        event.preventDefault()
        const back = key === 'PageUp' || (key === ' ' && event.shiftKey)
        if (mode === 'vertical' && !atEnd) stepStrip(back ? -1 : 1)
        else if (back) previous()
        else next()
        break
      }
      case 'a':
        if (mode === 'vertical') toggleAutoScroll()
        break
      case '[':
        if (mode === 'vertical') setAutoScrollLevel(autoScrollLevel - 1)
        break
      case ']':
        if (mode === 'vertical') setAutoScrollLevel(autoScrollLevel + 1)
        break
      case 'Home':
        event.preventDefault()
        setAtEnd(false)
        seekToPage(0)
        break
      case 'End':
        event.preventDefault()
        setAtEnd(false)
        seekToPage(Math.max(0, pageCount - 1))
        break
      case 'f':
        toggleFullscreen()
        break
      case 'd':
        update({ direction: prefs.direction === 'rtl' ? 'ltr' : 'rtl' })
        break
      case 'b':
        if (!bookmarkOffline) toggleBookmark.mutate(page)
        break
      case 't':
        setStripOpen((open) => !open)
        break
      case '1':
        edit({ mode: 'paged' })
        break
      case '2':
        edit({ mode: 'double' })
        break
      case '3':
        edit({ mode: 'vertical' })
        break
      case 'c':
        update({ cropBorders: !prefs.cropBorders })
        break
      case 'e':
        toggleAlwaysEnhance()
        break
      case '+':
      case '=':
        zoomIn()
        break
      case '-':
        zoomOut()
        break
      case '0':
        zoomReset()
        break
      case '?':
        setShortcutsOpen(true)
        break
      case 'Escape':
        if (!document.fullscreenElement && manifest) navigate(`/series/${manifest.seriesId}`)
        break
    }
  }

  useEffect(() => {
    const listener = (event: KeyboardEvent) => onKey.current(event)
    window.addEventListener('keydown', listener)
    return () => window.removeEventListener('keydown', listener)
  }, [])

  /** Tap zones: outer thirds page, the middle toggles the chrome. Continuous mode splits top to bottom instead. */
  const handleTap = (clientX: number, clientY: number, bounds: DOMRect) => {
    if (prefs.tapZones && mode === 'vertical') {
      const ratio = (clientY - bounds.top) / bounds.height
      if (ratio < 0.33) {
        if (!surface || surface.scrollTop > 0) stepStrip(-1)
        else setChrome((visible) => !visible)
      } else if (ratio > 0.67) stepStrip(1)
      else setChrome((visible) => !visible)
      return
    }
    if (!prefs.tapZones || mode === 'vertical' || zoom !== 1) {
      setChrome((visible) => !visible)
      return
    }

    const ratio = (clientX - bounds.left) / bounds.width
    // Right-to-left reading puts "next" on the left edge.
    const leftAdvances = prefs.direction === 'rtl'
    if (ratio < 0.33) {
      if (leftAdvances) next()
      else previous()
    } else if (ratio > 0.67) {
      if (leftAdvances) previous()
      else next()
    } else {
      setChrome((visible) => !visible)
    }
  }

  // Read through a ref so the delayed single tap acts on the state at the time it fires.
  const tapRef = useRef(handleTap)
  tapRef.current = handleTap
  const pointerKind = useRef('mouse')
  const pendingTap = useRef<{
    timer: ReturnType<typeof setTimeout>
    x: number
    y: number
    bounds: DOMRect
  } | null>(null)
  useEffect(
    () => () => {
      if (pendingTap.current) clearTimeout(pendingTap.current.timer)
    },
    [],
  )

  // Only on the end screen: the server arms a checkpoint from the completing write, so there is
  // nothing to ask while the chapter is still open. Incognito reading writes nothing to arm one.
  const { data: checkpoint } = useRatingCheckpoint(manifest?.seriesId, chapterId, atEnd && !incognito)
  const setSoFar = useSetProvisionalRating()
  const dismissCheckpoint = useDismissRatingCheckpoint()

  /**
   * A mouse click acts at once, and so does a touch tap in a page-turning side zone. Only a touch
   * tap that toggles the toolbar (the centre zone, or anywhere while zoomed or without tap zones)
   * waits a moment for a second tap, which toggles zoom instead.
   */
  const onSurfaceClick = (event: React.MouseEvent<HTMLDivElement>) => {
    // A tap on the page is a tap outside the notes dropdown; it closes and the tap still counts.
    if (notesOpen && !phoneSheet) {
      setNotesOpen(false)
      setNotesThread(null)
    }
    const bounds = event.currentTarget.getBoundingClientRect()
    if (pointerKind.current !== 'touch' || mode === 'vertical') {
      handleTap(event.clientX, event.clientY, bounds)
      return
    }

    const { clientX, clientY } = event
    const ratio = (clientX - bounds.left) / bounds.width
    if (zoom === 1 && prefs.tapZones && (ratio < 0.33 || ratio > 0.67)) {
      handleTap(clientX, clientY, bounds)
      return
    }

    const first = pendingTap.current
    if (first) {
      clearTimeout(first.timer)
      pendingTap.current = null
      if (Math.hypot(clientX - first.x, clientY - first.y) <= DOUBLE_TAP_SLOP) {
        setZoom((z) => (z === 1 ? DOUBLE_TAP_ZOOM : 1))
        return
      }
      tapRef.current(first.x, first.y, first.bounds)
    }
    const timer = setTimeout(() => {
      pendingTap.current = null
      tapRef.current(clientX, clientY, bounds)
    }, DOUBLE_TAP_MS)
    pendingTap.current = { timer, x: clientX, y: clientY, bounds }
  }

  // A cached manifest is shown only once the fresh one has landed and the resume is applied;
  // before that its prefs and position are a snapshot of the previous visit.
  // Also held while webtoon detection reads the first page headers, so a strip never opens paged.
  const guessing = autoWanted && webtoon === undefined
  if (!isError && (isLoading || (manifest && (resumedFor !== manifest.chapterId || guessing)))) {
    return (
      <div className="reader-root">
        <Center h="100dvh">
          <div className="reader-page-skeleton" aria-hidden />
        </Center>
      </div>
    )
  }

  if (!manifest || (isError && resumedFor !== manifest.chapterId)) {
    // Only a 404 means the file is the problem; anything else (network, 401, 500) is worth retrying.
    const unreadable = !isError || (error instanceof ApiError && error.status === 404)
    return (
      <div className="reader-root">
        <Center h="100dvh">
          <Stack align="center" gap="sm">
            {isOfflineUnavailable(error) ? (
              <Stack align="center" gap={4}>
                <Group gap={8} wrap="nowrap">
                  <IconCloudOff size={18} color="var(--warn)" aria-hidden />
                  <Text c="var(--ink-2)" fw={600}>
                    <Trans>Not available offline</Trans>
                  </Text>
                </Group>
                <Text c="var(--ink-3)" size="sm">
                  <Trans>This chapter isn't saved on this device.</Trans>
                </Text>
              </Stack>
            ) : (
              <Text c="var(--ink-3)">
                {unreadable ? (
                  <Trans>This chapter has no readable file.</Trans>
                ) : (
                  <Trans>Could not load this chapter.</Trans>
                )}
              </Text>
            )}
            <Group gap="xs">
              {!unreadable && (
                <Button variant="light" loading={isFetching} onClick={() => void refetch()}>
                  <Trans>Try again</Trans>
                </Button>
              )}
              <Button component={Link} to="/library" variant="light">
                <Trans>Back to library</Trans>
              </Button>
            </Group>
          </Stack>
        </Center>
      </div>
    )
  }

  const closeNotes = () => {
    setNotesOpen(false)
    setNotesThread(null)
  }
  const notesBody = {
    chapterId: manifest.chapterId,
    seriesId: manifest.seriesId,
    page: atEnd ? null : page,
    pinPage: atEnd ? null : page,
    pageCount: manifest.pageCount,
    initialThreadId: notesThread?.chapterId === manifest.chapterId ? notesThread.rootId : null,
    onGoToPage: (target: number) => {
      setAtEnd(false)
      seekToPage(target)
      if (phoneSheet) setNotesOpen(false)
    },
  }

  // The manifest's read count is a snapshot from when the chapter opened, so finishing this one on
  // screen is added here, on the same condition the server uses. Incognito writes nothing.
  const readingCounted =
    !incognito && !manifest.completed && (finished || (recordedTo >= manifest.pageCount - 1 && !holdCompletion))

  return (
    <div className="reader-root" style={{ background: prefs.background }}>
      <ReaderToolbar
        manifest={manifest}
        page={page}
        pageLabel={pageNumber}
        onSeek={seekToPage}
        onPrevChapter={() => void goToChapter(manifest.previousChapterId, false)}
        // Skipping ahead is not finishing: only leaving from the last page counts as read.
        onNextChapter={() =>
          void goToChapter(manifest.nextChapterId, recordedTo >= manifest.pageCount - 1 && !holdCompletion)
        }
        prefs={view}
        onPrefs={edit}
        autoLayout={autoVertical}
        selection={selection}
        onSelection={setSelection}
        source={source}
        autoProfileId={autoProfileId}
        profiles={profiles}
        fullscreen={fullscreen}
        onToggleFullscreen={toggleFullscreen}
        zoom={zoom}
        zoomMax={ZOOM_MAX}
        onZoomIn={zoomIn}
        onZoomOut={zoomOut}
        onZoomReset={zoomReset}
        incognito={incognito}
        onIncognito={setIncognito}
        readingCounted={readingCounted}
        bookmarked={bookmarked}
        bookmarkDisabled={bookmarkOffline}
        onToggleBookmark={() => toggleBookmark.mutate(page)}
        noteCount={manifest.noteCount ?? 0}
        notesOpen={notesOpen}
        onToggleNotes={() => {
          setNotesThread(null)
          setNotesOpen((open) => !open)
        }}
        stripOpen={stripOpen}
        onToggleStrip={() => setStripOpen((open) => !open)}
        visible={chrome}
        onHold={setToolbarHeld}
        onReveal={() => setChrome(true)}
        onShortcuts={() => setShortcutsOpen(true)}
        serverEnhancing={server !== null}
        enhancedPages={enhancedPages}
        upscalingPages={upscalingPages}
        enhancePhase={enhancePhase}
        thumbs={thumbs}
        shownTo={shownTo}
        autoScroll={{
          on: autoScroll,
          level: autoScrollLevel,
          onToggle: toggleAutoScroll,
          onLevel: setAutoScrollLevel,
        }}
      />

      {atEnd ? (
        <ChapterEnd
          manifest={manifest}
          readingCounted={readingCounted}
          rtl={prefs.direction === 'rtl'}
          onNext={() => void goToChapter(manifest.nextChapterId, true)}
          onStay={() => setAtEnd(false)}
          onOpenNotes={() => setNotesOpen(true)}
          ratingPrompt={
            checkpoint?.checkpoint && (
              <RatingCheckpointPrompt
                checkpoint={checkpoint.checkpoint}
                soFar={checkpoint.provisionalRating}
                onRate={(rating) =>
                  setSoFar.mutate(
                    { seriesId: manifest.seriesId, rating },
                    { onSuccess: () => notifications.show({ color: 'var(--ok)', message: now`Rated ${rating}/10 so far` }) },
                  )
                }
                onDismiss={() => dismissCheckpoint.mutate(manifest.seriesId)}
              />
            )
          }
        />
      ) : (
        <div
          ref={setSurface}
          className="reader-surface"
          // Continuous mode scrolls one way only, unless the reader has been zoomed past 100%,
          // which is the one case where panning across a page is what was asked for.
          data-scroll={
            mode === 'vertical' && !(fit === 'original' && prefs.scale > 100)
              ? 'vertical'
              : undefined
          }
          onPointerDown={(event) => {
            pointerKind.current = event.pointerType
          }}
          onClick={onSurfaceClick}
        >
          {mode === 'vertical' ? (
            <ContinuousView
              urls={urls}
              page={page}
              onPageChange={onStripPage}
              seekVersion={seekVersion}
              onPastEnd={continuousPastEnd}
              hasNext={manifest.nextChapterId != null}
              nextLabel={manifest.nextChapterLabel}
              fit={fit}
              scale={prefs.scale}
              tone={tone}
              gap={prefs.pageGap}
              label={serverChapterLabel(manifest.label)}
              enhance={enhance}
              enhanceMethod={prefs.enhanceMethod}
              enhanceFallback={prefs.enhanceFallback}
              server={server}
              onStretched={onStretched}
              onEnhanceRange={onEnhanceRange}
            />
          ) : (
            <PagedView
              urls={urls}
              spread={spreads[spreadIndex] ?? [0]}
              fit={fit}
              direction={prefs.direction}
              zoom={zoom}
              scale={prefs.scale}
              tone={tone}
              label={serverChapterLabel(manifest.label)}
              onMeasure={measure}
              cropBorders={prefs.cropBorders}
              splitWide={mode === 'paged' ? prefs.splitWide : 'off'}
              half={half}
              enhance={enhance}
              enhanceMethod={prefs.enhanceMethod}
              enhanceFallback={prefs.enhanceFallback}
              server={server}
              preload={preloadPages}
              onStretched={onStretched}
            />
          )}
        </div>
      )}
      <ServerEnhanceStatus
        phase={enhancePhase}
        method={prefs.enhanceMethod}
        style={prefs.enhanceProgress}
        chrome={chrome}
        rtl={prefs.direction === 'rtl'}
      />

      {stripOpen && (
        <div
          className="reader-strip-wrap"
          data-visible={chrome}
          onMouseEnter={() => setStripHeld(true)}
          onMouseLeave={() => setStripHeld(false)}
          onFocus={(event) => {
            if (!(event.target as HTMLElement).matches(':focus-visible')) return
            setChrome(true)
            setStripHeld(true)
          }}
          onBlur={(event) => {
            if (!event.currentTarget.contains(event.relatedTarget as Node | null)) setStripHeld(false)
          }}
        >
          <PageStrip
            urls={thumbs}
            page={page}
            bookmarks={bookmarkedPages}
            notedPages={notedPageSet}
            onSelect={seekToPage}
            rtl={prefs.direction === 'rtl'}
          />
        </div>
      )}

      {/* Not gated on `atEnd`: it self-unmounts after a couple of seconds, and toggling a gate
          would remount it (replaying the flash) every time the end-of-chapter prompt is dismissed. */}
      {prefs.chapterBanner && (
        <ChapterBanner
          key={manifest.chapterId}
          seriesTitle={manifest.seriesTitle}
          label={serverChapterLabel(manifest.label)}
          pageCount={manifest.pageCount}
          coverUrl={manifest.seriesCoverUrl}
        />
      )}

      {shortcutsOpen && (
        <ShortcutSheet rtl={prefs.direction === 'rtl'} onClose={() => setShortcutsOpen(false)} />
      )}

      {!phoneSheet && prefs.notesBeside && !atEnd && (
        <NotesMargin
          key={`notes-margin-${manifest.chapterId}`}
          surface={surface}
          vertical={mode === 'vertical'}
          shownPages={spreads[spreadIndex] ?? [page]}
          page={page}
          chapterId={manifest.chapterId}
          seriesId={manifest.seriesId}
          pageCount={manifest.pageCount}
          finished={manifest.completed || finished}
          zoomed={zoom !== 1}
          layoutKey={`${mode}:${fit}:${prefs.scale}:${spreadIndex}:${half}:${page}:${zoom}`}
          lift={!chrome ? 'none' : stripOpen ? 'strip' : 'bar'}
          onGoToPage={seekToPage}
        />
      )}

      {phoneSheet ? (
        <NotesPanel
          key={`notes-${manifest.chapterId}`}
          opened={notesOpen}
          onClose={closeNotes}
          chapterLabel={serverChapterLabel(manifest.label)}
          inReader
          zIndex={OVERLAY_Z}
          {...notesBody}
        />
      ) : (
        <NotesDropdown
          key={`notes-${manifest.chapterId}`}
          opened={notesOpen}
          onClose={closeNotes}
          title={t`Notes, ${serverChapterLabel(manifest.label)}`}
          zIndex={OVERLAY_Z}
          headerExtra={
            <Switch
              size="xs"
              label={t`Beside the page`}
              checked={prefs.notesBeside}
              onChange={(event) => edit({ notesBeside: event.currentTarget.checked })}
            />
          }
          {...notesBody}
        />
      )}

      {prefs.showPageNumber && !chrome && !atEnd && (
        <div className="reader-page-badge">
          {pageNumber} / {manifest.pageCount}
        </div>
      )}

      {!atEnd && (
        <VisuallyHidden role="status" aria-live="polite">
          <Trans>
            Page {pageNumber} of {pageCount}
          </Trans>
        </VisuallyHidden>
      )}
    </div>
  )
}
