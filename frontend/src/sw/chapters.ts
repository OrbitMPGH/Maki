import { cacheNames } from '../pwa/contract'
import { isJson, isUnreachable, markOffline, matchIn, offlineResponse, store, tryFetch } from './net'
import { apiPath, noteStatus, passThrough } from './data'
import { currentUser } from './user'

const MANIFEST = /^reader\/chapter\/\d+$/
const PAGE = /^reader\/chapter\/\d+\/page\/\d+$/
const THUMB = /^reader\/chapter\/\d+\/thumb\/\d+$/

/**
 * A saved copy is replaced only by a live one of the same file version, so a chapter whose file
 * changed keeps opening against the pages stored with it until the re-download finishes.
 */
async function refreshSaved(cacheName: string, url: string, live: Response): Promise<void> {
  const saved = await matchIn(cacheName, url)
  if (!saved) return
  try {
    const [before, now] = (await Promise.all([saved.json(), live.clone().json()])) as { pageVersion?: unknown }[]
    if (before.pageVersion === now.pageVersion) await store(cacheName, url, live)
  } catch {
    // Not the shape expected; the saved copy stays.
  }
}

/**
 * The manifest is network first. A live copy refreshes the data cache and, when the chapter is
 * saved on this device, the saved copy too, so a later offline open resumes where reading stopped.
 * Offline, only a saved chapter opens: anything else would show pages that cannot load.
 */
async function manifest(event: FetchEvent): Promise<Response> {
  const { request } = event
  const before = await currentUser()
  const res = await tryFetch(request)
  if (res && !isUnreachable(res)) {
    noteStatus(res)
    const userId = await currentUser()
    if (userId !== null && userId === before && res.status === 200 && isJson(res)) {
      event.waitUntil(store(cacheNames.data(userId), request.url, res.clone()))
      event.waitUntil(refreshSaved(cacheNames.chapters(userId), request.url, res.clone()))
    }
    return res
  }
  const userId = await currentUser()
  if (userId !== null) {
    const saved = await matchIn(cacheNames.chapters(userId), request.url)
    if (saved) return markOffline(saved)
  }
  return offlineResponse()
}

/** Saved pages come from the device; anything else goes to the network and is not kept. */
async function page(event: FetchEvent): Promise<Response> {
  const userId = await currentUser()
  if (userId !== null) {
    const saved = await matchIn(cacheNames.chapters(userId), event.request.url)
    if (saved) return saved
  }
  return passThrough(event.request)
}

/** Thumbnails are never saved, but the full page they shrink usually is. */
async function thumb(event: FetchEvent, url: URL): Promise<Response> {
  const res = await tryFetch(event.request)
  if (res && !isUnreachable(res)) {
    noteStatus(res)
    return res
  }
  const userId = await currentUser()
  if (userId !== null) {
    const full = `${url.origin}${url.pathname.replace('/thumb/', '/page/')}${url.search}`
    const saved = await matchIn(cacheNames.chapters(userId), full)
    if (saved) return markOffline(saved)
  }
  return offlineResponse()
}

export function chapterRoute(event: FetchEvent, url: URL): Promise<Response> | undefined {
  const path = apiPath(url)
  if (path === null) return undefined
  if (MANIFEST.test(path)) return manifest(event)
  if (PAGE.test(path)) return page(event)
  if (THUMB.test(path)) return thumb(event, url)
  return undefined
}
