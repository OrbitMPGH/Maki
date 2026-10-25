import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { Button, Stack, Text } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { cropRegion, FULL_REGION, isFull, partOf, type PagePart, type Region } from './pageShape'
import EnhancedPage from './EnhancedPage'
import { useCompareKey } from './compareHold'
import {
  EnhanceScope,
  enhanceKey,
  presetFor,
  regionBitmap,
  useEnhanceEngine,
  type PageEnhance,
} from './enhanceRuntime'
import { useWarmServerPages, type ServerEnhance } from './useServerEnhance'
import type {
  EnhanceMethod,
  ReaderDirection,
  ReaderEnhance,
  ReaderFit,
  ReaderSplitWide,
} from './prefs'
import type { Spread } from './useSpreads'

const FIT_CLASS: Record<ReaderFit, string> = {
  width: 'reader-fit-width',
  height: 'reader-fit-height',
  screen: 'reader-fit-screen',
  original: 'reader-fit-original',
}

type PageState = 'loading' | 'ready' | 'failed'

/** How a page is shown beyond its fit: trimmed, one half of a spread, or turned a quarter. */
export interface PageShaping {
  crop: boolean
  part: PagePart
  rotate: 0 | 90 | -90
}

const PLAIN: PageShaping = { crop: false, part: 'whole', rotate: 0 }

/**
 * A region of a page drawn at the size the fit would give that region alone. The blank canvas
 * carries the region's pixel size and takes the fit classes, so it sizes exactly like an `<img>`
 * of the cropped page would (1:1 included); the real image sits inside, scaled and offset so only
 * the region shows. A canvas rather than a sized image: it has its size the moment it mounts, so a
 * page turn never flashes an empty box, and with no context drawn it holds no pixel buffer.
 * An enhanced page takes this path even when it is whole, and draws the engine's output over the frame.
 */
function RegionPage({
  src,
  url,
  alt,
  className,
  style,
  tone,
  fit,
  image,
  region,
  rotate,
  enhance,
  page,
  server,
}: {
  src: string
  url: string
  alt: string
  className: string
  style: React.CSSProperties | undefined
  tone: string | undefined
  fit: ReaderFit
  image: HTMLImageElement
  region: Region
  rotate: 0 | 90 | -90
  enhance: PageEnhance | null
  page: number
  server: ServerEnhance | null
}) {
  const [box, setBox] = useState<HTMLDivElement | null>(null)
  const [covered, setCovered] = useState(false)
  const width = Math.max(1, Math.round(image.naturalWidth * region.w))
  const height = Math.max(1, Math.round(image.naturalHeight * region.h))
  const turned = rotate !== 0
  const frame: React.CSSProperties = turned
    ? {
        left: '50%',
        top: '50%',
        width: `${(100 * width) / height}%`,
        height: `${(100 * height) / width}%`,
        transform: `translate(-50%, -50%) rotate(${rotate}deg)`,
      }
    : { inset: 0 }
  return (
    <div ref={setBox} className="reader-region" data-fit={fit}>
      <canvas
        className={className}
        width={turned ? height : width}
        height={turned ? width : height}
        style={style}
        aria-hidden
      />
      <div className="reader-region-frame" style={frame}>
        <img
          src={url}
          alt={alt}
          className="reader-region-image"
          data-no-cover-fallback
          style={{
            left: `${(-region.x / region.w) * 100}%`,
            top: `${(-region.y / region.h) * 100}%`,
            width: `${100 / region.w}%`,
            height: `${100 / region.h}%`,
            filter: tone,
            visibility: enhance && covered ? 'hidden' : undefined,
          }}
          decoding="sync"
          draggable={false}
        />
        {enhance && (
          <EnhancedPage
            image={image}
            src={src}
            region={region}
            rotate={rotate}
            alt={alt}
            tone={tone}
            enhance={enhance}
            page={page}
            server={server}
            priority={0}
            box={box}
            onShowing={setCovered}
          />
        )}
      </div>
    </div>
  )
}

