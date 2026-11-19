import { useEffect, useLayoutEffect, useRef } from 'react'
import { wheelPixels } from './wheelTurn'

// A pinch that ends this close to fit snaps back to it.
const ZOOM_SNAP = 1.05
const WHEEL_ZOOM_STEP = 25
const WHEEL_ZOOM_COMMIT_MS = 200
// Fingers lifting off a pinch can still send a click, which must not also hit a tap zone.
const CLICK_AFTER_PINCH_MS = 400

export interface StripZoomOptions {
  /** On in Continuous only; the paged layouts have their own gestures. */
  enabled: boolean
  zoom: number
  zoomMax: number
  onZoom: (zoom: number) => void
}

interface Point {
  x: number
  y: number
}

interface LiveZoom {
  strip: HTMLElement
  zoom: number
  /** The strip point (unscaled) held under the fingers during a pinch. */
  anchor: Point | null
  at: Point
}

const clamp = (value: number, min: number, max: number) => Math.min(max, Math.max(min, value))

/**
 * Pinch zoom for the Continuous strip. The strip keeps the browser's own scrolling, momentum and
 * all, so this only takes two-finger touches, Ctrl+wheel and trackpad pinches. The strip is scaled
 * from its top-left corner (ContinuousView draws `zoom` as a transform), which grows the scroll
 * range on both axes, so panning a zoomed strip is plain native scrolling. While a pinch runs the
 * scale is drawn straight onto the strip; it reaches React state once, at the end.
 */
