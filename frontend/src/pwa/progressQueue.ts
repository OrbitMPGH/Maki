import type { QueryClient } from '@tanstack/react-query'
import { ApiError, apiWithOrigin, UnauthorizedError } from '../api/client'
import { getConnectivity } from '../lib/connectivity'
import { pwaSupported } from '../lib/pwa/serviceWorker'
import { REPLAY_HEADER, type QueuedProgress } from './contract'
import {
  deleteProgress,
  listProgress,
  mergeProgress,
  progressForChapter,
  resetSendingProgress,
  setProgressState,
} from './db'

/**
 * Sends reading progress queued on this device while the server was out of reach. Runs in the
 * page, because only the page can read the antiforgery cookie. One replay at a time per tab, and
 * `navigator.locks` keeps two tabs from sending the same rows.
 */

const LOCK_NAME = 'maki-progress-replay'
const PAGE_OUT_OF_RANGE = 'error.reader.pageOutOfRange'
/** How long the reader waits for a chapter's queued rows to go out before it opens. */
const READER_WAIT_MS = 3000
const RETRY_BASE_MS = 5000
const RETRY_MAX_MS = 300_000
/** Refusals with a 4xx that might yet change before a row is given up on. */
const MAX_ATTEMPTS = 5

let userId: number | null = null
let queryClient: QueryClient | null = null
let running: Promise<void> | null = null
let failures = 0
let retryTimer: ReturnType<typeof setTimeout> | undefined
/** Op ids of rows the server refused for a reason that will not change, logged once each. */
const reported = new Set<string>()
const drainedListeners = new Set<() => void>()

/** Whose queue this tab writes to and replays. Set from the signed-in identity, online or not. */
export function setQueueUser(id: number | null, client: QueryClient): void {
  queryClient = client
  if (id === userId) return
  userId = id
  failures = 0
  clearTimeout(retryTimer)
}

/** Signed out, or the session ended: nothing more is written to or sent from anybody's queue. */
export function clearQueueUser(): void {
  userId = null
  failures = 0
  clearTimeout(retryTimer)
}

export function queueUser(): number | null {
  return userId
}

/** Called after a replay that changed anything on the server. */
export function onProgressDrained(listener: () => void): () => void {
  drainedListeners.add(listener)
  return () => {
    drainedListeners.delete(listener)
  }
}

/**
 * A write made while offline, folded into the chapter's open queue row. False when this device
 * keeps no queue (plain HTTP, or nobody known to be signed in), so the caller sends it instead.
 */
export async function queueOfflineWrite(
  chapterId: number,
  seriesId: number,
  pageIndex: number,
  completed: boolean,
  seconds: number,
  pageCount: number,
): Promise<boolean> {
  const user = userId
  if (user === null || !pwaSupported()) return false
  await mergeProgress({
    userId: user,
    chapterId,
    seriesId,
    pageIndex,
    // A later merge moves the position, so the completion the server would derive from it is kept here.
    completed: completed || (pageCount > 0 && pageIndex >= pageCount - 1),
    seconds,
    clientTime: new Date().toISOString(),
  })
  return true
}

function withLock<T>(task: () => Promise<T>): Promise<T> {
  const locks = typeof navigator === 'undefined' ? undefined : navigator.locks
  if (locks && typeof locks.request === 'function') return locks.request(LOCK_NAME, task)
  return task()
}

type Outcome = 'sent' | 'dropped' | 'kept' | 'stop'

async function sendRow(row: QueuedProgress): Promise<Outcome> {
  if (!(await setProgressState(row.id, 'sending'))) return 'dropped'
  // Read again: an offline write may have merged into the row since the list was taken.
  const current = (await progressForChapter(row.userId, row.chapterId)).find((r) => r.id === row.id)
  if (!current) return 'dropped'
  const body = {
    pageIndex: current.pageIndex,
    // Never false: the server only derives completion from the position when the field is absent.
    completed: current.completed || undefined,
    seconds: current.seconds,
    final: true,
    clientTime: current.clientTime,
    opId: current.opId,
  }
  try {
    const { servedOffline } = await apiWithOrigin(`/reader/chapter/${current.chapterId}/progress`, {
      method: 'PUT',
      headers: { [REPLAY_HEADER]: '1' },
      body: JSON.stringify(body),
    })
    if (servedOffline) {
      await setProgressState(current.id, 'pending')
      return 'stop'
    }
    await deleteProgress(current.id)
    return 'sent'
  } catch (err) {
    if (err instanceof UnauthorizedError) {
      await setProgressState(current.id, 'pending')
      return 'stop'
    }
    // The chapter or its file is gone, out of this account's reach, or the page count changed under
    // the queued position.
    if (
      err instanceof ApiError &&
      (err.status === 404 || err.status === 403 || err.status === 410 || (err.status === 400 && err.code === PAGE_OUT_OF_RANGE))
    ) {
      await deleteProgress(current.id)
      return 'dropped'
    }
    if (err instanceof ApiError && err.status >= 400 && err.status < 500 && err.status !== 408 && err.status !== 429) {
      // Kept for a few tries rather than dropped at once, so a binding bug cannot quietly eat the queue.
      const attempts = (current.attempts ?? 0) + 1
      if (attempts >= MAX_ATTEMPTS) {
        await deleteProgress(current.id)
        return 'dropped'
      }
      await setProgressState(current.id, 'pending', attempts)
      if (!reported.has(current.opId)) {
        reported.add(current.opId)
        console.error('The server refused a queued reading position', current, err)
      }
      return 'kept'
    }
    await setProgressState(current.id, 'pending')
    return 'stop'
  }
}

