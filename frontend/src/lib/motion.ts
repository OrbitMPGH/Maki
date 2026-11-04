import { motionPace } from './motionPrefs'

/**
 * The house motion for script: motion/react, the Web Animations calls and Mantine's overlays
 * (theme.ts). Mirrors `--ease`, `--ease-loop` and `--dur-*` at the top of theme.css, and the two must
 * stay in step.
 */

/** `--ease` as bezier points, which motion/react takes. */
export const EASE = [0.16, 1, 0.3, 1] as const
/** `--ease` as a CSS timing function, for element.animate() and Mantine's transitions. */
export const EASE_CSS = 'cubic-bezier(0.16, 1, 0.3, 1)'

/** `--ease-loop`, the symmetric curve for anything that repeats (pulses, sweeps). Spinners stay linear. */
export const EASE_LOOP = [0.45, 0, 0.55, 1] as const

/** `--dur-*`, in seconds. */
export const DUR = { fast: 0.12, base: 0.15, medium: 0.18, slow: 0.35 } as const

/**
 * Rewind and the Discover hero stage a slide or a backdrop rather than answer a click, so they run
 * longer than the UI scale. They still take the house curve.
 */
export const CINEMATIC = { enter: 0.4, fade: 0.55, backdrop: 0.9, countUp: 1.6 } as const

/** A duration in seconds at the current pace: shorter under "Faster animations". */
export const paced = (seconds: number) => seconds * motionPace()

/** The same in milliseconds, as element.animate() and Mantine take them. */
export const pacedMs = (seconds: number) => Math.round(seconds * 1000 * motionPace())