export function useStripZoom(surface: HTMLElement | null, options: StripZoomOptions) {
  const latest = useRef(options)
  latest.current = options
  const pending = useRef<{ zoom: number; left: number; top: number } | null>(null)
  // Read by the correction below: a zoom out shrinks the strip, and the browser clamps the scroll
  // position to the new range before that correction can read it.
  const position = useRef({ left: 0, top: 0 })
  const { enabled, zoom } = options

  useEffect(() => {
    if (!surface || !enabled) return
    const scroller = surface
    scroller.dataset.pinch = 'true'

    let live: LiveZoom | null = null
    let pinch: { d0: number; from: number } | null = null
    let wheelTimer = 0
    let pinchEndedAt = -Infinity

    const record = () => {
      position.current = { left: scroller.scrollLeft, top: scroller.scrollTop }
    }
    record()

    const local = (x: number, y: number) => {
      const box = scroller.getBoundingClientRect()
      return { x: x - box.left, y: y - box.top }
    }

    const startLive = (): LiveZoom | null => {
      const strip = scroller.querySelector<HTMLElement>(':scope > .reader-continuous')
      if (!strip) return null
      strip.style.willChange = 'transform'
      return { strip, zoom: latest.current.zoom, anchor: null, at: { x: 0, y: 0 } }
    }
    const anchorAt = (zoomed: LiveZoom, at: Point) => ({
      x: (scroller.scrollLeft + at.x - zoomed.strip.offsetLeft) / zoomed.zoom,
      y: (scroller.scrollTop + at.y - zoomed.strip.offsetTop) / zoomed.zoom,
    })
    /** Draws `next` with the strip point `anchor` under the surface point `at`. */
    const drawLive = (zoomed: LiveZoom, next: number, anchor: Point, at: Point) => {
      zoomed.zoom = next
      zoomed.at = at
      zoomed.strip.style.transformOrigin = 'left top'
      zoomed.strip.style.transform = next === 1 ? '' : `scale(${next})`
      scroller.scrollLeft = zoomed.strip.offsetLeft + anchor.x * next - at.x
      scroller.scrollTop = zoomed.strip.offsetTop + anchor.y * next - at.y
      record()
    }
    const commitLive = () => {
      window.clearTimeout(wheelTimer)
      const zoomed = live
      live = null
      if (!zoomed) return
      // Painted at its final scale once the gesture stops, instead of a stretched copy of the start.
      zoomed.strip.style.willChange = ''
      const settled = zoomed.zoom < ZOOM_SNAP ? 1 : Math.round(zoomed.zoom * 100) / 100
      if (settled !== zoomed.zoom) drawLive(zoomed, settled, anchorAt(zoomed, zoomed.at), zoomed.at)
      if (settled === latest.current.zoom) return
      pending.current = { zoom: settled, left: scroller.scrollLeft, top: scroller.scrollTop }
      latest.current.onZoom(settled)
    }

    const metrics = (touches: TouchList) => {
      const a = touches[0]
      const b = touches[1]
      return {
        d: Math.hypot(a.clientX - b.clientX, a.clientY - b.clientY),
        mid: local((a.clientX + b.clientX) / 2, (a.clientY + b.clientY) / 2),
      }
    }

    const onTouchStart = (event: TouchEvent) => {
      if (event.touches.length !== 2 || pinch) return
      live = live ?? startLive()
      if (!live) return
      const { d, mid } = metrics(event.touches)
      pinch = { d0: Math.max(1, d), from: live.zoom }
      live.anchor = anchorAt(live, mid)
      live.at = mid
    }
    // Not passive: the strip's touch-action leaves pinching out so the browser never zooms the
    // whole page, but a two-finger drag would still scroll it under the pinch.
    const onTouchMove = (event: TouchEvent) => {
      if (!pinch || !live?.anchor || event.touches.length < 2) return
      if (event.cancelable) event.preventDefault()
      const { d, mid } = metrics(event.touches)
      const next = clamp((pinch.from * d) / pinch.d0, 1, latest.current.zoomMax)
      drawLive(live, next, live.anchor, mid)
    }
    const onTouchEnd = (event: TouchEvent) => {
      if (!pinch || event.touches.length >= 2) return
      pinch = null
      pinchEndedAt = performance.now()
      commitLive()
    }

    const onClick = (event: MouseEvent) => {
      if (performance.now() - pinchEndedAt > CLICK_AFTER_PINCH_MS) return
      pinchEndedAt = -Infinity
      event.stopPropagation()
      event.preventDefault()
    }

    const onWheel = (event: WheelEvent) => {
      if (!event.ctrlKey) {
        // The tail of a trackpad pinch can arrive without Ctrl; it must not scroll the strip mid-zoom.
        if (live && !pinch) event.preventDefault()
        return
      }
      // A trackpad pinch arrives as Ctrl+wheel.
      event.preventDefault()
      if (pinch) return
      live = live ?? startLive()
      if (!live) return
      const at = local(event.clientX, event.clientY)
      const { dy } = wheelPixels(event, scroller.clientHeight)
      const step = clamp(dy, -WHEEL_ZOOM_STEP, WHEEL_ZOOM_STEP)
      const next = clamp(live.zoom * Math.exp(-step / 100), 1, latest.current.zoomMax)
      drawLive(live, next, anchorAt(live, at), at)
      window.clearTimeout(wheelTimer)
      wheelTimer = window.setTimeout(commitLive, WHEEL_ZOOM_COMMIT_MS)
    }

    // Safari's own pinch zoom ignores touch-action on some versions.
    const onGesture = (event: Event) => event.preventDefault()

    scroller.addEventListener('scroll', record, { passive: true })
    scroller.addEventListener('touchstart', onTouchStart, { passive: true })
    scroller.addEventListener('touchmove', onTouchMove, { passive: false })
    scroller.addEventListener('touchend', onTouchEnd, { passive: true })
    scroller.addEventListener('touchcancel', onTouchEnd, { passive: true })
    scroller.addEventListener('click', onClick, true)
    scroller.addEventListener('wheel', onWheel, { passive: false })
    scroller.addEventListener('gesturestart', onGesture)
    return () => {
      window.clearTimeout(wheelTimer)
      if (live) live.strip.style.willChange = ''
      delete scroller.dataset.pinch
      scroller.removeEventListener('scroll', record)
      scroller.removeEventListener('touchstart', onTouchStart)
      scroller.removeEventListener('touchmove', onTouchMove)
      scroller.removeEventListener('touchend', onTouchEnd)
      scroller.removeEventListener('touchcancel', onTouchEnd)
      scroller.removeEventListener('click', onClick, true)
      scroller.removeEventListener('wheel', onWheel)
      scroller.removeEventListener('gesturestart', onGesture)
    }
  }, [surface, enabled])

  useEffect(() => {
    if (!surface || !enabled) return
    if (zoom > 1) surface.dataset.zoomed = 'true'
    else delete surface.dataset.zoomed
    return () => {
      delete surface.dataset.zoomed
    }
  }, [surface, enabled, zoom])

  // Runs after ContinuousView has drawn the new scale. A pinch already put the strip where it ended;
  // a button or key zooms around the middle of the screen.
  const lastZoom = useRef(zoom)
  useLayoutEffect(() => {
    const previous = lastZoom.current
    lastZoom.current = zoom
    const ended = pending.current
    pending.current = null
    if (!surface || !enabled || previous === zoom) return
    if (ended && ended.zoom === zoom) {
      surface.scrollLeft = ended.left
      surface.scrollTop = ended.top
    } else {
      const ratio = zoom / previous
      const x = surface.clientWidth / 2
      const y = surface.clientHeight / 2
      surface.scrollLeft = zoom === 1 ? 0 : (position.current.left + x) * ratio - x
      surface.scrollTop = (position.current.top + y) * ratio - y
    }
    position.current = { left: surface.scrollLeft, top: surface.scrollTop }
  }, [zoom, surface, enabled])
}
