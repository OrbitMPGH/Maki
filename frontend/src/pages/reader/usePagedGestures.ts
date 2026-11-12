import { useEffect, useLayoutEffect, useRef } from 'react'
import { WheelTurn, wheelPixels } from './wheelTurn'

// Movement under this is still a tap.
const SLOP_PX = 10
// A swipe turns the page once it has travelled this share of the width (within the bounds), or
// when it is flung faster than FLING_PX_MS over at least FLING_MIN_PX.
const SWIPE_SHARE = 0.12
const SWIPE_MIN_PX = 48
const SWIPE_MAX_PX = 96
const FLING_PX_MS = 0.4
const FLING_MIN_PX = 24
// Steeper than this (vertical over horizontal) is not a page swipe.
const SWIPE_SLOPE = 0.7
const VELOCITY_WINDOW_MS = 100
// A pinch that ends this close to fit snaps back to it.
const ZOOM_SNAP = 1.05
const WHEEL_ZOOM_STEP = 25
const WHEEL_ZOOM_COMMIT_MS = 200
const MOMENTUM_MIN_PX_MS = 0.3
const MOMENTUM_STOP_PX_MS = 0.02
const MOMENTUM_DECAY = 0.95

export interface PagedGestureOptions {
  /** Off in Continuous, which keeps the browser's own scrolling. */
  enabled: boolean
  zoom: number
  zoomMax: number
  rtl: boolean
  onZoom: (zoom: number) => void
  onTurn: (forward: boolean) => void
}

interface Sample {
  x: number
  y: number
  t: number
}

interface Drag {
  id: number
  type: string
  kind: 'pending' | 'pan' | 'swipe' | 'idle'
  x0: number
  y0: number
  left0: number
  top0: number
  samples: Sample[]
}

interface LiveZoom {
  content: HTMLElement
  from: number
  zoom: number
  /** The content point (unscaled) held under the fingers during a pinch. */
  anchor: { x: number; y: number } | null
  at: { x: number; y: number }
}

function velocity(samples: Sample[]): { x: number; y: number } {
  const last = samples[samples.length - 1]
  const first = samples.find((s) => last.t - s.t <= VELOCITY_WINDOW_MS) ?? last
  const dt = last.t - first.t
  return dt > 0 ? { x: (last.x - first.x) / dt, y: (last.y - first.y) / dt } : { x: 0, y: 0 }
}

/**
 * Touch, mouse and wheel input for Single and Double, on the reader surface (the paged view's
 * scroller). One place decides what a pointer is doing, so a gesture is only ever one thing:
 * - two fingers pinch the reader's own zoom around their midpoint (the browser's page zoom is off
 *   here, so the toolbar keeps its size); Ctrl+wheel and trackpad pinches do the same;
 * - one finger, or the mouse, pans a page that overflows, and always pans while zoomed;
 * - one finger swiped sideways past a threshold turns the page once it lifts (the page does not
 *   follow the finger);
 * - the wheel scrolls the page and turns it from the edge, at most once per gesture;
 * - anything that moved past a tap swallows the click that follows, so it never also hits a tap zone.
 * While a pinch runs the zoom is drawn straight onto the page; it reaches React state once, at the end.
 */
