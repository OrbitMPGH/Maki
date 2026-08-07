import { useCallback, useEffect, useMemo, useRef } from 'react'

/** How often the accumulator wakes up. Also the largest slice any single accrual may add. */
const TICK_MS = 5000

/**
 * No pointer, key, wheel or scroll event for this long and the clock stops, even with the tab
 * visible and focused. Generous on purpose: the visibility and focus gates already catch walking
 * away from the browser, and this only has to catch walking away from a focused window, so the
 * cost of a slow reader on a dense page being cut off is worse than five idle minutes counted.
 */
const IDLE_MS = 300_000

export interface ReadingClock {
  /** Whole seconds accumulated since the last take, consumed. Sub-second remainder is kept. */
  take: () => number
  /** Whole seconds waiting to be reported, without consuming them. */
  pending: () => number
}

/**
 * Measures how long the reader is actually being read, in the only place that can know: the
 * client. The server sees a page request and cannot tell reading from a tab left open overnight,
 * so time is counted here — visible, focused and not idle — and reported to the server as deltas.
 *
 * `enabled` follows the same gate as the position writer, so an incognito sitting is not timed
 * either. Whatever is banked when it flips off stays banked and is simply never sent.
 */
export function useReadingClock(enabled: boolean): ReadingClock {
  const millis = useRef(0)
  const lastActivity = useRef(Date.now())
  const lastAccrual = useRef(Date.now())
  const running = useRef(false)

  /**
   * Credits the stretch since the last accrual, if it was spent reading. Both the timer and the
   * readers below call this, which is what makes a chapter change exact: leaving is nearly always
   * mid-interval, and crediting only on the tick dropped a couple of seconds every time — for a
   * keyboard reader, on every single chapter.
   */
  const accrue = useCallback(() => {
    const now = Date.now()
    const elapsed = now - lastAccrual.current
    lastAccrual.current = now

    // A stretch that arrives late (a throttled background timer, a sleeping laptop) is worth at
    // most one interval: the gap is time the machine was not showing anybody a page.
    if (
      running.current &&
      document.visibilityState === 'visible' &&
      document.hasFocus() &&
      now - lastActivity.current < IDLE_MS
    ) {
      millis.current += Math.min(elapsed, TICK_MS)
    }
  }, [])

  useEffect(() => {
    if (!enabled) return

    const poke = () => {
      lastActivity.current = Date.now()
    }

    // Capture phase: the reader stops some of these from bubbling (tap zones, the page strip),
    // and a page turn is exactly the signal that says somebody is still there. `keydown` matters
    // most — plenty of people read a whole volume on the arrow keys without touching the mouse.
    const events = ['pointerdown', 'pointermove', 'keydown', 'wheel', 'scroll', 'touchstart']
    for (const name of events) {
      window.addEventListener(name, poke, { capture: true, passive: true })
    }

    poke()
    lastAccrual.current = Date.now()
    running.current = true
    const timer = setInterval(accrue, TICK_MS)

    return () => {
      // Credit the part-interval before going quiet. Tracking flips off on every chapter change,
      // and this cleanup runs before the progress hook's flush, so what it credits still goes out
      // on that write rather than being stranded.
      accrue()
      running.current = false
      clearInterval(timer)
      for (const name of events) {
        window.removeEventListener(name, poke, { capture: true })
      }
    }
  }, [enabled, accrue])

  return useMemo(
    () => ({
      take: () => {
        accrue()
        const seconds = Math.floor(millis.current / 1000)
        millis.current -= seconds * 1000
        return seconds
      },
      pending: () => {
        accrue()
        return Math.floor(millis.current / 1000)
      },
    }),
    [accrue],
  )
}
