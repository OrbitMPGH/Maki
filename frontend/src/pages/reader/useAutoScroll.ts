import { useCallback, useEffect, useRef, useState } from 'react'

export const AUTO_SCROLL_MIN = 1
export const AUTO_SCROLL_MAX = 10
const DEFAULT_LEVEL = 5
const LEVEL_KEY = 'maki-reader-autoscroll-speed'

// Speed is in screens per second, so a level reads the same on a phone and a monitor. Level 1 takes
// 30 s a screen and level 10 takes 3 s, each step about 1.3x the one before.
const SLOWEST = 1 / 30
const STEP = Math.pow(10, 1 / (AUTO_SCROLL_MAX - AUTO_SCROLL_MIN))
const screensPerSecond = (level: number) => SLOWEST * Math.pow(STEP, level - AUTO_SCROLL_MIN)

// Anything else moving the strip (a fling, a tap step, the scrollbar, a seek) holds the scroll until
// it has been still this long, then picks up from wherever it landed.
const EXTERNAL_HOLD_MS = 900
const DRIFT_PX = 3
// The last screen gets this share of a screen's reading time before the strip counts as finished.
const END_DWELL_SHARE = 0.8

const clampLevel = (level: number) => Math.min(AUTO_SCROLL_MAX, Math.max(AUTO_SCROLL_MIN, Math.round(level)))

function storedLevel(): number {
  try {
    const stored = Number(localStorage.getItem(LEVEL_KEY))
    return Number.isFinite(stored) && stored > 0 ? clampLevel(stored) : DEFAULT_LEVEL
  } catch {
    return DEFAULT_LEVEL
  }
}

/** The auto-scroll speed level, remembered per device: the same level suits a phone and a desk differently. */
export function useAutoScrollLevel(): [number, (level: number) => void] {
  const [level, setLevel] = useState(storedLevel)
  const set = useCallback((next: number) => {
    const value = clampLevel(next)
    setLevel(value)
    try {
      localStorage.setItem(LEVEL_KEY, String(value))
    } catch {
      // Private window or blocked storage: the level lasts for this visit.
    }
  }, [])
  return [level, set]
}

/**
 * Scrolls the continuous strip down at a steady pace while `running`. A finger on the screen holds
 * it, and any scroll it did not make itself holds it until that settles. At the bottom it waits most
 * of a screen's reading time, then calls `onEnd`, again every so often until the strip changes.
 */
export function useAutoScroll(scroller: HTMLElement | null, running: boolean, level: number, onEnd: () => void) {
  const onEndRef = useRef(onEnd)
  onEndRef.current = onEnd
  const levelRef = useRef(level)
  levelRef.current = level

  useEffect(() => {
    if (!scroller || !running) return
    let frame = 0
    let last = performance.now()
    let pos = scroller.scrollTop
    let heldUntil = 0
    let touching = false
    let endSince = 0

    const tick = (now: number) => {
      frame = requestAnimationFrame(tick)
      const dt = Math.min(0.1, (now - last) / 1000)
      last = now
      // Browsers round scrollTop, so `pos` keeps the fraction that slow speeds need to move at all.
      if (Math.abs(scroller.scrollTop - pos) > DRIFT_PX) {
        pos = scroller.scrollTop
        heldUntil = now + EXTERNAL_HOLD_MS
      }
      if (touching || now < heldUntil || document.hidden) {
        endSince = 0
        return
      }
      const speed = screensPerSecond(levelRef.current)
      const max = scroller.scrollHeight - scroller.clientHeight
      if (pos >= max - 1) {
        if (endSince === 0) endSince = now
        if (now - endSince >= (END_DWELL_SHARE / speed) * 1000) {
          endSince = now
          onEndRef.current()
        }
        return
      }
      endSince = 0
      pos = Math.min(max, pos + scroller.clientHeight * speed * dt)
      scroller.scrollTop = pos
    }

    const onTouchStart = () => {
      touching = true
    }
    const onTouchEnd = (event: TouchEvent) => {
      if (event.touches.length > 0) return
      touching = false
      heldUntil = performance.now() + EXTERNAL_HOLD_MS
    }

    frame = requestAnimationFrame(tick)
    scroller.addEventListener('touchstart', onTouchStart, { passive: true })
    scroller.addEventListener('touchend', onTouchEnd, { passive: true })
    scroller.addEventListener('touchcancel', onTouchEnd, { passive: true })
    return () => {
      cancelAnimationFrame(frame)
      scroller.removeEventListener('touchstart', onTouchStart)
      scroller.removeEventListener('touchend', onTouchEnd)
      scroller.removeEventListener('touchcancel', onTouchEnd)
    }
  }, [scroller, running])
}
