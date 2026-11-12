import { useEffect, useState, useSyncExternalStore } from 'react'

/** Where the continuous strip is scrolled to, measured by ContinuousView on scroll. */
export interface StripPlace {
  /** The page the reader counts as current (the one owning the middle band). */
  page: number
  /** How far down that page the top of the screen is, 0 to 1. A resume puts this point back at the top. */
  offset: number
  /** The page is taller than the screen, the only case where the offset is worth saving. */
  tall: boolean
  /** Share of the chapter (0 to 1) at the middle of the screen, counting each page as one equal step. */
  progress: number
}

/**
 * A small store the strip writes on every scroll frame and the rail and the progress writes read,
 * so scrolling never re-renders the whole reader. `seek` is registered by the strip.
 */
export interface StripTracker {
  get: () => StripPlace | null
  set: (place: StripPlace | null) => void
  subscribe: (listener: () => void) => () => void
  /** Scrolls the strip so `progress` sits in the middle of the screen. Null while no strip is mounted. */
  seek: ((progress: number) => void) | null
}

export function createStripTracker(): StripTracker {
  let place: StripPlace | null = null
  const listeners = new Set<() => void>()
  return {
    get: () => place,
    set: (next) => {
      if (
        next === place ||
        (next && place && next.page === place.page && next.offset === place.offset && next.progress === place.progress && next.tall === place.tall)
      ) {
        return
      }
      place = next
      for (const listener of listeners) listener()
    },
    subscribe: (listener) => {
      listeners.add(listener)
      return () => {
        listeners.delete(listener)
      }
    },
    seek: null,
  }
}

export function useStripPlace(tracker: StripTracker): StripPlace | null {
  return useSyncExternalStore(tracker.subscribe, tracker.get, tracker.get)
}

/** How long the strip has to rest before its offset counts as a position worth saving. */
const SETTLE_MS = 400

/**
 * The strip's offset once scrolling pauses, for the progress writes: 0 on a page no taller than
 * the screen, so a strip of ordinary pages writes exactly what it wrote before offsets existed.
 * Null until the strip has settled at least once since `key` (the chapter) last changed.
 */
function useSettledOffset(tracker: StripTracker, enabled: boolean, key: number): { page: number; offset: number } | null {
  const [settled, setSettled] = useState<{ key: number; page: number; offset: number } | null>(null)
  useEffect(() => {
    if (!enabled) return
    let timer: ReturnType<typeof setTimeout> | undefined
    const unsubscribe = tracker.subscribe(() => {
      clearTimeout(timer)
      timer = setTimeout(() => {
        const place = tracker.get()
        if (!place) return
        const offset = place.tall ? Math.round(place.offset * 10_000) / 10_000 : 0
        setSettled((current) =>
          current && current.key === key && current.page === place.page && current.offset === offset
            ? current
            : { key, page: place.page, offset },
        )
      }, SETTLE_MS)
    })
    return () => {
      clearTimeout(timer)
      unsubscribe()
    }
  }, [tracker, enabled, key])
  return enabled && settled?.key === key ? settled : null
}

/** Where the reader resumed a chapter, the offset it reports until the strip has settled somewhere. */
export interface StripResume {
  chapterId: number
  page: number
  offset: number
}

/**
 * The offset the progress writes carry with `page`, or undefined where none applies (the paged
 * layouts, a finished chapter). Starts as the resume's own offset, so opening a chapter is not
 * mistaken for moving within it.
 */
export function useStripOffset(
  tracker: StripTracker,
  enabled: boolean,
  chapterId: number,
  page: number,
  resumed: StripResume | null,
): number | undefined {
  const settled = useSettledOffset(tracker, enabled, chapterId)
  if (!enabled) return undefined
  if (settled?.page === page) return settled.offset
  return resumed?.chapterId === chapterId && resumed.page === page ? resumed.offset : 0
}

/**
 * Where a chapter resumes inside its page: from this device's newest queued write at the resume page
 * when there is one (offline, it is newer than anything the server knows), else from the manifest.
 */
export function resumeOffsetOf(
  rows: readonly { pageIndex: number; pageOffset?: number }[],
  resumePage: number,
  base: { resumePage: number; resumeOffset?: number },
): number {
  const queued = rows.filter((row) => row.pageIndex === resumePage).at(-1)
  const offset = queued ? (queued.pageOffset ?? 0) : base.resumePage === resumePage ? (base.resumeOffset ?? 0) : 0
  return Number.isFinite(offset) ? Math.min(1, Math.max(0, offset)) : 0
}