/**
 * One page with its own loading and failure state: the page's outline while it loads, and a retry
 * where a failed one would otherwise show the browser's broken-image icon. The image stays mounted
 * (hidden until it is ready) so a retry just re-requests it with a cache-busting suffix.
 */
function PagedPage({
  src,
  alt,
  className,
  style,
  tone,
  fit,
  shaping,
  enhance,
  page,
  server,
  onLoaded,
}: {
  src: string
  alt: string
  className: string
  style: React.CSSProperties | undefined
  tone: string | undefined
  fit: ReaderFit
  shaping: PageShaping
  enhance: PageEnhance | null
  page: number
  server: ServerEnhance | null
  onLoaded: (image: HTMLImageElement) => void
}) {
  const [state, setState] = useState<PageState>('loading')
  const [attempt, setAttempt] = useState(0)
  const [image, setImage] = useState<HTMLImageElement | null>(null)
  const url = attempt === 0 ? src : `${src}${src.includes('?') ? '&' : '?'}retry=${attempt}`

  const loaded = useRef(onLoaded)
  loaded.current = onLoaded
  const markReady = useCallback((element: HTMLImageElement) => {
    setState('ready')
    setImage(element)
    loaded.current(element)
  }, [])
  // A page already in the browser cache is complete before React attaches its load listener.
  const setRef = useCallback(
    (element: HTMLImageElement | null) => {
      if (element?.complete && element.naturalWidth > 0) markReady(element)
    },
    [markReady],
  )

  const ready = state === 'ready' && image !== null
  const natural = ready ? { width: image.naturalWidth, height: image.naturalHeight } : null
  const wide = natural !== null && natural.width > natural.height
  const crop = useMemo(() => (ready && shaping.crop ? cropRegion(image) : null), [ready, image, shaping.crop])
  const region = partOf(crop, wide ? shaping.part : 'whole')
  const rotate = wide ? shaping.rotate : 0
  const shaped = natural !== null && (enhance !== null || rotate !== 0 || !isFull(region))

  return (
    <>
      {state === 'loading' && <div className="reader-page-skeleton" aria-hidden />}
      {state === 'failed' && (
        <Stack align="center" gap="xs" onClick={(event) => event.stopPropagation()}>
          <Text c="var(--ink-3)" size="sm">
            <Trans>This page failed to load.</Trans>
          </Text>
          <Button
            variant="light"
            size="xs"
            onClick={() => {
              setState('loading')
              setImage(null)
              setAttempt((n) => n + 1)
            }}
          >
            <Trans>Try again</Trans>
          </Button>
        </Stack>
      )}
      <img
        ref={setRef}
        src={url}
        data-no-cover-fallback
        alt={alt}
        className={className}
        style={{ ...style, filter: tone, ...(state === 'ready' && !shaped ? undefined : { display: 'none' }) }}
        decoding="async"
        draggable={false}
        onLoad={(event) => markReady(event.currentTarget)}
        onError={() => setState('failed')}
      />
      {shaped && image && (
        <RegionPage
          src={src}
          url={url}
          alt={alt}
          className={className}
          style={style}
          tone={tone}
          fit={fit}
          image={image}
          region={region}
          rotate={rotate}
          enhance={enhance}
          page={page}
          server={server}
        />
      )}
    </>
  )
}

/**
 * One spread at a time: a single page, or two side by side in double mode.
 * Page turns are instant because neighbours are already preloaded.
 */
