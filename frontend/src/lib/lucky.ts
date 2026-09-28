import { useCallback } from 'react'
import { useAnimate } from 'motion/react'
import { missingCount } from '../api/hooks'
import type { SeriesDto } from '../api/types'

/**
 * Still wants chapters, or has downloaded chapters left to read. A series nothing has reported
 * reading progress for counts as unread: Maki cannot tell it was finished, so it stays rollable.
 */
export function isUnfinished(s: SeriesDto): boolean {
  return missingCount(s) > 0 || (s.chapterFileCount > 0 && (s.readChapterCount ?? 0) < s.chapterFileCount)
}

/** Uniform pick. `exclude` is ignored when it would leave nothing to pick from. */
export function pickRandom<T>(pool: T[], exclude?: (t: T) => boolean): T | null {
  const narrowed = exclude ? pool.filter((x) => !exclude(x)) : pool
  const from = narrowed.length > 0 ? narrowed : pool
  if (from.length === 0) return null
  return from[Math.floor(Math.random() * from.length)]
}

export function prefersReducedMotion(): boolean {
  return typeof window !== 'undefined' && window.matchMedia('(prefers-reduced-motion: reduce)').matches
}

// theme.css --ease; motion wants the bezier points, not the custom property.
const EASE = [0.16, 1, 0.3, 1] as const

/** Spins whatever the returned ref is attached to. Resolves when the spin ends. */
export function useDiceTumble() {
  const [scope, animate] = useAnimate<HTMLSpanElement>()
  const tumble = useCallback(
    async (duration: number) => {
      if (!scope.current || prefersReducedMotion()) return
      await animate(
        scope.current,
        { rotate: [0, 720], scale: [1, 1.3, 1] },
        { duration: duration / 1000, ease: EASE },
      )
    },
    [animate, scope],
  )
  return { scope, tumble }
}
