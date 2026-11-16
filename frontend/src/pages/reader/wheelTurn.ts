// A pause this long between wheel events ends a gesture. Trackpad inertia keeps events coming
// closer together than this until it has fully stopped.
const WHEEL_GAP_MS = 220
// Scroll pushed against the edge before the page turns. One mouse notch (about 100 px, or three
// 16 px lines in Firefox) is enough; a trackpad needs a deliberate push.
const WHEEL_TURN_PX = 40
// After a turn, a stream that has decayed to almost nothing and then picks up again is a new swipe
// started inside the old one's inertia.
const WHEEL_TAIL_PX = 3
const WHEEL_RESTART_PX = 12

export interface WheelInput {
  /** Pixels, already normalised from lines or pages. */
  dx: number
  dy: number
  time: number
  /** Whether the scroller can still move in the direction of the delta, on that axis. */
  canScroll: (axis: 'x' | 'y', delta: number) => boolean
}

/** `scroll` leaves the event to the browser, `swallow` cancels it, `turn` cancels it and turns the page. */
export type WheelOutcome = { kind: 'scroll' } | { kind: 'swallow' } | { kind: 'turn'; axis: 'x' | 'y'; delta: number }

interface Gesture {
  axis: 'x' | 'y'
  last: number
  push: number
  /** Scrolled the page in this gesture, so reaching the edge does not also turn it. */
  scrolled: boolean
  turned: boolean
  floor: number
}

/**
 * Turns wheel events into at most one page turn per physical gesture. The page scrolls first; a
 * gesture that scrolled it to the edge stops there, and the next one, pushing on against the edge,
 * turns the page. Everything after a turn is swallowed until the gesture ends, so a trackpad's
 * inertia neither turns a second page nor scrolls the new one.
 */
export class WheelTurn {
  private gesture: Gesture | null = null

  reset() {
    this.gesture = null
  }

  feed({ dx, dy, time, canScroll }: WheelInput): WheelOutcome {
    const size = Math.max(Math.abs(dx), Math.abs(dy))
    let g = this.gesture
    const restarted = g?.turned && g.floor <= WHEEL_TAIL_PX && size >= WHEEL_RESTART_PX
    if (!g || time - g.last > WHEEL_GAP_MS || restarted) {
      g = { axis: Math.abs(dy) >= Math.abs(dx) ? 'y' : 'x', last: time, push: 0, scrolled: false, turned: false, floor: size }
      this.gesture = g
    }
    g.last = time
    g.floor = Math.min(g.floor, size)
    if (g.turned) return { kind: 'swallow' }
    const delta = g.axis === 'y' ? dy : dx
    if (delta === 0) return { kind: 'scroll' }
    if (canScroll(g.axis, delta)) {
      g.scrolled = true
      g.push = 0
      return { kind: 'scroll' }
    }
    if (g.scrolled) return { kind: 'scroll' }
    // A push the other way starts the count again.
    g.push = Math.sign(g.push) === Math.sign(delta) ? g.push + delta : delta
    if (Math.abs(g.push) < WHEEL_TURN_PX) return { kind: 'swallow' }
    g.turned = true
    g.floor = size
    return { kind: 'turn', axis: g.axis, delta: g.push }
  }
}

/** A wheel event's deltas in pixels. */
export function wheelPixels(event: WheelEvent, pageHeight: number): { dx: number; dy: number } {
  const unit = event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? pageHeight : 1
  return { dx: event.deltaX * unit, dy: event.deltaY * unit }
}
