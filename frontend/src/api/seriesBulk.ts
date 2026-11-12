import { api, ApiError } from './client'
import { onHubEvent } from './signalr'

/** A Library bulk action the server runs as one queued operation. */
export interface SeriesBulkRequest {
  /** e.g. `/series/refresh/bulk`. */
  path: string
  /** Sent alongside `seriesIds`, such as a move's `rootFolderId`. */
  body?: Record<string, unknown>
}

export interface SeriesBulkFailure {
  seriesId: number
  title: string | null
  error: string
}

export interface SeriesBulkStatus {
  operationId: string
  action: string
  state: 'queued' | 'running' | 'finished' | 'cancelled'
  total: number
  done: number
  succeeded: number
  failed: number
  failures: SeriesBulkFailure[]
}

/** A queued operation, or for a quick write (monitor mode) the count it already updated. */
interface QueuedResponse {
  operationId?: string | null
  total?: number
  updated?: number
  skipped: number[]
}

export interface SeriesBulkResult {
  succeeded: number
  failed: number
  /** Server-worded, in the caller's language. */
  firstError: string | null
  /** Ids the server would not queue: gone, or outside the caller's root folders. */
  skipped: number
  stopped: boolean
}

/** Matches `SeriesBulkOperations.MaxSeries` on the server. */
const CHUNK = 5000
/** The hub pushes progress; this poll only covers a missed push or a reconnect. */
const POLL_MS = 5000

function finished(status: SeriesBulkStatus) {
  return status.state === 'finished' || status.state === 'cancelled'
}

function waitFor(id: string, onStatus: (s: SeriesBulkStatus) => void, stop: AbortSignal): Promise<SeriesBulkStatus | null> {
  return new Promise((resolve) => {
    let settled = false
    let last: SeriesBulkStatus | null = null
    const settle = (status: SeriesBulkStatus | null) => {
      if (settled) return
      settled = true
      off()
      clearInterval(timer)
      stop.removeEventListener('abort', cancel)
      resolve(status)
    }
    const handle = (status: SeriesBulkStatus) => {
      if (settled || status.operationId !== id) return
      last = status
      onStatus(status)
      if (finished(status)) settle(status)
    }
    const poll = async () => {
      try {
        handle(await api<SeriesBulkStatus>(`/series/bulk/${id}`))
      } catch (err) {
        // Gone after a server restart: nothing more will arrive, so stop waiting with what we have.
        if (err instanceof ApiError && err.status === 404) settle(last)
      }
    }
    const cancel = () => {
      void api(`/series/bulk/${id}/cancel`, { method: 'POST' }).catch(() => {})
    }
    const off = onHubEvent<SeriesBulkStatus>('seriesBulkProgress', handle)
    const timer = setInterval(() => void poll(), POLL_MS)
    stop.addEventListener('abort', cancel)
    if (stop.aborted) cancel()
    void poll()
  })
}

/**
 * Queues a bulk action for `ids` and waits for it to finish, reporting progress as it goes. A
 * selection over the server's per-request limit is sent in chunks, one after the other.
 */
export async function runSeriesBulk(
  request: SeriesBulkRequest,
  ids: number[],
  onProgress: (done: number, result: SeriesBulkResult) => void,
  stop: AbortSignal,
): Promise<SeriesBulkResult> {
  const result: SeriesBulkResult = { succeeded: 0, failed: 0, firstError: null, skipped: 0, stopped: false }
  let doneBefore = 0
  for (let start = 0; start < ids.length; start += CHUNK) {
    if (stop.aborted) {
      result.stopped = true
      break
    }
    const chunk = ids.slice(start, start + CHUNK)
    const queued = await api<QueuedResponse>(request.path, {
      method: 'POST',
      body: JSON.stringify({ ...request.body, seriesIds: chunk }),
    })
    result.skipped += queued.skipped.length
    doneBefore += queued.skipped.length
    const { operationId } = queued
    if (!operationId) {
      result.succeeded += queued.updated ?? 0
      doneBefore += queued.updated ?? 0
      onProgress(doneBefore, result)
      continue
    }
    const succeededBefore = result.succeeded
    const failedBefore = result.failed
    const final = await waitFor(
      operationId,
      (status) => {
        result.succeeded = succeededBefore + status.succeeded
        result.failed = failedBefore + status.failed
        result.firstError ??= status.failures[0]?.error ?? null
        onProgress(doneBefore + status.done, result)
      },
      stop,
    )
    doneBefore += queued.total ?? 0
    if (final?.state === 'cancelled') {
      result.stopped = true
      break
    }
  }
  return result
}