export default function PagedView({
  urls,
  spread,
  fit,
  direction,
  zoom,
  scale,
  tone,
  label,
  onMeasure,
  cropBorders = false,
  splitWide = 'off',
  half = 0,
  enhance = 'off',
  enhanceMethod = 'a4k-m',
  server = null,
  preload,
}: {
  urls: string[]
  spread: Spread
  fit: ReaderFit
  direction: ReaderDirection
  zoom: number
  /** Percent scale on top of the '1:1' fit; ignored for the other fits. */
  scale: number
  /** CSS filter for the page images (dim and colour), from `toneFilter`. */
  tone?: string
  label: string
  onMeasure: (index: number, image: HTMLImageElement) => void
  cropBorders?: boolean
  /** What a wide page does when it is shown alone; the parent passes 'off' outside single-page mode. */
  splitWide?: ReaderSplitWide
  /** Which half of a split wide page is up, in reading order. */
  half?: 0 | 1
  enhance?: ReaderEnhance
  enhanceMethod?: EnhanceMethod
  /** Set while a server method is in use and the server tier can serve it. */
  server?: ServerEnhance | null
  /** The reader's preload window: the spread on screen and the pages after it, in reading order. */
  preload?: number[]
}) {
  const { t } = useLingui()
  // In right-to-left reading the lower page number belongs on the right.
  const ordered = direction === 'rtl' ? [...spread].reverse() : spread
  const root = useRef<HTMLDivElement>(null)

  // The scroller outlives the page, so a page read to its bottom would otherwise hand the next one
  // the same offset. A page wider than the viewport (zoomed, or 1:1) starts at the edge reading
  // begins from: the right in right-to-left. Its width is not known until the image has loaded, so
  // the right edge is held until the reader touches the scroller or the zoom changes.
  const rtl = direction === 'rtl'
  // The half read first is the right one in right-to-left. A quarter turn puts it at the top:
  // anticlockwise lifts the right edge, clockwise the left.
  const single = spread.length === 1
  const shaping: PageShaping =
    !cropBorders && (!single || splitWide === 'off')
      ? PLAIN
      : {
          crop: cropBorders,
          part: single && splitWide === 'split' ? ((half === 0) === rtl ? 'right' : 'left') : 'whole',
          rotate: single && splitWide === 'rotate' ? (rtl ? -90 : 90) : 0,
        }
  const enhanceOn = enhance !== 'off'
  const engineState = useEnhanceEngine(enhanceOn)
  const engine = engineState.status === 'ready' ? engineState.engine : null
  useCompareKey(enhanceOn)
  // Bumped when a page on screen reports the size it is shown at, which is what the pages ahead are
  // enhanced at.
  const [sized, setSized] = useState(0)
  const [scope] = useState(() => new EnhanceScope(() => setSized((n) => n + 1)))
  useEffect(() => {
    scope.attach(engine)
    return () => scope.release()
  }, [scope, engine, urls])
  const layout = `${fit}|${zoom}|${scale}`
  const serverOn = server !== null
  const pageEnhance = useMemo<PageEnhance | null>(
    () =>
      (engine || serverOn) && enhance !== 'off'
        ? { mode: enhance, preset: presetFor(enhanceMethod), engine, scope, layout }
        : null,
    [engine, serverOn, enhance, enhanceMethod, scope, layout],
  )

  // Pages that leave the window around the spread are evicted from the engine's cache. The two
  // before it stay, so turning back is instant.
  const lead = Math.min(...spread)
  useEffect(() => {
    const keep = new Set<string>()
    for (const page of [...spread, ...(preload ?? []), lead - 1, lead - 2]) {
      if (urls[page]) keep.add(urls[page])
    }
    scope.retain(keep)
  }, [scope, urls, spread, preload, lead])

  // The pages ahead are enhanced at the size a page of the same pixel size is shown at now, so a
  // turn finds them in the cache. Pages of another size wait until they are on screen.
  const { crop: shapeCrop, part: shapePart, rotate: shapeRotate } = shaping
  useEffect(() => {
    if (!pageEnhance?.engine || !preload?.length) return
    const { engine: live, scope: owner, preset, mode } = pageEnhance
    const controller = new AbortController()
    const { signal } = controller
    const shown = new Set(spread)
    const images: HTMLImageElement[] = []
    preload
      .filter((page) => !shown.has(page))
      .forEach((page, order) => {
        const src = urls[page]
        if (!src) return
        const image = new Image()
        image.src = src
        images.push(image)
        void image
          .decode()
          .then(async () => {
            if (signal.aborted) return
            if (image.naturalWidth > image.naturalHeight && (shapePart !== 'whole' || shapeRotate !== 0)) return
            const region = (shapeCrop ? cropRegion(image) : null) ?? FULL_REGION
            const target = owner.sizeFor(image.naturalWidth * region.w, image.naturalHeight * region.h)
            if (!target || (mode === 'auto' && !target.stretched)) return
            const key = enhanceKey(src, region)
            if (live.peek(key, preset, target.width, target.height)) return
            owner.note(src, key)
            const bitmap = await regionBitmap(image, region)
            if (signal.aborted) return bitmap.close()
            await live.enhance(bitmap, {
              preset,
              targetWidth: target.width,
              targetHeight: target.height,
              key,
              priority: order + 1,
              signal,
            })
          })
          .catch(() => {})
      })
    return () => {
      controller.abort()
      for (const image of images) image.src = ''
    }
  }, [pageEnhance, preload, spread, urls, shapeCrop, shapePart, shapeRotate, sized])

  const ahead = useMemo(() => (preload ?? []).filter((page) => !spread.includes(page)), [preload, spread])
  useWarmServerPages(pageEnhance ? server : null, ahead)

  const rtlRef = useRef(rtl)
  rtlRef.current = rtl
  const pinRight = useRef(false)
  const toStart = useCallback(() => {
    const scroller = root.current?.parentElement
    if (scroller) scroller.scrollLeft = rtlRef.current ? scroller.scrollWidth : 0
  }, [])
  const leadSrc = urls[spread[0]]
  useLayoutEffect(() => {
    const scroller = root.current?.parentElement
    if (scroller) scroller.scrollTop = 0
    pinRight.current = rtlRef.current
    toStart()
  }, [leadSrc, half, toStart])

  useEffect(() => {
    const scroller = root.current?.parentElement
    const content = root.current
    if (!scroller || !content) return
    const release = () => {
      pinRight.current = false
    }
    const observer = new ResizeObserver(() => {
      if (pinRight.current) toStart()
    })
    observer.observe(content)
    const events = ['wheel', 'touchstart', 'pointerdown']
    for (const name of events) scroller.addEventListener(name, release, { passive: true })
    return () => {
      observer.disconnect()
      for (const name of events) scroller.removeEventListener(name, release)
    }
  }, [toStart])

  // Scrolling can only reach the right and bottom of a transformed box, so the zoom grows from the
  // top-left corner and the scroll position is moved to keep the viewport centre where it was.
  const lastZoom = useRef(zoom)
  useLayoutEffect(() => {
    const scroller = root.current?.parentElement
    const previous = lastZoom.current
    lastZoom.current = zoom
    if (!scroller || previous === zoom) return
    pinRight.current = false
    const centre = scroller.clientWidth / 2
    scroller.scrollLeft = (scroller.scrollLeft + centre) * (zoom / previous) - centre
  }, [zoom])

  return (
    <div
      ref={root}
      className="reader-paged"
      data-double={spread.length > 1}
      style={zoom === 1 ? undefined : { transform: `scale(${zoom})`, transformOrigin: 'left top' }}
    >
      {ordered.map((page) => {
        const src = urls[page]
        if (!src) return null
        const pageNumber = page + 1
        return (
          <PagedPage
            key={src}
            src={src}
            alt={t`${label} - page ${pageNumber}`}
            className={`reader-page ${FIT_CLASS[fit]}`}
            style={fit === 'original' && scale !== 100 ? { zoom: scale / 100 } : undefined}
            tone={tone}
            fit={fit}
            shaping={shaping}
            enhance={pageEnhance}
            page={page}
            server={server}
            onLoaded={(image) => onMeasure(page, image)}
          />
        )
      })}
    </div>
  )
}
