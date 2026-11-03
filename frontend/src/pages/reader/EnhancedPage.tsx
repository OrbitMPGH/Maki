import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react'
import { serverEnhancedUrl, serverEnhancedUrlNow } from '../../api/reader'
import { isSoftPage, knownSoft } from '../../lib/enhance/softness'
import { useCompareHeld } from './compareHold'
import {
  enhanceKey,
  isEnhanceError,
  markEnhanceBroken,
  measureTarget,
  regionBitmap,
  type EnhanceTarget,
  type PageEnhance,
} from './enhanceRuntime'
import type { Region } from './pageShape'
import type { ServerEnhance } from './useServerEnhance'

const SETTLE_MS = 300

export interface EnhancedPageProps {
  /** The decoded original page. It is the engine's source and stays mounted for crop and compare. */
  image: HTMLImageElement
  /** The page URL without a retry suffix; with the region it keys the engine's cache. */
  src: string
  region: Region
  rotate: 0 | 90 | -90
  alt: string
  tone?: string
  enhance: PageEnhance
  /** The page's index in the chapter, which names its server upscale. */
  page: number
  /** Set while a server method is in use; until this page is ready there, the engine's fallback result shows. */
  server?: ServerEnhance | null
  /** 0 for a page on screen, then its distance in reading order. */
  priority: number
  /** The layout box the region is displayed in, measured for the auto gate and the target size. */
  box: HTMLElement | null
  /** Draw positioned over `box` (the continuous strip) instead of filling the parent (RegionPage's frame). */
  overlay?: boolean
  /** True while the canvas is showing, so the host can hide its image. */
  onShowing: (showing: boolean) => void
}

const sameTarget = (a: EnhanceTarget | null, b: EnhanceTarget) =>
  a !== null && a.width === b.width && a.height === b.height && a.stretched === b.stretched

/**
 * A page region run through the enhance engine and drawn into a canvas. Until the result is in,
 * while compare is held, or when `auto` finds the page sharp and shown at about its own size, the canvas is
 * hidden and the host's image shows instead. With a server method, the server's upscale replaces
 * the canvas once it is ready and decoded.
 */
