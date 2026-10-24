import { useState } from 'react'

/**
 * The page motion switches under Settings > Appearance: the cross-fade between pages, the cover
 * that flies into the series poster (lib/pageTransition.ts), and a faster pace for both. Kept on
 * this device, beside the background and accent, since a slow phone may want them off where a
 * desktop keeps them. Read at each navigation rather than held in React, because the history
 * listener lives outside it. The OS reduced-motion setting turns the motion off whatever is stored.
 */
export type MotionPref = 'pageTransitions' | 'coverMorph' | 'fastMotion'

const PREFS: Record<MotionPref, { key: string; on: boolean }> = {
  pageTransitions: { key: 'maki-page-transitions', on: true },
  coverMorph: { key: 'maki-cover-morph', on: true },
  fastMotion: { key: 'maki-fast-motion', on: false },
}

/** How much of each duration the motion takes: about half in fast mode. */
export const motionPace = () => (motionEnabled('fastMotion') ? 0.55 : 1)

export const prefersReducedMotion = () => window.matchMedia('(prefers-reduced-motion: reduce)').matches

export function motionEnabled(pref: MotionPref): boolean {
  const { key, on } = PREFS[pref]
  try {
    const stored = localStorage.getItem(key)
    return stored === null ? on : stored === 'on'
  } catch {
    return on
  }
}

export function useMotionPref(pref: MotionPref): [boolean, (on: boolean) => void] {
  const [on, setOn] = useState(() => motionEnabled(pref))
  const set = (next: boolean) => {
    setOn(next)
    const { key, on: byDefault } = PREFS[pref]
    try {
      if (next === byDefault) localStorage.removeItem(key)
      else localStorage.setItem(key, next ? 'on' : 'off')
    } catch {
      // Storage blocked: the switch flips, but the choice won't outlast this page.
    }
  }
  return [on, set]
}