export function usePagedGestures(surface: HTMLElement | null, options: PagedGestureOptions) {
  const latest = useRef(options)
  latest.current = options
  const pending = useRef<{ zoom: number; left: number; top: number } | null>(null)
  const { enabled, zoom } = options

  useEffect(() => {
    if (!surface || !enabled) return
    const scroller = surface
    scroller.dataset.gestures = 'paged'

    const pointers = new Map<number, { x: number; y: number }>()
    let drag: Drag | null = null
    let pinch: { d0: number } | null = null
    let live: LiveZoom | null = null
    let swallowClick = false
    let momentum = 0
    let wheelZoomTimer = 0
    const wheel = new WheelTurn()

    const local = (x: number, y: number) => {
      const box = scroller.getBoundingClientRect()
      return { x: x - box.left, y: y - box.top }
    }
    const overflow = () => ({
      x: scroller.scrollWidth - scroller.clientWidth > 1,
      y: scroller.scrollHeight - scroller.clientHeight > 1,
    })
    const stopMomentum = () => {
      if (momentum) cancelAnimationFrame(momentum)
      momentum = 0
    }

    const startLive = (): LiveZoom | null => {
      const content = scroller.querySelector<HTMLElement>(':scope > .reader-paged')
      if (!content) return null
      const from = latest.current.zoom
      return { content, from, zoom: from, anchor: null, at: { x: 0, y: 0 } }
    }
    /** Draws `next` with the content point `anchor` under the surface point `at`. */
    const drawLive = (zoomed: LiveZoom, next: number, anchor: { x: number; y: number }, at: { x: number; y: number }) => {
      zoomed.zoom = next
      zoomed.at = at
      zoomed.content.style.transformOrigin = 'left top'
      zoomed.content.style.transform = next === 1 ? '' : `scale(${next})`
      scroller.scrollLeft = anchor.x * next - at.x
      scroller.scrollTop = anchor.y * next - at.y
    }
    const anchorAt = (zoomed: LiveZoom, at: { x: number; y: number }) => ({
      x: (scroller.scrollLeft + at.x) / zoomed.zoom,
      y: (scroller.scrollTop + at.y) / zoomed.zoom,
    })
    const commitLive = () => {
      window.clearTimeout(wheelZoomTimer)
      const zoomed = live
      live = null
      if (!zoomed) return
      const settled = zoomed.zoom < ZOOM_SNAP ? 1 : Math.round(zoomed.zoom * 100) / 100
      if (settled !== zoomed.zoom) drawLive(zoomed, settled, anchorAt(zoomed, zoomed.at), zoomed.at)
      if (settled === latest.current.zoom) return
      // The paged view corrects the scroll for a zoom change it did not see; this is put back after it.
      pending.current = { zoom: settled, left: scroller.scrollLeft, top: scroller.scrollTop }
      latest.current.onZoom(settled)
    }

    const pinchMetrics = () => {
      const [a, b] = [...pointers.values()]
      return { d: Math.hypot(a.x - b.x, a.y - b.y), mid: local((a.x + b.x) / 2, (a.y + b.y) / 2) }
    }

    const onPointerDown = (event: PointerEvent) => {
      if (event.pointerType === 'mouse' && event.button !== 0) return
      if (event.target instanceof Element && event.target.closest('button, a, input, textarea, select, [role="button"]')) {
        return
      }
      stopMomentum()
      swallowClick = false
      pointers.set(event.pointerId, { x: event.clientX, y: event.clientY })
      if (pointers.size === 1) {
        drag = {
          id: event.pointerId,
          type: event.pointerType,
          kind: 'pending',
          x0: event.clientX,
          y0: event.clientY,
          left0: scroller.scrollLeft,
          top0: scroller.scrollTop,
          samples: [{ x: event.clientX, y: event.clientY, t: event.timeStamp }],
        }
        return
      }
      if (pointers.size === 2 && event.pointerType !== 'mouse' && !pinch) {
        drag = null
        delete scroller.dataset.panning
        swallowClick = true
        live = live ?? startLive()
        if (!live) return
        const { d, mid } = pinchMetrics()
        pinch = { d0: Math.max(1, d) }
        live.anchor = anchorAt(live, mid)
        live.at = mid
      }
    }

    const onPointerMove = (event: PointerEvent) => {
      if (!pointers.has(event.pointerId)) return
      pointers.set(event.pointerId, { x: event.clientX, y: event.clientY })
      if (pinch) {
        if (!live?.anchor || pointers.size < 2) return
        const { d, mid } = pinchMetrics()
        const next = Math.min(latest.current.zoomMax, Math.max(1, (live.from * d) / pinch.d0))
        drawLive(live, next, live.anchor, mid)
        return
      }
      if (!drag || drag.id !== event.pointerId) return
      drag.samples.push({ x: event.clientX, y: event.clientY, t: event.timeStamp })
      if (drag.samples.length > 12) drag.samples.shift()
      const dx = event.clientX - drag.x0
      const dy = event.clientY - drag.y0
      if (drag.kind === 'pending') {
        if (Math.hypot(dx, dy) < SLOP_PX) return
        swallowClick = true
        const room = overflow()
        if (drag.type === 'mouse') drag.kind = room.x || room.y ? 'pan' : 'idle'
        else if (latest.current.zoom > 1) drag.kind = 'pan'
        else if (Math.abs(dx) > Math.abs(dy)) drag.kind = room.x ? 'pan' : 'swipe'
        else drag.kind = room.y ? 'pan' : 'idle'
        if (drag.kind === 'pan') {
          drag.x0 = event.clientX
          drag.y0 = event.clientY
          drag.left0 = scroller.scrollLeft
          drag.top0 = scroller.scrollTop
          if (drag.type === 'mouse') {
            try {
              scroller.setPointerCapture(event.pointerId)
            } catch {
              // The pointer is already gone; the pan simply ends with it.
            }
            scroller.dataset.panning = 'true'
          }
        }
        return
      }
      if (drag.kind === 'pan') {
        scroller.scrollLeft = drag.left0 - dx
        scroller.scrollTop = drag.top0 - dy
      }
    }

    const glide = (speed: { x: number; y: number }) => {
      if (Math.hypot(speed.x, speed.y) < MOMENTUM_MIN_PX_MS) return
      let vx = speed.x
      let vy = speed.y
      let last = performance.now()
      const step = (now: number) => {
        const dt = Math.min(48, now - last)
        last = now
        scroller.scrollLeft -= vx * dt
        scroller.scrollTop -= vy * dt
        const decay = Math.pow(MOMENTUM_DECAY, dt / 16)
        vx *= decay
        vy *= decay
        momentum = Math.hypot(vx, vy) > MOMENTUM_STOP_PX_MS ? requestAnimationFrame(step) : 0
      }
      momentum = requestAnimationFrame(step)
    }

    const onPointerEnd = (event: PointerEvent) => {
      if (!pointers.delete(event.pointerId)) return
      if (pinch) {
        if (pointers.size < 2) {
          pinch = null
          commitLive()
        }
        return
      }
      const ended = drag
      if (!ended || ended.id !== event.pointerId) return
      drag = null
      delete scroller.dataset.panning
      if (event.type !== 'pointerup') return
      if (ended.kind === 'swipe') {
        const dx = event.clientX - ended.x0
        const dy = event.clientY - ended.y0
        const far = Math.abs(dx) >= Math.min(SWIPE_MAX_PX, Math.max(SWIPE_MIN_PX, scroller.clientWidth * SWIPE_SHARE))
        const speed = velocity(ended.samples).x
        const flung = Math.abs(speed) >= FLING_PX_MS && Math.abs(dx) >= FLING_MIN_PX && Math.sign(speed) === Math.sign(dx)
        if ((far || flung) && Math.abs(dy) <= Math.abs(dx) * SWIPE_SLOPE) {
          latest.current.onTurn(latest.current.rtl ? dx > 0 : dx < 0)
        }
      } else if (ended.kind === 'pan' && ended.type !== 'mouse') {
        glide(velocity(ended.samples))
      }
    }

    const onClick = (event: MouseEvent) => {
      if (!swallowClick) return
      swallowClick = false
      event.stopPropagation()
      event.preventDefault()
    }

    const canScroll = (axis: 'x' | 'y', delta: number) => {
      const position = axis === 'y' ? scroller.scrollTop : scroller.scrollLeft
      const max =
        axis === 'y' ? scroller.scrollHeight - scroller.clientHeight : scroller.scrollWidth - scroller.clientWidth
      return delta > 0 ? position < max - 1 : position >= 1
    }

    const onWheel = (event: WheelEvent) => {
      const { dx, dy } = wheelPixels(event, scroller.clientHeight)
      if (event.ctrlKey) {
        // A trackpad pinch arrives as Ctrl+wheel.
        event.preventDefault()
        if (pinch) return
        live = live ?? startLive()
        if (!live) return
        const at = local(event.clientX, event.clientY)
        const step = Math.max(-WHEEL_ZOOM_STEP, Math.min(WHEEL_ZOOM_STEP, dy))
        const next = Math.min(latest.current.zoomMax, Math.max(1, live.zoom * Math.exp(-step / 100)))
        drawLive(live, next, anchorAt(live, at), at)
        window.clearTimeout(wheelZoomTimer)
        wheelZoomTimer = window.setTimeout(commitLive, WHEEL_ZOOM_COMMIT_MS)
        return
      }
      if (live) {
        event.preventDefault()
        return
      }
      stopMomentum()
      const outcome = wheel.feed({ dx, dy, time: event.timeStamp, canScroll })
      if (outcome.kind === 'scroll') return
      event.preventDefault()
      if (outcome.kind !== 'turn') return
      const forward = outcome.axis === 'y' || !latest.current.rtl ? outcome.delta > 0 : outcome.delta < 0
      latest.current.onTurn(forward)
    }

    // Safari's own pinch zoom ignores touch-action on some versions.
    const onGesture = (event: Event) => event.preventDefault()
    // A drag the browser could not scroll still ends in a fling, and Chrome drops the next tap's click
    // to stop that fling. Taking the moves keeps it from starting one.
    const onTouchMove = (event: TouchEvent) => {
      if (event.cancelable) event.preventDefault()
    }

    scroller.addEventListener('pointerdown', onPointerDown)
    scroller.addEventListener('pointermove', onPointerMove)
    scroller.addEventListener('pointerup', onPointerEnd)
    scroller.addEventListener('pointercancel', onPointerEnd)
    scroller.addEventListener('click', onClick, true)
    scroller.addEventListener('wheel', onWheel, { passive: false })
    scroller.addEventListener('gesturestart', onGesture)
    scroller.addEventListener('touchmove', onTouchMove, { passive: false })
    return () => {
      stopMomentum()
      window.clearTimeout(wheelZoomTimer)
      delete scroller.dataset.gestures
      delete scroller.dataset.panning
      scroller.removeEventListener('pointerdown', onPointerDown)
      scroller.removeEventListener('pointermove', onPointerMove)
      scroller.removeEventListener('pointerup', onPointerEnd)
      scroller.removeEventListener('pointercancel', onPointerEnd)
      scroller.removeEventListener('click', onClick, true)
      scroller.removeEventListener('wheel', onWheel)
      scroller.removeEventListener('gesturestart', onGesture)
      scroller.removeEventListener('touchmove', onTouchMove)
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

  // Runs after the paged view's own layout effect for the same zoom change, so the scroll position
  // the pinch ended on wins over its correction.
  useLayoutEffect(() => {
    const ended = pending.current
    if (!ended || ended.zoom !== zoom) return
    pending.current = null
    if (!surface) return
    surface.scrollLeft = ended.left
    surface.scrollTop = ended.top
  }, [zoom, surface])
}
