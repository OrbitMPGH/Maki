import { OFFLINE_CODE, OFFLINE_HEADER } from '../pwa/contract'

/**
 * A stopped Maki behind a reverse proxy still answers: a 502 or 504 with the proxy's own HTML or an
 * empty body. Maki's own 502s always carry JSON, so those are real answers. A 503 never counts.
 */
export function isUnreachable(res: Response): boolean {
  if (res.status !== 502 && res.status !== 504) return false
  return !(res.headers.get('Content-Type') ?? '').includes('application/json')
}

/** The network's answer, or null when the fetch rejected. */
export async function tryFetch(request: Request): Promise<Response | null> {
  try {
    return await fetch(request)
  } catch {
    return null
  }
}

export function offlineResponse(): Response {
  return new Response(JSON.stringify({ code: OFFLINE_CODE }), {
    status: 503,
    headers: { 'Content-Type': 'application/json', [OFFLINE_HEADER]: '1' },
  })
}

/** A cached response re-issued with the offline marker, so the page can tell it from a live one. */
export function markOffline(res: Response): Response {
  const headers = new Headers(res.headers)
  headers.set(OFFLINE_HEADER, '1')
  return new Response(res.body, { status: res.status, statusText: res.statusText, headers })
}

export function isJson(res: Response): boolean {
  return (res.headers.get('Content-Type') ?? '').includes('application/json')
}

export function isImage(res: Response): boolean {
  return (res.headers.get('Content-Type') ?? '').startsWith('image/')
}

export async function matchIn(cacheName: string, url: string): Promise<Response | undefined> {
  const cache = await caches.open(cacheName)
  return cache.match(url, { ignoreVary: true })
}

const TRIM_EVERY = 25
const sinceTrim = new Map<string, number>()

/**
 * Stores a copy and keeps the cache near `cap` entries by dropping the first keys. That is
 * insertion order in Chromium and unpromised elsewhere, and a re-put keeps its slot, so this is an
 * approximate LRU at best. Listing every key is the expensive part, so it runs on a cache's first
 * store after the worker starts and then once per `TRIM_EVERY` stores: a cache can overshoot `cap`
 * by that many entries.
 */
export async function store(cacheName: string, url: string, res: Response, cap?: number): Promise<void> {
  try {
    const cache = await caches.open(cacheName)
    try {
      await cache.put(url, res)
    } catch (err) {
      // The overshoot reached the quota: trim now so the next store fits. This copy is lost.
      if (cap !== undefined && err instanceof DOMException && err.name === 'QuotaExceededError') {
        sinceTrim.set(cacheName, 0)
        await trimTo(cache, cap)
      }
      return
    }
    if (cap === undefined) return
    const count = (sinceTrim.get(cacheName) ?? TRIM_EVERY - 1) + 1
    if (count < TRIM_EVERY) {
      sinceTrim.set(cacheName, count)
      return
    }
    sinceTrim.set(cacheName, 0)
    await trimTo(cache, cap)
  } catch {
    // A body that could not be read; the response already went to the page.
  }
}

async function trimTo(cache: Cache, cap: number): Promise<void> {
  const keys = await cache.keys()
  for (const key of keys.slice(0, Math.max(0, keys.length - cap))) await cache.delete(key)
}
