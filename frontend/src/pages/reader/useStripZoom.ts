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
  /** Drawn on top of the scale: where the strip will sit once the scroll moves there at the end. */
  shift: Point
  /** The strip point (unscaled) held under the fingers during a pinch. */
  anchor: Point | null
  at: Point
}

const clamp = (value: number, min: number, max: number) => Math.min(max, Math.max(min, value))

/**
 * Pinch zoom for the Continuous strip. The strip keeps the browser's own scrolling, momentum and
 * all, so this only takes two-finger touches, Ctrl+wheel and trackpad pinches. The strip is scaled
 * from its top-left corner (ContinuousView draws `zoom` as a transform), which grows the scroll
 * range on both axes, so panning a zoomed strip is plain native scrolling.
 *
 * While a gesture runs, the scroll position is never written: Safari scrolls off the main thread
 * and eases back to its own position after a gesture that fought it, and it ignores a sideways
 * scroll while the unzoomed strip has none. The strip is drawn with a translate instead, to where
 * the scroll will put it, and the zoom and the scroll land together once the fingers lift.
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
    // A pinch is live until its last finger lifts; one finger left behind pans the strip natively.
    let touchLive = false
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
      return { strip, zoom: latest.current.zoom, shift: { x: 0, y: 0 }, anchor: null, at: { x: 0, y: 0 } }
    }
    /** The scroll position that shows the live drawing without its shift. */
    const target = (zoomed: LiveZoom) => ({
      left: scroller.scrollLeft - zoomed.shift.x,
      top: scroller.scrollTop - zoomed.shift.y,
    })
    const anchorAt = (zoomed: LiveZoom, at: Point) => {
      const { left, top } = target(zoomed)
      return {
        x: (left + at.x - zoomed.strip.offsetLeft) / zoomed.zoom,
        y: (top + at.y - zoomed.strip.offsetTop) / zoomed.zoom,
      }
    }
    /** Draws `next` with the strip point `anchor` under the surface point `at`, as far as the strip's edges allow. */
    const drawLive = (zoomed: LiveZoom, next: number, anchor: Point, at: Point) => {
      const { strip } = zoomed
      zoomed.zoom = next
      zoomed.at = at
      const width = Math.max(strip.offsetWidth, strip.scrollWidth)
      const height = Math.max(strip.offsetHeight, strip.scrollHeight)
      const left = clamp(strip.offsetLeft + anchor.x * next - at.x, 0, Math.max(0, strip.offsetLeft + width * next - scroller.clientWidth))
      const top = clamp(strip.offsetTop + anchor.y * next - at.y, 0, Math.max(0, strip.offsetTop + height * next - scroller.clientHeight))
      zoomed.shift = { x: scroller.scrollLeft - left, y: scroller.scrollTop - top }
      strip.style.transformOrigin = 'left top'
      strip.style.transform = `translate(${zoomed.shift.x}px, ${zoomed.shift.y}px) scale(${next})`
    }
    const commitLive = () => {
      window.clearTimeout(wheelTimer)
      const zoomed = live
      live = null
      touchLive = false
      if (!zoomed) return
      // Painted at its final scale once the gesture stops, instead of a stretched copy of the start.
      zoomed.strip.style.willChange = ''
      const settled = zoomed.zoom < ZOOM_SNAP ? 1 : Math.round(zoomed.zoom * 100) / 100
      drawLive(zoomed, settled, anchorAt(zoomed, zoomed.at), zoomed.at)
      const end = { zoom: settled, ...target(zoomed) }
      if (settled !== latest.current.zoom) {
        pending.current = end
        latest.current.onZoom(settled)
        return
      }
      zoomed.strip.style.transform = settled === 1 ? '' : `scale(${settled})`
      scroller.scrollLeft = end.left
      scroller.scrollTop = end.top
      record()
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
      if (event.touches.length < 2 || pinch) return
      if (live && !touchLive) commitLive()
      live = live ?? startLive()
      if (!live) return
      touchLive = true
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
      if (!touchLive) return
      if (pinch && event.touches.length < 2) {
        pinch = null
        pinchEndedAt = performance.now()
      }
      if (event.touches.length === 0 || event.type === 'touchcancel') commitLive()
    }

    // A browser that scrolled under the pinch anyway (a finger that moved before the second one
    // landed) is followed, so the fingers keep their spot. After the pinch, a finger left behind
    // pans the drawing along with the scroll, which is what it would do once committed.
    const onScroll = () => {
      record()
      if (live && pinch && live.anchor) drawLive(live, live.zoom, live.anchor, live.at)
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
        if (live && !touchLive) event.preventDefault()
        return
      }
      // A trackpad pinch arrives as Ctrl+wheel.
      event.preventDefault()
      if (touchLive) return
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

    scroller.addEventListener('scroll', onScroll, { passive: true })
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
      scroller.removeEventListener('scroll', onScroll)
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

  // Runs after ContinuousView has drawn the new scale, and after the surface has its sideways
  // scrolling back. A pinch moves the scroll to where it drew the strip; a button or key zooms
  // around the middle of the screen.
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