export default function EnhancedPage({
  image,
  src,
  region,
  rotate,
  alt,
  tone,
  enhance,
  page,
  server = null,
  priority,
  box,
  overlay = false,
  onShowing,
}: EnhancedPageProps) {
  const canvas = useRef<HTMLCanvasElement>(null)
  const held = useCompareHeld()
  const [target, setTarget] = useState<EnhanceTarget | null>(null)
  const [drawn, setDrawn] = useState<string | null>(null)
  const { mode, preset, engine, scope, layout } = enhance
  const key = enhanceKey(src, region)
  const identity = `${key}|${preset}`
  const regionId = `${region.x},${region.y},${region.w},${region.h}`
  // A page measured ahead answers at once; one that was not is measured after it paints.
  const known = mode === 'auto' ? knownSoft(src) : false
  const [, setMeasured] = useState<string | null>(null)
  useEffect(() => {
    if (known !== undefined) return
    const timer = setTimeout(() => {
      isSoftPage(image, src)
      setMeasured(src)
    })
    return () => clearTimeout(timer)
  }, [known, image, src])
  const soft = known === true
  const softRef = useRef(soft)
  softRef.current = soft

  const measure = useRef(() => {})
  measure.current = () => {
    if (!box) return
    const next = measureTarget(box, rotate, { width: image.naturalWidth, height: image.naturalHeight }, region)
    if (!next) return
    scope.noteSize(image.naturalWidth * region.w, image.naturalHeight * region.h, next)
    scope.notePage(page, next.stretched || softRef.current)
    setTarget((previous) => (sameTarget(previous, next) ? previous : next))
  }

  // Measured before the first paint, so a page turn can swap in a ready upscale without showing
  // the plain page first, then again only once a resize has settled, so turning a phone does not
  // flip pages between enhanced and plain on the way round.
  useLayoutEffect(() => {
    if (!box) return
    measure.current()
    let first = true
    let timer: ReturnType<typeof setTimeout> | undefined
    const observer = new ResizeObserver(() => {
      if (first) {
        first = false
        measure.current()
        return
      }
      clearTimeout(timer)
      timer = setTimeout(() => measure.current(), SETTLE_MS)
    })
    observer.observe(box)
    return () => {
      observer.disconnect()
      clearTimeout(timer)
    }
  }, [box, rotate, regionId, image])

  useEffect(() => {
    if (soft) measure.current()
  }, [soft])

  const lastLayout = useRef(layout)
  useEffect(() => {
    if (lastLayout.current === layout) return
    lastLayout.current = layout
    const timer = setTimeout(() => measure.current(), SETTLE_MS)
    return () => clearTimeout(timer)
  }, [layout])

  const [serverImage, setServerImage] = useState<HTMLImageElement | null>(null)

  // In the strip the canvas sits over the page image, which moves whenever a page above it loads.
  useEffect(() => {
    const elements = [canvas.current, serverImage].filter((element) => element !== null)
    const parent = box?.parentElement
    if (!overlay || !box || !parent || elements.length === 0) return
    const place = () => {
      const at = box.getBoundingClientRect()
      const origin = parent.getBoundingClientRect()
      for (const element of elements) {
        element.style.left = `${at.left - origin.left - parent.clientLeft}px`
        element.style.top = `${at.top - origin.top - parent.clientTop}px`
        element.style.width = `${at.width}px`
        element.style.height = `${at.height}px`
      }
    }
    place()
    const observer = new ResizeObserver(place)
    observer.observe(box)
    observer.observe(parent)
    return () => observer.disconnect()
  }, [overlay, box, serverImage])

  const active = target !== null && (mode === 'always' || target.stretched || soft)
  const priorityRef = useRef(priority)
  priorityRef.current = priority
  // `key` carries the region's values; the object itself may be rebuilt on every render.
  const regionRef = useRef(region)
  regionRef.current = region

  // A page `auto` no longer wants lets go of its pixels, and is drawn again if it comes back.
  useEffect(() => {
    if (active) return
    const element = canvas.current
    if (element) {
      element.width = 0
      element.height = 0
    }
    setDrawn(null)
  }, [active])

  useEffect(() => {
    if (!active || !target || !engine || !preset) return
    const draw = (bitmap: ImageBitmap) => {
      const element = canvas.current
      if (!element) return
      element.width = bitmap.width
      element.height = bitmap.height
      element.getContext('2d')?.drawImage(bitmap, 0, 0)
      setDrawn(identity)
    }
    scope.note(src, key)
    const hit = engine.peek(key, preset, target.width, target.height)
    if (hit) {
      draw(hit)
      return
    }
    const controller = new AbortController()
    const { signal } = controller
    void regionBitmap(image, regionRef.current)
      .then((bitmap) => {
        if (signal.aborted) {
          bitmap.close()
          return null
        }
        return engine.enhance(bitmap, {
          preset,
          targetWidth: target.width,
          targetHeight: target.height,
          key,
          priority: priorityRef.current,
          signal,
        })
      })
      .then((result) => {
        if (result && !signal.aborted) draw(result)
      })
      .catch((error: unknown) => {
        if (isEnhanceError(error, 'broken') || isEnhanceError(error, 'unsupported')) markEnhanceBroken()
      })
    return () => controller.abort()
  }, [active, target, engine, scope, image, identity, key, preset, src])

  useEffect(() => {
    engine?.setPriority(key, priority)
  }, [engine, key, priority])

  // Drop the canvas's pixels with it, rather than waiting for the element to be collected.
  useEffect(() => {
    const element = canvas.current
    return () => {
      if (!element) return
      element.width = 0
      element.height = 0
    }
  }, [])

  const serverChapter = server?.chapterId
  const serverModel = server?.model
  const serverVersion = server?.pageVersion
  const serverReady = server?.states.get(page) === 'ready'
  const serverKey = serverReady ? `${serverChapter}|${page}|${serverModel}|${serverVersion}` : null
  const urlNow =
    serverKey && serverChapter !== undefined && serverModel && serverVersion !== undefined
      ? serverEnhancedUrlNow(serverChapter, page, serverModel, serverVersion)
      : null
  const [resolved, setResolved] = useState<{ key: string; url: string } | null>(null)
  useEffect(() => {
    if (urlNow || !serverKey || serverChapter === undefined || !serverModel || serverVersion === undefined) return
    let live = true
    void serverEnhancedUrl(serverChapter, page, serverModel, serverVersion).then((url) => {
      if (live) setResolved({ key: serverKey, url })
    })
    return () => {
      live = false
    }
  }, [urlNow, serverKey, serverChapter, page, serverModel, serverVersion])
  const serverUrl = urlNow ?? (resolved && resolved.key === serverKey ? resolved.url : null)
  const [decoded, setDecoded] = useState<string | null>(null)
  const [failed, setFailed] = useState<string | null>(null)
  const serverWanted = active && serverUrl !== null && failed !== serverUrl

  // A server image that leaves the window lets go of its pixels with it.
  useEffect(() => {
    if (!serverImage) return
    return () => serverImage.removeAttribute('src')
  }, [serverImage])

  // A page warmed ahead (useWarmServerPages) is complete the moment its element mounts. Taken then,
  // before paint, it replaces the plain page in the same frame instead of after a load and a decode.
  const attachServerImage = useCallback(
    (element: HTMLImageElement | null) => {
      setServerImage(element)
      if (element?.complete && element.naturalWidth > 0 && serverUrl) setDecoded(serverUrl)
    },
    [serverUrl],
  )

  const serverDecoded = serverWanted && decoded === serverUrl
  const reportServer = server?.reportShown
  useEffect(() => {
    if (!serverDecoded || !reportServer) return
    reportServer(page, true)
    return () => reportServer(page, false)
  }, [serverDecoded, reportServer, page])

  const serverShowing = serverDecoded && !held
  const showing = active && drawn === identity && !held && !serverShowing
  const covering = showing || serverShowing
  const reportShowing = useRef(onShowing)
  reportShowing.current = onShowing
  // Before paint: the canvas hides in this commit, and a passive effect would leave one frame with
  // neither it nor the host's image showing.
  useLayoutEffect(() => {
    reportShowing.current(covering)
  }, [covering])
  useEffect(() => () => reportShowing.current(false), [])

  return (
    <>
      <canvas
        ref={canvas}
        className={overlay ? 'reader-enhanced reader-enhanced-overlay' : 'reader-enhanced'}
        role="img"
        aria-label={alt}
        data-enhanced={showing}
        style={{ filter: tone, visibility: showing ? undefined : 'hidden' }}
      />
      {serverWanted && (
        <img
          ref={attachServerImage}
          src={serverUrl}
          alt={alt}
          className={
            overlay ? 'reader-enhanced reader-enhanced-overlay reader-enhanced-server' : 'reader-region-image reader-enhanced-server'
          }
          data-enhanced={serverShowing}
          data-no-cover-fallback
          // Hidden until decoded, so this only matters on the warmed path: no blank frame while it paints.
          decoding="sync"
          draggable={false}
          style={{
            ...(overlay
              ? undefined
              : {
                  left: `${(-region.x / region.w) * 100}%`,
                  top: `${(-region.y / region.h) * 100}%`,
                  width: `${100 / region.w}%`,
                  height: `${100 / region.h}%`,
                }),
            filter: tone,
            visibility: serverShowing ? undefined : 'hidden',
          }}
          onLoad={(event) => {
            const url = serverUrl
            event.currentTarget.decode().then(
              () => setDecoded(url),
              () => setFailed(url),
            )
          }}
          onError={() => setFailed(serverUrl)}
        />
      )}
    </>
  )
}
