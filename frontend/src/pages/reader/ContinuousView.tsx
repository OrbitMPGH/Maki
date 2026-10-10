import { memo, useCallback, useEffect, useRef, useState } from 'react'
import { Button, Stack, Text } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import type { ReaderFit } from './prefs'

const FIT_CLASS: Record<ReaderFit, string> = {
  width: 'reader-fit-width',
  height: 'reader-fit-height',
  screen: 'reader-fit-width',
  original: 'reader-fit-original',
}

// Pixels of downward scroll (wheel delta / touch drag) needed at the bottom to trigger the next
// chapter. Sized for a couple of scroll-wheel notches, not a long hold.
const PAST_END_THRESHOLD = 1000
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
  eager: boolean
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
  eager,
  register,
  onSettled,
}: StripPageProps) {
  const { t } = useLingui()
  const image = useRef<HTMLImageElement | null>(null)
  const [failed, setFailed] = useState(false)
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
    setFailed(false)
    setAttempt((n) => n + 1)
  }

  return (
    <>
      <img
        ref={setRef}
        data-page={index}
        src={url}
        alt={t`${label} - page ${pageNumber}`}
        className={`reader-page ${FIT_CLASS[fit]}`}
        style={{
          ...(fit === 'original' && scale !== 100 ? { zoom: scale / 100 } : undefined),
          ...(failed ? { display: 'none' } : undefined),
        }}
        // A window around the current page rather than the whole prefix: resuming at page 300
        // of a webtoon strip would otherwise fetch and decode 300 pages at once. Only the
        // pages close enough to shift the target's offset need forcing. Unloaded pages hold a
        // min-height placeholder (theme.css), which is what gives lazy loading real positions.
        loading={eager ? 'eager' : 'lazy'}
        decoding="async"
        draggable={false}
        onLoad={(event) => onSettled(index, event.currentTarget)}
        onError={(event) => {
          setFailed(true)
          onSettled(index, event.currentTarget)
        }}
      />
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
  gap,
  label,
  pastEndLabel,
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
  gap: number
  label: string
}) {
  const container = useRef<HTMLDivElement>(null)
  const pages = useRef<(HTMLImageElement | null)[]>([])
  const sentinel = useRef<HTMLDivElement>(null)
  // Mirrors `pastEndProgress` state without the render lag, so consecutive wheel/touch events in
  // the same frame accumulate correctly instead of each reading a stale 0.
  const progress = useRef(0)
  const [pastEndProgress, setPastEndProgress] = useState(0)
  // The last chapter's counterpart to `pastEndProgress`: shown under the same conditions, but it
  // only ever says the strip has run out, so nothing accumulates and nothing fires.
  const [atLibraryEnd, setAtLibraryEnd] = useState(false)
  // Read fresh inside the seek effect below without making `page` itself a dependency: the band
  // tracker updates `page` continuously while scrolling, and re-running the seek effect on every
  // one of those would re-scroll to wherever the user just scrolled from.
  const pageRef = useRef(page)
  pageRef.current = page

  // Set by the sentinel effect below; image load handlers re-run it once a page settles.
  const reportEnd = useRef<() => void>(() => {})

  useEffect(() => {
    progress.current = 0
    setPastEndProgress(0)
    setAtLibraryEnd(false)
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
  // the true max (there's no native overscroll to detect), so instead this reads wheel/touch
  // deltas directly and only counts them while already at the bottom, the same way Kavita's
  // reader does it.
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

    const advance = (delta: number) => {
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
      progress.current = Math.min(PAST_END_THRESHOLD, progress.current + delta)
      setPastEndProgress(progress.current / PAST_END_THRESHOLD)
      if (progress.current >= PAST_END_THRESHOLD) {
        decay()
        onPastEnd()
        return
      }
      idle = setTimeout(decay, PAST_END_IDLE_MS)
    }

    // Once the reader has scrolled by hand, the pending seek is history: a later image load must
    // not yank them back to where the resume landed.
    const abandonSeek = () => settling.current.clear()

    const onWheel = (event: WheelEvent) => {
      abandonSeek()
      advance(event.deltaY)
    }

    let touchY: number | null = null
    const onTouchStart = (event: TouchEvent) => {
      abandonSeek()
      touchY = event.touches[0]?.clientY ?? null
    }
    const onTouchMove = (event: TouchEvent) => {
      const y = event.touches[0]?.clientY
      if (y == null || touchY == null) return
      advance(touchY - y)
      touchY = y
    }
    const onTouchEnd = () => {
      touchY = null
    }

    scroller.addEventListener('wheel', onWheel, { passive: true })
    scroller.addEventListener('touchstart', onTouchStart, { passive: true })
    scroller.addEventListener('touchmove', onTouchMove, { passive: true })
    scroller.addEventListener('touchend', onTouchEnd, { passive: true })
    return () => {
      clearTimeout(idle)
      scroller.removeEventListener('wheel', onWheel)
      scroller.removeEventListener('touchstart', onTouchStart)
      scroller.removeEventListener('touchmove', onTouchMove)
      scroller.removeEventListener('touchend', onTouchEnd)
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
        style={{ gap: `${gap}px` }}
      >
        {urls.map((src, index) => (
          <StripPage
            key={src}
            index={index}
            src={src}
            label={label}
            fit={fit}
            scale={scale}
            eager={index < 3 || Math.abs(index - page) <= 2}
            register={registerPage}
            onSettled={onPageSettled}
          />
        ))}
        <div ref={sentinel} style={{ height: 1 }} />
      </div>
      {(pastEndProgress > 0 || atLibraryEnd) && (
        <div className={`reader-next-chapter-hint${atLibraryEnd ? ' is-end' : ''}`}>
          <span>{atLibraryEnd ? <Trans>No more chapters</Trans> : (pastEndLabel ?? <Trans>Scroll for next chapter</Trans>)}</span>
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
