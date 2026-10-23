import { lazy, type ComponentType } from 'react'

const loaders: (() => Promise<unknown>)[] = []
let preloading = false

/**
 * `lazy`, and a place in the idle preload. A route whose chunk is still on the network when it is
 * opened holds up the page transition (lib/pageTransition.ts) until it gives up, and the page then
 * lands without one, so every route's chunk is fetched once the app has settled.
 */
export function lazyRoute<C extends ComponentType>(load: () => Promise<{ default: C }>) {
  loaders.push(load)
  return lazy(load)
}

/**
 * True while the idle preload is fetching. A chunk it fails to fetch (offline, say) is no stale
 * deploy, so main.tsx's reload-on-missing-chunk handler leaves it to the real navigation.
 */
export const preloadingRoutes = () => preloading

/** Fetches the route chunks one at a time while the browser is idle. Returns a cancel. */
export function preloadRoutes(): () => void {
  let cancelled = false
  // Safari has no requestIdleCallback.
  const idle = (run: () => void) =>
    typeof requestIdleCallback === 'function' ? requestIdleCallback(run, { timeout: 3000 }) : setTimeout(run, 500)
  const next = (i: number) => {
    if (cancelled || i >= loaders.length) {
      preloading = false
      return
    }
    idle(() => {
      if (cancelled) return
      preloading = true
      loaders[i]()
        .catch(() => {})
        .finally(() => next(i + 1))
    })
  }
  next(0)
  return () => {
    cancelled = true
    preloading = false
  }
}
