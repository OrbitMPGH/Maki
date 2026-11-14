import { memo, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { Button, Stack, Text } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { enhanceBudget } from '../../lib/enhance/budget'
import type { EnhanceFallback, EnhanceMethod, ReaderEnhance, ReaderFit } from './prefs'
import EnhancedPage from './EnhancedPage'
import NextChapterHint from './NextChapterHint'
import { useLinger } from './useLinger'
import { scrollBehavior } from '../../lib/scrollBehavior'
import { useCompareKey } from './compareHold'
import { EnhanceScope, presetFor, useEnhanceEngine, type PageEnhance, type StretchedReport } from './enhanceRuntime'
import { FULL_REGION } from './pageShape'
import type { ServerEnhance } from './useServerEnhance'
import { measureStrip, seekStrip } from './stripMeasure'
import type { StripTracker } from './stripPosition'
import { cutHeight } from './creditWindow'

const FIT_CLASS: Record<ReaderFit, string> = {
  width: 'reader-fit-width',
  height: 'reader-fit-height',
  screen: 'reader-fit-width',
  original: 'reader-fit-original',
}

// Pixels of wheel delta needed at the bottom to trigger the next chapter. Sized for a couple of
// scroll-wheel notches, not a long hold.
const PAST_END_WHEEL = 1000
// A finger drag moves by real screen pixels and only counts once the strip is already at the
// bottom, so a wheel-sized distance took two or three full swipes on a phone. One deliberate pull
// of about a quarter of the screen does it instead, with a floor for short landscape screens.
const PAST_END_TOUCH_SHARE = 0.25
const PAST_END_TOUCH_MIN = 140
// Wheel events this soon after a finger lifts belong to the same gesture, for a browser that
// reports a touch scroll both ways.
const WHEEL_AFTER_TOUCH_MS = 600
/** A touch that has gone this long without an event is taken as lost, so the wheel works again. */
const TOUCH_STALE_MS = 3000

/** A loaded page's natural size, which unloaded pages borrow as their placeholder shape. */
interface PageShape {
  width: number
  /** Height over width. */
  aspect: number
}

function median(values: number[]): number {
  const sorted = [...values].sort((a, b) => a - b)
  return sorted[Math.floor(sorted.length / 2)]
}

const ENHANCE_MAX_PAGES = 32
/** Share of the engine's budget spent on the pages ahead; the rest keeps the pages just read. */
const ENHANCE_AHEAD_SHARE = 0.75
const NO_RANGE: number[] = []

/**
 * The pages to enhance: the current one, the pages ahead in order, then the pages behind. Sized in
 * displayed device pixels against the engine's budget rather than in pages, so a chapter of a few
 * very tall slices runs a page or two ahead and one of many short slices runs far down the strip.
 * The pages either side of the current one are always in. `beyond` is how many pages past the last
 * one the ahead share still covers, counted at the size of the pages ahead. `server` is the range
 * without the pages behind that have left the screen: the server's one worker would otherwise spend
 * a tall slice's seconds on a page already read while the next one waits.
 */
function enhanceRange(
  elements: (HTMLImageElement | null)[],
  page: number,
  count: number,
): { range: number[]; beyond: number; server: number[] } {
  if (count === 0) return { range: NO_RANGE, beyond: 0, server: NO_RANGE }
  const dpr = window.devicePixelRatio || 1
  const pixels = (index: number) => {
    const rect = elements[index]?.getBoundingClientRect()
    return rect ? rect.width * rect.height * dpr * dpr : 0
  }
  const budget = enhanceBudget()
  const range = [page]
  let left = budget * ENHANCE_AHEAD_SHARE - pixels(page)
  let index = page + 1
  let spent = 0
  for (; index < count && range.length < ENHANCE_MAX_PAGES; index++) {
    const size = pixels(index)
    if (index > page + 1 && size > left) break
    range.push(index)
    left -= size
    spent += size
  }
  let beyond = 0
  if (index >= count) {
    const typical = range.length > 1 ? spent / (range.length - 1) : pixels(page)
    const fits = typical > 0 ? Math.floor(Math.max(0, left) / typical) : ENHANCE_MAX_PAGES
    beyond = Math.min(ENHANCE_MAX_PAGES, Math.max(page === count - 1 ? 1 : 0, fits))
  }
  const server = [...range]
  left = budget * (1 - ENHANCE_AHEAD_SHARE)
  for (let index = page - 1; index >= 0 && range.length < ENHANCE_MAX_PAGES; index--) {
    const size = pixels(index)
    if (index < page - 1 && size > left) break
    range.push(index)
    left -= size
    const rect = elements[index]?.getBoundingClientRect()
    if (rect && rect.bottom > 0 && rect.top < window.innerHeight) server.push(index)
  }
  return { range, beyond, server }
}

// How close to the true bottom counts as "at the bottom": scrollHeight/clientHeight are
// fractional in some browsers, so an exact `=== ` check misses by sub-pixel amounts.
const BOTTOM_EPSILON = 2
// Scroll banked towards the next chapter is dropped after this long without more of it, so a
// half-filled meter left behind does not turn the next wheel notch into a chapter jump.
const PAST_END_IDLE_MS = 1200
// The card stays hidden until the meter is this full: the tail of a fling that lands on the bottom,
// or a finger settling there, is not a request for the next chapter. Once up, it stays up until
// the meter is empty.
const HINT_SHOW_AT = 0.15
// How long the card stays after the meter empties, fade included (theme.css, `[data-leaving]`).
// Scrolling back down within it picks the same card up again instead of flashing a new one in.
const HINT_LINGER_MS = 600
// A full meter holds until the next chapter's pages arrive. If they never do (a failed load), it
// lets go after this long so the strip is not stuck.
const LEAVING_GIVE_UP_MS = 8000
// After a jump, wheel input only counts again once it pauses this long, so the rest of the fling
// that filled the meter does not start filling it again.
const WHEEL_GESTURE_GAP_MS = 250
const WHEEL_LINE_PX = 40

interface StripPageProps {
  index: number
  /** The page's index in the chapter, which names its server upscale. */
  chapterPage: number
  src: string
  label: string
  fit: ReaderFit
  scale: number
  tone: string | undefined
  eager: boolean
  /** Set only for pages in the eager window while Enhance is on. */
  enhance: PageEnhance | null
  /** Set with `enhance` while a server method is in use. */
  server: ServerEnhance | null
  /** Distance from the current page in reading order; pages after it come first. */
  priority: number
  register: (index: number, element: HTMLImageElement | null) => void
  onSettled: (index: number, element: HTMLImageElement) => void
}

/**
 * One page of the strip. Memoised with stable callbacks so a page change, which re-renders the
 * view on every scroll step, only touches the few pages whose eager window moved.
 */
const StripPage = memo(function StripPage({
  index,
  chapterPage,
  src,
  label,
  fit,
  scale,
  tone,
  eager,
  enhance,
  server,
  priority,
  register,
  onSettled,
}: StripPageProps) {
  const { t } = useLingui()
  const image = useRef<HTMLImageElement | null>(null)
  const [failed, setFailed] = useState(false)
  const [loaded, setLoaded] = useState<HTMLImageElement | null>(null)
  const [covered, setCovered] = useState(false)
  const [attempt, setAttempt] = useState(0)
  const pageNumber = chapterPage + 1
  const url = attempt === 0 ? src : `${src}${src.includes('?') ? '&' : '?'}retry=${attempt}`

  const setRef = useCallback(
    (element: HTMLImageElement | null) => {
      image.current = element
      register(index, element)
    },
    [index, register],
  )

  const retry = () => {
    image.current?.removeAttribute('data-loaded')
    setLoaded(null)
    setFailed(false)
    setAttempt((n) => n + 1)
  }

  const alt = t`${label} - page ${pageNumber}`
  return (
    <>
      <img
        ref={setRef}
        data-page={index}
        data-no-cover-fallback
        src={url}
        alt={alt}
        // Stays painted under the enhanced page: Firefox snaps a canvas's edges on its own, worst
        // while scrolling, and a seam that opens between two pages then shows this page, not the background.
        aria-hidden={enhance && covered ? true : undefined}
        className={`reader-page ${FIT_CLASS[fit]}`}
        style={{
          ...(fit === 'original' && scale !== 100 ? { zoom: scale / 100 } : undefined),
          ...(tone ? { filter: tone } : undefined),
          ...(failed ? { display: 'none' } : undefined),
        }}
        // A window around the current page rather than the whole prefix: resuming at page 300
        // of a webtoon strip would otherwise fetch and decode 300 pages at once. Only the
        // pages close enough to shift the target's offset need forcing. Unloaded pages hold a
        // min-height placeholder (theme.css), which is what gives lazy loading real positions.
        loading={eager ? 'eager' : 'lazy'}
        decoding="async"
        draggable={false}
        onLoad={(event) => {
          setLoaded(event.currentTarget)
          onSettled(index, event.currentTarget)
        }}
        onError={(event) => {
          setFailed(true)
          onSettled(index, event.currentTarget)
        }}
      />
      {enhance && loaded && !failed && (
        <EnhancedPage
          image={loaded}
          src={src}
          region={FULL_REGION}
          rotate={0}
          alt={alt}
          tone={tone}
          enhance={enhance}
          page={chapterPage}
          server={server}
          priority={priority}
          box={loaded}
          overlay
          onShowing={setCovered}
        />
      )}
      {failed && (
        <Stack align="center" gap="xs" py="xl">
          <Text c="var(--ink-3)" size="sm">
            <Trans>This page failed to load.</Trans>
          </Text>
          <Button variant="light" size="xs" onClick={retry}>
            <Trans>Try again</Trans>
          </Button>
        </Stack>
      )}
    </>
  )
})

/**
 * The last page of the strip, which may end part way down: with `cut` set, the share of the page
 * below the cut is not laid out, so the strip, its end and the next-chapter meter end there. The
 * image keeps its full size and URL inside, so positions stay shares of the real page. Always the
 * wrapper for the last page, so a cut that arrives late does not remount the image.
 */
function CutPage({ cut, children }: { cut: number; children: React.ReactNode }) {
  const box = useRef<HTMLDivElement>(null)
  const [height, setHeight] = useState<number | null>(null)
  useLayoutEffect(() => {
    const image = box.current?.querySelector('img')
    if (!image || cut <= 0) {
      setHeight(null)
      return
    }
    const measure = () => {
      const full = image.getBoundingClientRect().height
      // A page that failed to load has no height; its retry button is left alone.
      setHeight(full > 0 ? cutHeight(full, cut) : null)
    }
    measure()
    const observer = new ResizeObserver(measure)
    observer.observe(image)
    return () => observer.disconnect()
  }, [cut])
  return (
    <div ref={box} className="reader-cut" data-cut={height != null ? true : undefined} style={height != null ? { height } : undefined}>
      {children}
    </div>
  )
}

/**
 * The webtoon strip: every page stacked, scrolled continuously. The current page is whichever
 * one owns the middle of the viewport, tracked with an IntersectionObserver rather than a scroll
 * handler so long chapters don't run layout maths on every frame.
 */
export default function ContinuousView({
  urls,
  first = 0,
  page: chapterPage,
  onPageChange: onChapterPageChange,
  seekVersion,
  seekOffset = 0,
  seekSmooth = false,
  onPastEnd,
  hasNext,
  nextLabel,
  fit,
  scale,
  tone,
  gap,
  label,
  pastEndLabel,
  enhance = 'off',
  enhanceMethod = 'a4k-m',
  enhanceFallback = 'a4k-m',
  server = null,
  onStretched,
  onEnhanceRange,
  tracker,
  lastCut = 0,
}: {
  /** The pages shown: the chapter's from `first` on, past any credit pages left out. */
  urls: string[]
  /** The chapter index of `urls[0]`. Every page index this view takes or reports is the chapter's. */
  first?: number
  page: number
  onPageChange: (page: number) => void
  /** Bumped by the parent on every *explicit* navigation (resume, toolbar scrub, page-strip
   *  click, Home/End); this is what triggers a scroll. Plain `page` changes driven by this
   *  view's own scroll tracking must NOT scroll, or the view would fight the user's scroll. */
  seekVersion: number
  /** How far down the target page (0 to 1) the seek lands, for a skip that ends mid-page. */
  seekOffset?: number
  /** Animate the seek instead of jumping, for a skip the reader asked for. */
  seekSmooth?: boolean
  /** Fired once the bottom-of-strip meter fills, the strip's analogue of turning past the last page
   *  in paged mode. Returning true means the next chapter is on its way: the meter then stays full
   *  and takes no more input until `urls` change. */
  onPastEnd: () => boolean | void
  /** Picks which bottom-of-strip prompt shows: the fillable "scroll for next chapter" meter, or,
   *  on the last chapter, an inert "no more chapters" one that never advances anywhere. */
  hasNext: boolean
  /** The next chapter's label, which the meter names as its target. */
  nextLabel?: string | null
  /** What the fillable meter says; the Discover preview has no next chapter to scroll into. */
  pastEndLabel?: React.ReactNode
  fit: ReaderFit
  /** Percent scale on top of the '1:1' fit; ignored for the other fits. */
  scale: number
  /** CSS filter for the page images (dim and colour), from `toneFilter`. */
  tone?: string
  gap: number
  label: string
  enhance?: ReaderEnhance
  enhanceMethod?: EnhanceMethod
  /** What a server method shows until its page is ready there. */
  enhanceFallback?: EnhanceFallback
  /** Set while a server method is in use and the server tier can serve it. */
  server?: ServerEnhance | null
  /** The pages `auto` enhances: shown at 1.2x their size or more as measured on screen, or soft. */
  onStretched?: StretchedReport
  /**
   * The pages being enhanced, current first, then ahead, then behind. `beyond` is how many of the
   * next chapter's pages the window would reach past the last one. `server` is what the server is
   * asked for: the same pages minus those behind that are off screen.
   */
  onEnhanceRange?: (pages: number[], beyond: number, server: number[]) => void
  /** Receives where the strip is scrolled to, and lends it a way to scroll there. */
  tracker?: StripTracker
  /** The share of the last page, from its bottom, that is left out: a credit banner fused into it. */
  lastCut?: number
}) {
  // Inside, pages count from the first one shown.
  const page = chapterPage - first
  const onPageChange = useCallback((index: number) => onChapterPageChange(first + index), [onChapterPageChange, first])
  const container = useRef<HTMLDivElement>(null)
  const pages = useRef<(HTMLImageElement | null)[]>([])
  const sentinel = useRef<HTMLDivElement>(null)
  // Mirrors `pastEndProgress` state (0 to 1) without the render lag, so consecutive wheel/touch
  // events in the same frame accumulate correctly instead of each reading a stale 0.
  const progress = useRef(0)
  const [pastEndProgress, setPastEndProgress] = useState(0)
  const hintShown = useRef(false)
  const [hintOn, setHintOn] = useState(false)
  const setMeter = useCallback((value: number) => {
    progress.current = value
    setPastEndProgress(value)
    hintShown.current = value >= HINT_SHOW_AT || (hintShown.current && value > 0)
    setHintOn(hintShown.current)
  }, [])
  const hintMounted = useLinger(hintOn, HINT_LINGER_MS)
  // A finger is pulling the meter, which fills by distance and moves on only on release.
  const [pulling, setPulling] = useState(false)
  // The meter filled and the next chapter is loading.
  const leaving = useRef(false)
  const [opening, setOpening] = useState(false)
  // Wheel input is ignored until it pauses, set on every jump. Both outlive the listener effect,
  // which runs again for the next chapter while the fling is still going.
  const wheelHeld = useRef(false)
  const lastWheelAt = useRef(0)
  const onPastEndRef = useRef(onPastEnd)
  onPastEndRef.current = onPastEnd
  const hasNextRef = useRef(hasNext)
  hasNextRef.current = hasNext
  // Read fresh inside the seek effect below without making `page` itself a dependency: the band
  // tracker updates `page` continuously while scrolling, and re-running the seek effect on every
  // one of those would re-scroll to wherever the user just scrolled from.
  const pageRef = useRef(page)
  pageRef.current = page

  // Only the enhance range around the current page is enhanced; a page leaving it drops its canvas
  // and its cached result.
  const enhanceOn = enhance !== 'off'
  const preset = presetFor(enhanceMethod, enhanceFallback)
  const engineState = useEnhanceEngine(enhanceOn && preset !== null)
  const engine = engineState.status === 'ready' ? engineState.engine : null
  useCompareKey(enhanceOn)
  const stretchedRef = useRef(onStretched)
  stretchedRef.current = onStretched
  const [scope] = useState(() => new EnhanceScope(undefined, (wanted, plain) => stretchedRef.current?.(wanted, plain)))
  useEffect(() => {
    scope.attach(engine)
    return () => scope.release()
  }, [scope, engine, urls])
  useEffect(() => () => scope.forgetPages(), [scope, urls])
  const layout = `${fit}|${scale}`
  const serverOn = server !== null
  const pageEnhance = useMemo<PageEnhance | null>(
    () =>
      (engine || serverOn) && enhance !== 'off'
        ? { mode: enhance, preset, engine, scope, layout }
        : null,
    [engine, serverOn, enhance, preset, scope, layout],
  )
  const [range, setRange] = useState<number[]>(NO_RANGE)
  const [beyond, setBeyond] = useState(0)
  const [serverRange, setServerRange] = useState<number[]>(NO_RANGE)
  const inRange = useMemo(() => new Set(range), [range])
  const isEager = (index: number) => index < 3 || Math.abs(index - page) <= 2 || inRange.has(index)
  useEffect(() => {
    scope.retain(new Set(range.map((index) => urls[index])))
  }, [scope, urls, range])
  const rangeRef = useRef(onEnhanceRange)
  rangeRef.current = onEnhanceRange
  useEffect(() => {
    const toChapter = (list: number[]) => (first === 0 ? list : list.map((index) => first + index))
    rangeRef.current?.(toChapter(range), beyond, toChapter(serverRange))
  }, [range, beyond, serverRange, first])

  // Set by the sentinel effect below; image load handlers re-run it once a page settles.
  const reportEnd = useRef<() => void>(() => {})

  // Unloaded pages take the median shape of the pages loaded so far, so the strip is close to its
  // real length long before most pages arrive: the scrollbar says where you are and dragging it
  // does not chase a strip that grows under it. Chapters nearly always use one page size, so after
  // the first page the estimate is usually exact. Until then the CSS placeholder holds the room.
  const samples = useRef<{ widths: number[]; aspects: number[] }>({ widths: [], aspects: [] })
  const [shape, setShape] = useState<PageShape | null>(null)

  // Measured again whenever a page or the layout moves the strip; pages not loaded yet are counted
  // at their placeholder size, which borrows the loaded pages' shape.
  useEffect(() => {
    if (!enhanceOn) {
      setRange(NO_RANGE)
      setBeyond(0)
      setServerRange(NO_RANGE)
      return
    }
    const update = () => {
      const next = enhanceRange(pages.current, Math.min(page, urls.length - 1), urls.length)
      const same = (previous: number[], pages: number[]) =>
        previous.length === pages.length && previous.every((index, at) => index === pages[at]) ? previous : pages
      setRange((previous) => same(previous, next.range))
      setServerRange((previous) => same(previous, next.server))
      setBeyond(next.beyond)
    }
    update()
    window.addEventListener('resize', update)
    return () => window.removeEventListener('resize', update)
  }, [enhanceOn, urls, page, shape, fit, scale, gap])

  useEffect(() => {
    leaving.current = false
    setOpening(false)
    setPulling(false)
    setMeter(0)
    samples.current = { widths: [], aspects: [] }
    setShape(null)
  }, [urls, setMeter])

  useEffect(() => {
    if (!opening) return
    const timer = setTimeout(() => {
      leaving.current = false
      setOpening(false)
      setMeter(0)
    }, LEAVING_GIVE_UP_MS)
    return () => clearTimeout(timer)
  }, [opening, setMeter])

  // Pages before the seek target whose load can still shift the target's offset. Re-scroll keeps
  // firing while this is non-empty; cleared once every preceding page has reported loaded (or the
  // seek target changes). Without this, a resume straight after a hard refresh (no browser image
  // cache, nothing pre-loaded from the same-session scroll-through) lands on the target element's
  // pre-layout offset, then the page silently drifts as still-lazy images above it finish loading.
  const settling = useRef<Set<number>>(new Set())
  // The page the last seek asked for. Held separately from `pageRef`, which the band tracker
  // rewrites as the strip reflows: re-scrolling to *that* would chase the drift instead of
  // correcting it, landing on (and then saving) whatever page the shift happened to expose.
  const seekTarget = useRef(0)
  const seekShare = useRef(0)
  const seekAnimated = useRef(false)
  const seekShareRef = useRef(seekOffset)
  seekShareRef.current = seekOffset
  const seekSmoothRef = useRef(seekSmooth)
  seekSmoothRef.current = seekSmooth

  const scrollToSeek = useCallback((smooth: boolean) => {
    const element = pages.current[seekTarget.current]
    if (!element) return
    const scroller = container.current?.parentElement
    if (seekShare.current <= 0 || !scroller) {
      element.scrollIntoView(smooth ? { block: 'start', behavior: scrollBehavior() } : { block: 'start' })
      return
    }
    const box = element.getBoundingClientRect()
    const top = box.top - scroller.getBoundingClientRect().top + scroller.scrollTop + box.height * seekShare.current
    scroller.scrollTo(smooth ? { top, behavior: scrollBehavior() } : { top })
  }, [])

  // Scrolls to the target page on every explicit seek, the initial resume included, since that's
  // just the first seek the parent issues once the manifest's saved position lands.
  useEffect(() => {
    if (seekVersion === 0 || urls.length === 0) return
    const target = pageRef.current
    seekTarget.current = target
    seekShare.current = seekShareRef.current
    seekAnimated.current = seekSmoothRef.current
    scrollToSeek(seekAnimated.current)
    // A landing partway down the target also moves with the target's own height.
    const last = seekShare.current > 0 ? target + 1 : target
    settling.current = new Set(
      Array.from({ length: last }, (_, index) => index).filter(
        (index) => !pages.current[index]?.complete,
      ),
    )
  }, [seekVersion, urls, scrollToSeek])

  // Pages not loaded yet borrow the loaded pages' shape, so a new shape moves every placeholder
  // above the target at once. Scroll anchoring covers that where the browser has it; Safari does not.
  useEffect(() => {
    if (settling.current.size > 0) scrollToSeek(seekAnimated.current)
  }, [shape, scrollToSeek])

  const onPageSettled = useCallback((index: number, element: HTMLImageElement) => {
    element.dataset.loaded = 'true'
    if (element.naturalWidth > 0 && element.naturalHeight > 0) {
      const { widths, aspects } = samples.current
      widths.push(element.naturalWidth)
      aspects.push(element.naturalHeight / element.naturalWidth)
      const next = { width: median(widths), aspect: median(aspects) }
      // Only a real change re-renders: each load of a same-sized page leaves the medians alone.
      setShape((prev) =>
        prev && prev.width === next.width && Math.abs(prev.aspect - next.aspect) < 0.005 ? prev : next,
      )
    }
    reportEnd.current()
    if (!settling.current.delete(index)) return
    scrollToSeek(seekAnimated.current)
  }, [scrollToSeek])

  const registerPage = useCallback((index: number, element: HTMLImageElement | null) => {
    pages.current[index] = element
  }, [])

  useEffect(() => {
    if (urls.length === 0) return

    // Track the whole intersecting set and take the topmost, rather than whichever entry the
    // callback happened to see last. While images are still loading they have no height yet, so
    // several stack inside the band at once and "last wins" reports a page further down than the
    // one actually on screen.
    const intersecting = new Set<number>()
    const observer = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          const index = Number((entry.target as HTMLElement).dataset.page)
          if (!Number.isFinite(index)) continue
          if (entry.isIntersecting) intersecting.add(index)
          else intersecting.delete(index)
        }
        if (intersecting.size > 0) onPageChange(Math.min(...intersecting))
      },
      // A thin band across the middle of the VIEWPORT. Not the content element: that isn't the
      // scroller (`.reader-surface` is), so a percentage rootMargin against it would carve a band
      // out of the middle of the whole chapter and report page ~10 of 20 while page 1 is on screen.
      { rootMargin: '-45% 0px -45% 0px', threshold: 0 },
    )

    for (const element of pages.current) {
      if (element) observer.observe(element)
    }
    return () => observer.disconnect()
  }, [urls, onPageChange])

  // The band tracker above answers "what's in the middle of the screen", which a short last page
  // (e.g. a small end-of-chapter credit image) can fail to ever reach: it never crosses the
  // center band, so the count sticks on the previous, taller page even once the strip is fully
  // scrolled. A 1px sentinel right after the last page catches that: it enters the viewport only
  // once the strip is scrolled essentially to its end, at which point the last page is current
  // regardless of the band. Until the images around the viewport have settled the strip is
  // collapsed and the sentinel sits in view at the top, so it only counts once nothing that could
  // still move it is waiting for its size. Lazy pages that were skipped over stay unrequested, so
  // only images at or below the viewport top are waited on.
  useEffect(() => {
    if (urls.length === 0 || !sentinel.current) return
    const target = sentinel.current
    reportEnd.current = () => {
      const scroller = container.current?.parentElement
      if (!scroller) return
      const view = scroller.getBoundingClientRect()
      const end = target.getBoundingClientRect()
      if (end.bottom <= view.top || end.top >= view.bottom) return
      const pending = pages.current
        .slice(0, urls.length)
        .some((element) => element && !element.complete && element.getBoundingClientRect().bottom >= view.top)
      if (!pending) onPageChange(urls.length - 1)
    }
    const observer = new IntersectionObserver(() => reportEnd.current(), { threshold: 0 })
    observer.observe(target)
    return () => {
      observer.disconnect()
      reportEnd.current = () => {}
    }
  }, [urls, onPageChange])

  const remeasure = useRef<() => void>(() => {})
  useEffect(() => {
    const scroller = container.current?.parentElement
    if (!tracker || !scroller || urls.length === 0) return
    let frame = 0
    const measure = () => {
      frame = 0
      const place = measureStrip(scroller, pages.current, Math.min(pageRef.current, urls.length - 1), urls.length)
      tracker.set(place && first > 0 ? { ...place, page: first + place.page } : place)
    }
    const schedule = () => {
      if (frame === 0) frame = requestAnimationFrame(measure)
    }
    tracker.seek = (progress) => {
      // A drag on the rail is the reader scrolling by hand: the resume landing is history.
      settling.current.clear()
      seekStrip(scroller, pages.current, urls.length, progress)
    }
    remeasure.current = schedule
    schedule()
    scroller.addEventListener('scroll', schedule, { passive: true })
    window.addEventListener('resize', schedule)
    return () => {
      cancelAnimationFrame(frame)
      scroller.removeEventListener('scroll', schedule)
      window.removeEventListener('resize', schedule)
      remeasure.current = () => {}
      tracker.seek = null
      tracker.set(null)
    }
  }, [tracker, urls, first])
  // The band tracker moves the page a moment after the scroll that moved it.
  useEffect(() => remeasure.current(), [page])

  // The bottom-of-strip "scroll for next chapter" meter. `.reader-surface` clamps scrollTop at
  // the true max (there's no native overscroll to detect), so instead this reads wheel deltas and
  // finger positions directly and only counts them while already at the bottom, the same way
  // Kavita's reader does it.
  useEffect(() => {
    if (urls.length === 0) return
    const scroller = container.current?.parentElement
    if (!scroller) return

    const fromBottom = () => scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight
    const atBottom = () => fromBottom() < BOTTOM_EPSILON
    // A page at the end still loading will move the bottom when it lands (a credits image is rarely
    // the size the placeholder guessed), so the meter waits for it rather than fill and then reset.
    // Pages skipped above the viewport do not count: scroll anchoring keeps them from moving it.
    const settled = () => {
      const top = scroller.getBoundingClientRect().top
      for (let index = urls.length - 1; index >= 0; index--) {
        const element = pages.current[index]
        if (!element) continue
        if (element.getBoundingClientRect().bottom < top) return true
        if (!element.complete) return false
      }
      return true
    }
    // The bottom moved away under a partly filled meter (a page grew) while the reader is still
    // heading down: hold the fill until they reach the new bottom instead of starting over.
    const grewUnder = () => progress.current > 0 && fromBottom() < scroller.clientHeight

    let idle: ReturnType<typeof setTimeout> | undefined
    const decay = () => setMeter(0)

    // While the parent is leaving, the meter stays full and every input is ignored. A refusal (the
    // reader is still settling into this chapter) empties it.
    const fire = () => {
      clearTimeout(idle)
      wheelHeld.current = true
      if (onPastEndRef.current() === true) {
        leaving.current = true
        setOpening(true)
        setMeter(1)
      } else {
        decay()
      }
    }

    // `delta` is in pixels, `distance` how many of them this input needs for a full meter.
    const advance = (delta: number, distance: number) => {
      clearTimeout(idle)
      if (delta > 0 && (!atBottom() ? grewUnder() : !settled())) {
        idle = setTimeout(decay, PAST_END_IDLE_MS)
        return
      }
      if (delta <= 0 || !atBottom()) {
        if (progress.current !== 0) decay()
        return
      }
      setMeter(Math.min(1, progress.current + delta / distance))
      // On the last chapter the card only says the strip has run out, so a full meter goes nowhere.
      if (hasNextRef.current && progress.current >= 1) {
        fire()
        return
      }
      idle = setTimeout(decay, PAST_END_IDLE_MS)
    }

    // Once the reader has scrolled by hand, the pending seek is history: a later image load must
    // not yank them back to where the resume landed.
    const abandonSeek = () => settling.current.clear()

    // Touch is pull and release. Safari on iPad delivers a scrolling finger's moves late and in
    // bursts, so a meter that summed them crawled, then filled on the burst at release and changed
    // chapter unasked. Here the meter follows how far the finger is from where the strip hit the
    // bottom, so a late event still shows the true pull, and only letting go with it full moves on.
    let touching = false
    let touchEndedAt = 0
    let touchSeenAt = 0
    // A second finger turns the gesture into a pinch, which must never fill the meter.
    let multiTouch = false
    let pullFrom: number | null = null
    const touchDistance = () => Math.max(PAST_END_TOUCH_MIN, scroller.clientHeight * PAST_END_TOUCH_SHARE)

    const onWheel = (event: WheelEvent) => {
      abandonSeek()
      const now = performance.now()
      const sinceLast = now - lastWheelAt.current
      lastWheelAt.current = now
      if (leaving.current) return
      if (wheelHeld.current) {
        if (sinceLast < WHEEL_GESTURE_GAP_MS) return
        wheelHeld.current = false
      }
      if ((touching && now - touchSeenAt < TOUCH_STALE_MS) || now - touchEndedAt < WHEEL_AFTER_TOUCH_MS) return
      // deltaY is read before deltaMode: Firefox then reports pixels rather than lines.
      const delta = event.deltaY
      const unit = event.deltaMode === 1 ? WHEEL_LINE_PX : event.deltaMode === 2 ? scroller.clientHeight : 1
      advance(delta * unit, PAST_END_WHEEL)
    }

    const onTouchStart = (event: TouchEvent) => {
      abandonSeek()
      touching = true
      touchSeenAt = performance.now()
      if (leaving.current) return
      clearTimeout(idle)
      multiTouch = event.touches.length > 1
      const y = event.touches[0]?.clientY
      pullFrom = y != null && !multiTouch && atBottom() && settled() ? y : null
      if (multiTouch && progress.current !== 0) decay()
      setPulling(true)
    }
    const onTouchMove = (event: TouchEvent) => {
      touchSeenAt = performance.now()
      if (leaving.current) return
      if (event.touches.length > 1) multiTouch = true
      if (multiTouch) {
        pullFrom = null
        if (progress.current !== 0) decay()
        return
      }
      const y = event.touches[0]?.clientY
      if (y == null) return
      if (!atBottom() || !settled()) {
        pullFrom = null
        if (progress.current !== 0 && !(atBottom() || grewUnder())) decay()
        return
      }
      // Picks up a fill held while the strip grew from where it was.
      pullFrom ??= y + progress.current * touchDistance()
      const pull = pullFrom - y
      if (pull <= 0) {
        if (progress.current !== 0) decay()
        return
      }
      setMeter(Math.min(1, pull / touchDistance()))
    }
    const endTouch = (release: boolean) => {
      const full = release && !multiTouch && hasNextRef.current && progress.current >= 1
      touching = false
      multiTouch = false
      touchEndedAt = performance.now()
      pullFrom = null
      if (leaving.current) return
      setPulling(false)
      if (full) fire()
      else if (progress.current !== 0) decay()
    }
    // A finger lifted while another is still down is not the end of the gesture.
    const onTouchEnd = (event: TouchEvent) => {
      if (event.touches.length === 0) endTouch(true)
    }
    const onTouchCancel = () => endTouch(false)
    // The end of a touch never arrives when the page loses focus mid-gesture.
    const onLostFocus = () => {
      if (touching) endTouch(false)
    }
    const onVisibility = () => {
      if (document.hidden) onLostFocus()
    }

    scroller.addEventListener('wheel', onWheel, { passive: true })
    scroller.addEventListener('touchstart', onTouchStart, { passive: true })
    scroller.addEventListener('touchmove', onTouchMove, { passive: true })
    scroller.addEventListener('touchend', onTouchEnd, { passive: true })
    scroller.addEventListener('touchcancel', onTouchCancel, { passive: true })
    window.addEventListener('blur', onLostFocus)
    document.addEventListener('visibilitychange', onVisibility)
    return () => {
      clearTimeout(idle)
      scroller.removeEventListener('wheel', onWheel)
      scroller.removeEventListener('touchstart', onTouchStart)
      scroller.removeEventListener('touchmove', onTouchMove)
      scroller.removeEventListener('touchend', onTouchEnd)
      scroller.removeEventListener('touchcancel', onTouchCancel)
      window.removeEventListener('blur', onLostFocus)
      document.removeEventListener('visibilitychange', onVisibility)
    }
  }, [urls, setMeter])

  return (
    <>
      <div
        className="reader-continuous"
        ref={container}
        // Only a deliberate zoom past 100% is allowed to make a page wider than the strip; see
        // the width clamp in theme.css.
        data-zoomed={fit === 'original' && scale > 100}
        data-shaped={shape ? true : undefined}
        style={{
          gap: `${gap}px`,
          ...(shape && {
            '--page-aspect': String(shape.aspect),
            '--page-width': `${shape.width}px`,
          }),
        } as React.CSSProperties}
      >
        {urls.map((src, index) => {
          const strip = (
            <StripPage
              key={src}
              index={index}
              chapterPage={first + index}
              src={src}
              label={label}
              fit={fit}
              scale={scale}
              tone={tone}
              eager={isEager(index)}
              enhance={pageEnhance && inRange.has(index) ? pageEnhance : null}
              server={pageEnhance && inRange.has(index) ? server : null}
              priority={pageEnhance && inRange.has(index) ? (index >= page ? (index - page) * 2 : (page - index) * 2 + 1) : 0}
              register={registerPage}
              onSettled={onPageSettled}
            />
          )
          return index === urls.length - 1 ? (
            <CutPage key={src} cut={lastCut}>
              {strip}
            </CutPage>
          ) : (
            strip
          )
        })}
        <div ref={sentinel} style={{ height: 1 }} />
      </div>
      {hintMounted && (
        <NextChapterHint
          progress={pastEndProgress}
          pulling={pulling}
          opening={opening}
          leaving={!hintOn}
          atEnd={!hasNext}
          nextLabel={nextLabel}
          fallbackLabel={pastEndLabel}
        />
      )}
    </>
  )
}