/** Who the server says this session belongs to, or null when it was not the server answering. */
async function liveUser(): Promise<number | null> {
  const { data, servedOffline } = await apiWithOrigin<{ id: number }>('/auth/me')
  return servedOffline ? null : data.id
}

async function drain(user: number): Promise<{ changed: boolean; stopped: boolean }> {
  // Under the lock nobody else is sending, so a row still marked sending was left by a closed tab.
  await resetSendingProgress()
  let changed = false
  const rows = await listProgress(user)
  if (rows.length === 0) return { changed, stopped: false }
  // The rows are sent under whatever session the browser holds now, which must be theirs.
  const live = await liveUser()
  if (live === null) return { changed, stopped: true }
  if (live !== user) return { changed, stopped: false }
  for (const row of rows) {
    if (user !== userId) return { changed, stopped: true }
    const outcome = await sendRow(row)
    if (outcome === 'sent' || outcome === 'dropped') changed = true
    if (outcome === 'stop') return { changed, stopped: true }
  }
  return { changed, stopped: false }
}

function scheduleRetry(): void {
  clearTimeout(retryTimer)
  failures++
  const delay = Math.min(RETRY_MAX_MS, RETRY_BASE_MS * 2 ** (failures - 1))
  retryTimer = setTimeout(() => void replayProgress(), delay)
}

function invalidateProgress(): void {
  if (!queryClient) return
  for (const key of ['series', 'home', 'reader-progress', 'reader-continue']) {
    void queryClient.invalidateQueries({ queryKey: [key] })
  }
}

/** Sends the signed-in user's queue, oldest first. Joins a replay already running. */
export function replayProgress(): Promise<void> {
  if (running) return running
  const user = userId
  if (user === null || !pwaSupported()) return Promise.resolve()
  clearTimeout(retryTimer)
  const run = withLock(() => drain(user))
    .then(({ changed, stopped }) => {
      if (stopped && userId === user) scheduleRetry()
      else failures = 0
      if (!changed) return
      invalidateProgress()
      for (const listener of drainedListeners) listener()
    })
    .catch(() => {
      if (userId === user) scheduleRetry()
    })
    .finally(() => {
      if (running === run) running = null
    })
  running = run
  return run
}

/**
 * For the reader opening a chapter: lets that chapter's queued rows go out first, waiting at most
 * a few seconds, and returns what is still queued. `sent` says the server's copy changed since the
 * caller last read it.
 */
export async function settleQueuedProgress(chapterId: number): Promise<{ rows: QueuedProgress[]; sent: boolean }> {
  const user = userId
  if (user === null || !pwaSupported()) return { rows: [], sent: false }
  try {
    const before = await progressForChapter(user, chapterId)
    if (before.length === 0) return { rows: [], sent: false }
    // A row the server already refused would only be refused again; it does not hold the reader up.
    if (getConnectivity() === 'online' && before.some((row) => !row.attempts)) {
      await Promise.race([replayProgress(), new Promise((resolve) => setTimeout(resolve, READER_WAIT_MS))])
    }
    const after = await progressForChapter(user, chapterId)
    return { rows: after, sent: before.some((row) => !after.some((left) => left.id === row.id)) }
  } catch {
    return { rows: [], sent: false }
  }
}

/** Rows still waiting to reach the server, for the warning before everything is removed. */
export async function queuedProgressCount(user: number): Promise<number> {
  try {
    return (await listProgress(user)).length
  } catch {
    return 0
  }
}
