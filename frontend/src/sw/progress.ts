import { CHANNEL_NAME, cacheNames, QUEUED_HEADER, REPLAY_HEADER, type WorkerToPage } from '../pwa/contract'
import { insertCapturedProgress } from '../pwa/db'
import { randomUUID } from '../lib/uuid'
import { isUnreachable, matchIn, tryFetch } from './net'
import { apiPath, noteStatus } from './data'
import { currentUser } from './user'

const PROGRESS = /^reader\/chapter\/(\d+)\/progress$/

interface ProgressBody {
  pageIndex?: unknown
  completed?: unknown
  seconds?: unknown
  opId?: unknown
  seriesId?: unknown
  clientTime?: unknown
}

export function progressChapterId(url: URL): number | null {
  const match = apiPath(url)?.match(PROGRESS)
  return match ? Number(match[1]) : null
}

/** The body carries no series, so it comes from the manifest the reader fetched for this chapter. */
async function seriesOf(userId: number, chapterId: number, origin: string): Promise<number> {
  const url = `${origin}/api/v1/reader/chapter/${chapterId}`
  const cached = (await matchIn(cacheNames.data(userId), url)) ?? (await matchIn(cacheNames.chapters(userId), url))
  try {
    const manifest = cached ? ((await cached.json()) as { seriesId?: unknown }) : null
    return typeof manifest?.seriesId === 'number' ? manifest.seriesId : 0
  } catch {
    return 0
  }
}

function broadcast(message: WorkerToPage): void {
  if (typeof BroadcastChannel === 'undefined') return
  const channel = new BroadcastChannel(CHANNEL_NAME)
  channel.postMessage(message)
  channel.close()
}

/**
 * A live progress write that cannot reach the server is queued instead of lost. Everything the
 * server did answer, 401 and other errors included, goes back to the page as it came.
 */
export async function captureProgress(event: FetchEvent, url: URL, chapterId: number): Promise<Response> {
  // The page's replay of a queued row keeps that row itself when this fails.
  if (event.request.headers.get(REPLAY_HEADER) === '1') {
    const res = await fetch(event.request)
    noteStatus(res)
    return res
  }
  // Read before the attempt, which consumes the body.
  const text = await event.request.clone().text()
  const res = await tryFetch(event.request)
  if (res && !isUnreachable(res)) {
    noteStatus(res)
    return res
  }
  const userId = await currentUser()
  let body: ProgressBody | null = null
  try {
    body = JSON.parse(text) as ProgressBody
  } catch {
    body = null
  }
  if (userId === null || !body || typeof body.pageIndex !== 'number') return res ?? Response.error()

  const seriesId = typeof body.seriesId === 'number' ? body.seriesId : await seriesOf(userId, chapterId, url.origin)
  try {
    await insertCapturedProgress({
      userId,
      chapterId,
      seriesId,
      pageIndex: body.pageIndex,
      completed: body.completed === true,
      seconds: typeof body.seconds === 'number' && body.seconds > 0 ? body.seconds : 0,
      clientTime: typeof body.clientTime === 'string' && body.clientTime ? body.clientTime : new Date().toISOString(),
      opId: typeof body.opId === 'string' && body.opId ? body.opId : randomUUID(),
    })
  } catch {
    return res ?? Response.error()
  }
  broadcast({ type: 'queued', chapterId })
  return new Response(null, { status: 202, headers: { [QUEUED_HEADER]: '1' } })
}
