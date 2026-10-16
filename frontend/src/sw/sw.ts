import { cacheNames, type PageToWorker } from '../pwa/contract'
import { deleteUserRecords } from '../pwa/db'
import { sw } from './scope'
import { activateShell, installShell, isAppNavigation, navigation, shellRoute } from './shell'
import { dataRoute } from './data'
import { chapterRoute } from './chapters'
import { captureProgress, progressChapterId } from './progress'
import { registerPushHandlers } from './push'
import { setUser } from './user'

sw.addEventListener('install', (event) => {
  event.waitUntil(installShell().then(() => sw.skipWaiting()))
})

sw.addEventListener('activate', (event) => {
  event.waitUntil(activateShell().then(() => sw.clients.claim()))
})

function isMessage(data: unknown): data is PageToWorker {
  if (!data || typeof data !== 'object') return false
  const { type, userId } = data as { type?: unknown; userId?: unknown }
  if (type === 'user') return userId === null || typeof userId === 'number'
  return (type === 'purge-browse' || type === 'purge-user') && typeof userId === 'number'
}

async function handleMessage(message: PageToWorker): Promise<void> {
  if (message.type === 'user') return setUser(message.userId)
  const { userId } = message
  await caches.delete(cacheNames.data(userId))
  await caches.delete(cacheNames.covers(userId))
  if (message.type === 'purge-user') {
    await caches.delete(cacheNames.chapters(userId))
    await deleteUserRecords(userId)
  }
}

sw.addEventListener('message', (event) => {
  if (isMessage(event.data)) event.waitUntil(handleMessage(event.data))
})

sw.addEventListener('fetch', (event) => {
  const { request } = event
  const url = new URL(request.url)
  if (url.origin !== sw.location.origin) return
  if (url.pathname.startsWith('/signalr') || url.pathname.startsWith('/api/v1/opds')) return

  if (request.method === 'PUT') {
    const chapterId = progressChapterId(url)
    if (chapterId !== null) event.respondWith(captureProgress(event, url, chapterId))
    return
  }
  if (request.method !== 'GET') return

  if (request.mode === 'navigate') {
    if (isAppNavigation(request, url)) event.respondWith(navigation(request))
    return
  }

  const route = shellRoute(event, url) ?? chapterRoute(event, url) ?? dataRoute(event, url)
  if (route) event.respondWith(route)
})

registerPushHandlers()
