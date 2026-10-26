import { memo, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Button, Stack, Text } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { enhanceBudget } from '../../lib/enhance/budget'
import type { EnhanceMethod, ReaderEnhance, ReaderFit } from './prefs'
import EnhancedPage from './EnhancedPage'
import { useCompareKey } from './compareHold'
import { EnhanceScope, presetFor, useEnhanceEngine, type PageEnhance } from './enhanceRuntime'
import { FULL_REGION } from './pageShape'
import type { ServerEnhance } from './useServerEnhance'

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
 * The pages either side of the current one are always in.
 */
function enhanceRange(elements: (HTMLImageElement | null)[], page: number, count: number): number[] {
  if (count === 0) return NO_RANGE
  const dpr = window.devicePixelRatio || 1
  const pixels = (index: number) => {
    const rect = elements[index]?.getBoundingClientRect()
    return rect ? rect.width * rect.height * dpr * dpr : 0
  }
  const budget = enhanceBudget()
  const range = [page]
  let left = budget * ENHANCE_AHEAD_SHARE - pixels(page)
  for (let index = page + 1; index < count && range.length < ENHANCE_MAX_PAGES; index++) {
    const size = pixels(index)
    if (index > page + 1 && size > left) break
    range.push(index)
    left -= size
  }
  left = budget * (1 - ENHANCE_AHEAD_SHARE)
  for (let index = page - 1; index >= 0 && range.length < ENHANCE_MAX_PAGES; index--) {
    const size = pixels(index)
    if (index < page - 1 && size > left) break
    range.push(index)
    left -= size
  }
  return range
}

// How close to the true bottom counts as "at the bottom": scrollHeight/clientHeight are
// fractional in some browsers, so an exact `=== ` check misses by sub-pixel amounts.
const BOTTOM_EPSILON = 2
// Scroll banked towards the next chapter is dropped after this long without more of it, so a
// half-filled meter left behind does not turn the next wheel notch into a chapter jump.
const PAST_END_IDLE_MS = 1200

