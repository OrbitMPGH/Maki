import { msg } from '@lingui/core/macro'
import { scrollBehavior } from '../../lib/scrollBehavior'
import type { ScreenTurnMotion } from './prefs'

export const SCREEN_TURN_MOTIONS = [
  { value: 'smooth', label: msg`Smooth` },
  { value: 'quick', label: msg`Quick` },
  { value: 'instant', label: msg`Instant` },
] satisfies { value: ScreenTurnMotion; label: ReturnType<typeof msg> }[]

const QUICK_TURN_MS = 90

export function scrollStripTo(surface: HTMLElement, top: number, motion: ScreenTurnMotion): (() => void) | undefined {
  const behavior = scrollBehavior()
  if (motion !== 'quick' || behavior === 'auto') {
    surface.scrollTo({ top, behavior: motion === 'instant' ? 'instant' : behavior })
    return
  }

  const from = surface.scrollTop
  const start = performance.now()
  const height = surface.clientHeight
  let expected = from
  let frame = requestAnimationFrame(tick)
  function tick(now: number) {
    // Give the strip back as soon as another input, scroll anchoring or layout moves it.
    if (surface.clientHeight !== height || Math.abs(surface.scrollTop - expected) > 2) return
    const progress = Math.min(1, Math.max(0, (now - start) / QUICK_TURN_MS))
    surface.scrollTo({ top: from + (top - from) * progress, behavior: 'instant' })
    expected = surface.scrollTop
    if (progress < 1) frame = requestAnimationFrame(tick)
  }
  return () => cancelAnimationFrame(frame)
}
