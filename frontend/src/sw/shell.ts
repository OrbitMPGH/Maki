import { cacheNames, SHELL_CACHE_PREFIX } from '../pwa/contract'
import { isJson, isUnreachable, matchIn, store, tryFetch } from './net'

const SHELL = cacheNames.shell(__BUILD_ID__)
const PRECACHED = new Set(__PRECACHE__)
const CATALOGS = new Set(__CATALOGS__)
const NAVIGATION_TIMEOUT_MS = 5000
const SOURCE_ICON_CAP = 500

/** Safari fails a whole `addAll` on one bad entry, so the list goes in small batches. */
export async function installShell(): Promise<void> {
  const cache = await caches.open(SHELL)
  const list = [...PRECACHED]
  for (let i = 0; i < list.length; i += 20) {
    const batch = list.slice(i, i + 20).map((url) => new Request(url, { cache: 'no-cache' }))
    try {
      await cache.addAll(batch)
    } catch {
      await Promise.allSettled(batch.map((request) => cache.add(request)))
    }
  }
}

export async function activateShell(): Promise<void> {
  for (const name of await caches.keys()) {
    if (name.startsWith(SHELL_CACHE_PREFIX) && name !== SHELL) await caches.delete(name)
  }
  const assets = await caches.open(cacheNames.assets)
  for (const request of await assets.keys()) {
    if (!CATALOGS.has(new URL(request.url).pathname)) await assets.delete(request)
  }
}

function isPrecached(url: URL): boolean {
  return url.search === '' && PRECACHED.has(url.pathname)
}

export function isAppNavigation(request: Request, url: URL): boolean {
  if (request.mode !== 'navigate') return false
  return !/^\/(?:api\/|signalr|swagger)/.test(url.pathname)
}

/**
 * Network first, raced against a timeout, so a reachable server always hands out the current
 * index.html and an unreachable one falls back to the precached copy.
 */
export async function navigation(request: Request): Promise<Response> {
  const network = tryFetch(request)
  const timeout = new Promise<null>((resolve) => setTimeout(() => resolve(null), NAVIGATION_TIMEOUT_MS))
  const res = await Promise.race([network, timeout])
  if (res && !isUnreachable(res)) return res
  const cached = await matchIn(SHELL, '/index.html')
  if (cached) return cached
  return (await network) ?? Response.error()
}

async function cacheFirst(event: FetchEvent, cacheName: string, keep: boolean, cap?: number): Promise<Response> {
  const { request } = event
  const cached = await matchIn(cacheName, request.url)
  if (cached) return cached
  const res = await fetch(request)
  if (keep && res.status === 200 && res.type === 'basic') event.waitUntil(store(cacheName, request.url, res.clone(), cap))
  return res
}

/** Static files the shell owns. Undefined when the request is not one of them. */
export function shellRoute(event: FetchEvent, url: URL): Promise<Response> | undefined {
  if (isPrecached(url)) return cacheFirst(event, SHELL, false)
  if (url.pathname.startsWith('/assets/')) return cacheFirst(event, cacheNames.assets, true)
  if (url.pathname.startsWith('/source-icons/')) return cacheFirst(event, cacheNames.meta, true, SOURCE_ICON_CAP)
  if (url.pathname === '/initialize.json') return initialize(event)
  return undefined
}

async function initialize(event: FetchEvent): Promise<Response> {
  const { request } = event
  const res = await tryFetch(request)
  if (res && !isUnreachable(res)) {
    if (res.status === 200 && isJson(res)) event.waitUntil(store(cacheNames.meta, request.url, res.clone()))
    return res
  }
  const cached = await matchIn(cacheNames.meta, request.url)
  if (cached) return cached
  return res ?? Response.error()
}