interface StripPageProps {
  index: number
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
  const pageNumber = index + 1
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
        className={`reader-page ${FIT_CLASS[fit]}`}
        style={{
          ...(fit === 'original' && scale !== 100 ? { zoom: scale / 100 } : undefined),
          ...(tone ? { filter: tone } : undefined),
          ...(failed ? { display: 'none' } : undefined),
          ...(enhance && covered ? { visibility: 'hidden' } : undefined),
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
          page={index}
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
 * The webtoon strip: every page stacked, scrolled continuously. The current page is whichever
 * one owns the middle of the viewport, tracked with an IntersectionObserver rather than a scroll
 * handler so long chapters don't run layout maths on every frame.
 */
export default function ContinuousView({
  urls,
  page,
  onPageChange,
  seekVersion,
  onPastEnd,
  hasNext,
  fit,
  scale,
  tone,
  gap,
  label,
  pastEndLabel,
  enhance = 'off',
  enhanceMethod = 'a4k-m',
  server = null,
  onStretched,
  onEnhanceRange,
}: {
  urls: string[]
  page: number
  onPageChange: (page: number) => void
  /** Bumped by the parent on every *explicit* navigation (resume, toolbar scrub, page-strip
   *  click, Home/End); this is what triggers a scroll. Plain `page` changes driven by this
   *  view's own scroll tracking must NOT scroll, or the view would fight the user's scroll. */
  seekVersion: number
  /** Fired once the bottom-of-strip progress bar fills, the strip's analogue of turning past the
   *  last page in paged mode. */
  onPastEnd: () => void
  /** Picks which bottom-of-strip prompt shows: the fillable "scroll for next chapter" meter, or,
   *  on the last chapter, an inert "no more chapters" one that never advances anywhere. */
  hasNext: boolean
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
  /** Set while a server method is in use and the server tier can serve it. */
  server?: ServerEnhance | null
  /** The pages shown at 1.2x their size or more, as measured on screen. */
  onStretched?: (pages: ReadonlySet<number>) => void
  /** The pages being enhanced, current first, then ahead, then behind; what the server is asked for. */
  onEnhanceRange?: (pages: number[]) => void
}) {
  const container = useRef<HTMLDivElement>(null)
  const pages = useRef<(HTMLImageElement | null)[]>([])
  const sentinel = useRef<HTMLDivElement>(null)
  // Mirrors `pastEndProgress` state (0 to 1) without the render lag, so consecutive wheel/touch
  // events in the same frame accumulate correctly instead of each reading a stale 0.
  const progress = useRef(0)
  const [pastEndProgress, setPastEndProgress] = useState(0)
  // A finger is pulling the meter, which fills by distance and moves on only on release.
  const [pulling, setPulling] = useState(false)
  // The last chapter's counterpart to `pastEndProgress`: shown under the same conditions, but it
  // only ever says the strip has run out, so nothing accumulates and nothing fires.
  const [atLibraryEnd, setAtLibraryEnd] = useState(false)
  // Read fresh inside the seek effect below without making `page` itself a dependency: the band
  // tracker updates `page` continuously while scrolling, and re-running the seek effect on every
  // one of those would re-scroll to wherever the user just scrolled from.
  const pageRef = useRef(page)
  pageRef.current = page

  // Only the enhance range around the current page is enhanced; a page leaving it drops its canvas
  // and its cached result.
  const enhanceOn = enhance !== 'off'
  const engineState = useEnhanceEngine(enhanceOn)
  const engine = engineState.status === 'ready' ? engineState.engine : null
  useCompareKey(enhanceOn)
  const stretchedRef = useRef(onStretched)
  stretchedRef.current = onStretched
  const [scope] = useState(() => new EnhanceScope(undefined, (pages) => stretchedRef.current?.(pages)))
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
        ? { mode: enhance, preset: presetFor(enhanceMethod), engine, scope, layout }
        : null,
    [engine, serverOn, enhance, enhanceMethod, scope, layout],
  )
  const [range, setRange] = useState<number[]>(NO_RANGE)
  const inRange = useMemo(() => new Set(range), [range])
  const isEager = (index: number) => index < 3 || Math.abs(index - page) <= 2 || inRange.has(index)
  useEffect(() => {
    scope.retain(new Set(range.map((index) => urls[index])))
  }, [scope, urls, range])
  const rangeRef = useRef(onEnhanceRange)
  rangeRef.current = onEnhanceRange
  useEffect(() => {
    rangeRef.current?.(range)
  }, [range])

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
      return
    }
    const update = () => {
      const next = enhanceRange(pages.current, Math.min(page, urls.length - 1), urls.length)
      setRange((previous) =>
        previous.length === next.length && previous.every((index, at) => index === next[at]) ? previous : next,
      )
    }
    update()
    window.addEventListener('resize', update)
    return () => window.removeEventListener('resize', update)
  }, [enhanceOn, urls, page, shape, fit, scale, gap])

  useEffect(() => {
    progress.current = 0
    setPastEndProgress(0)
    setAtLibraryEnd(false)
    samples.current = { widths: [], aspects: [] }
    setShape(null)
  }, [urls])

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

  // Scrolls to the target page on every explicit seek, the initial resume included, since that's
  // just the first seek the parent issues once the manifest's saved position lands.
  useEffect(() => {
    if (seekVersion === 0 || urls.length === 0) return
    const target = pageRef.current
    seekTarget.current = target
    pages.current[target]?.scrollIntoView({ block: 'start' })
    settling.current = new Set(
      Array.from({ length: target }, (_, index) => index).filter(
        (index) => !pages.current[index]?.complete,
      ),
    )
  }, [seekVersion, urls])

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
    pages.current[seekTarget.current]?.scrollIntoView({ block: 'start' })
  }, [])

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

  // The bottom-of-strip "scroll for next chapter" meter. `.reader-surface` clamps scrollTop at
  // the true max (there's no native overscroll to detect), so instead this reads wheel deltas and
  // finger positions directly and only counts them while already at the bottom, the same way
  // Kavita's reader does it.
  useEffect(() => {
    if (urls.length === 0) return
    const scroller = container.current?.parentElement
    if (!scroller) return

    const atBottom = () =>
      scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight < BOTTOM_EPSILON

    let idle: ReturnType<typeof setTimeout> | undefined
    const decay = () => {
      progress.current = 0
      setPastEndProgress(0)
    }

    // `delta` is in pixels, `distance` how many of them this input needs for a full meter.
    const advance = (delta: number, distance: number) => {
      clearTimeout(idle)
      if (delta <= 0 || !atBottom()) {
        setAtLibraryEnd(false)
        if (progress.current !== 0) {
          progress.current = 0
          setPastEndProgress(0)
        }
        return
      }
      // Nothing to scroll into, so the prompt appears on the same gesture but stays inert: no
      // meter, no navigation.
      if (!hasNext) {
        setAtLibraryEnd(true)
        return
      }
      progress.current = Math.min(1, progress.current + delta / distance)
      setPastEndProgress(progress.current)
      if (progress.current >= 1) {
        decay()
        onPastEnd()
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
      if ((touching && now - touchSeenAt < TOUCH_STALE_MS) || now - touchEndedAt < WHEEL_AFTER_TOUCH_MS) return
      advance(event.deltaY, PAST_END_WHEEL)
    }

    const onTouchStart = (event: TouchEvent) => {
      abandonSeek()
      clearTimeout(idle)
      touching = true
      touchSeenAt = performance.now()
      multiTouch = event.touches.length > 1
      const y = event.touches[0]?.clientY
      pullFrom = y != null && !multiTouch && atBottom() ? y : null
      if (multiTouch && progress.current !== 0) decay()
      setPulling(true)
    }
    const onTouchMove = (event: TouchEvent) => {
      touchSeenAt = performance.now()
      if (event.touches.length > 1) multiTouch = true
      if (multiTouch) {
        pullFrom = null
        if (progress.current !== 0) decay()
        return
      }
      const y = event.touches[0]?.clientY
      if (y == null) return
      if (!atBottom()) {
        pullFrom = null
        setAtLibraryEnd(false)
        if (progress.current !== 0) decay()
        return
      }
      pullFrom ??= y
      const pull = pullFrom - y
      if (pull <= 0) {
        if (progress.current !== 0) decay()
        return
      }
      if (!hasNext) {
        setAtLibraryEnd(true)
        return
      }
      progress.current = Math.min(1, pull / touchDistance())
      setPastEndProgress(progress.current)
    }
    const endTouch = (release: boolean) => {
      const full = release && !multiTouch && hasNext && progress.current >= 1
      touching = false
      multiTouch = false
      touchEndedAt = performance.now()
      pullFrom = null
      setPulling(false)
      if (progress.current !== 0) decay()
      if (full) onPastEnd()
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
  }, [urls, hasNext, onPastEnd])

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
        {urls.map((src, index) => (
          <StripPage
            key={src}
            index={index}
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
        ))}
        <div ref={sentinel} style={{ height: 1 }} />
      </div>
      {(pastEndProgress > 0 || atLibraryEnd) && (
        <div className={`reader-next-chapter-hint${atLibraryEnd ? ' is-end' : ''}`}>
          <span>
            {atLibraryEnd ? (
              <Trans>No more chapters</Trans>
            ) : (
              pastEndLabel ??
              (!pulling ? (
                <Trans>Scroll for next chapter</Trans>
              ) : pastEndProgress >= 1 ? (
                <Trans>Release for next chapter</Trans>
              ) : (
                <Trans>Pull up for next chapter</Trans>
              ))
            )}
          </span>
          <div className="reader-next-chapter-bar">
            {!atLibraryEnd && (
              <div
                className="reader-next-chapter-fill"
                style={{ width: `${Math.min(1, pastEndProgress) * 100}%` }}
              />
            )}
          </div>
        </div>
      )}
    </>
  )
}
