import { cacheNames } from '../pwa/contract'
import { isImage, isJson, isUnreachable, markOffline, matchIn, offlineResponse, store, tryFetch } from './net'
import { currentUser, setUser, userGeneration } from './user'

const API = '/api/v1/'
const DATA_CAP = 2000
const COVER_CAP = 3000

/**
 * The reads Home, Library, the series page and the reader need, kept for offline use. Anything else
 * under the API passes through and answers the synthetic 503 when the server is unreachable.
 */
const READ_ALLOWLIST: RegExp[] = [
  /^auth\/me$/,
  /^series$/,
  /^series\/lookup$/,
  /^series\/\d+$/,
  /^series\/\d+\/files\/summary$/,
  /^chapter$/,
  /^reader\/used$/,
  /^reader\/series\/\d+\/(?:progress|continue)$/,
  /^reader\/chapter\/\d+\/bookmarks$/,
  /^home(?:\/[\w-]+)*$/,
  /^rails$/,
  /^rails\/\d+\/items$/,
  /^rootfolder$/,
  /^tags$/,
  /^library\/filters$/,
  /^settings\/(?:ui|reader|setup)$/,
  /^readingprofiles$/,
  /^inbox\/unread-count$/,
]

export function apiPath(url: URL): string | null {
  return url.pathname.startsWith(API) ? url.pathname.slice(API.length) : null
}

/**
 * A 401 means the session is gone: stop serving and storing that user's data. Caches stay. A 401 to
 * a request sent before the user last changed answers an earlier session and is ignored.
 */
export function noteStatus(res: Response, since: number): void {
  if (res.status === 401 && since === userGeneration()) void setUser(null)
}

/**
 * Network first. A live 200 of the right type is stored when a user is known; an unreachable
 * server is answered from the cache with the offline marker, or with the synthetic 503.
 */
export async function networkFirst(
  event: FetchEvent,
  cacheName: ((userId: number) => string) | null,
  accept: (res: Response) => boolean = isJson,
  onLive?: (res: Response) => Promise<void>,
): Promise<Response> {
  const { request } = event
  const before = await currentUser()
  const since = userGeneration()
  const res = await tryFetch(request)
  if (res && !isUnreachable(res)) {
    noteStatus(res, since)
    if (onLive) await onLive(res)
    // A response sent under one account must not land in the cache of whoever signed in meanwhile.
    // `auth/me` is the exception: it is what says who that is.
    const userId = await currentUser()
    const sameUser = userId === before || onLive !== undefined
    if (cacheName && userId !== null && sameUser && res.status === 200 && accept(res)) {
      event.waitUntil(store(cacheName(userId), request.url, res.clone(), DATA_CAP))
    }
    return res
  }
  const userId = await currentUser()
  if (cacheName && userId !== null) {
    const cached = await matchIn(cacheName(userId), request.url)
    if (cached) return markOffline(cached)
  }
  return offlineResponse()
}

/** A live `auth/me` also tells the worker who is signed in, in case the page's message was lost. */
async function learnUser(res: Response): Promise<void> {
  if (res.status !== 200 || !isJson(res)) return
  try {
    const me = (await res.clone().json()) as { id?: unknown }
    if (typeof me.id === 'number') await setUser(me.id)
  } catch {
    // Not the shape expected; the page's own message still sets it.
  }
}

/** Covers carry `?v=`, so the full URL is a safe cache key. */
async function cover(event: FetchEvent): Promise<Response> {
  const { request } = event
  const userId = await currentUser()
  if (userId === null) return passThrough(request)
  const cached = await matchIn(cacheNames.covers(userId), request.url)
  if (cached) return cached
  const since = userGeneration()
  const res = await tryFetch(request)
  if (!res || isUnreachable(res)) {
    // An img with a srcset never falls back to src, and offline saves only warm cover.jpg.
    const full = request.url.replace('/cover-sm.jpg', '/cover.jpg')
    const fallback = full !== request.url ? await matchIn(cacheNames.covers(userId), full) : undefined
    return fallback ?? offlineResponse()
  }
  noteStatus(res, since)
  if (res.status === 200 && isImage(res)) {
    event.waitUntil(store(cacheNames.covers(userId), request.url, res.clone(), COVER_CAP))
  }
  return res
}

export async function passThrough(request: Request): Promise<Response> {
  const since = userGeneration()
  const res = await tryFetch(request)
  if (!res || isUnreachable(res)) return offlineResponse()
  noteStatus(res, since)
  return res
}

/** GETs under the API that are not the reader's. Undefined for anything that is not an API path. */
export function dataRoute(event: FetchEvent, url: URL): Promise<Response> | undefined {
  const path = apiPath(url)
  if (path === null) return undefined
  if (path === 'auth/me') return networkFirst(event, cacheNames.data, isJson, learnUser)
  if (path.startsWith('mediacover/')) return cover(event)
  if (READ_ALLOWLIST.some((re) => re.test(path))) return networkFirst(event, cacheNames.data)
  return passThrough(event.request)
}
