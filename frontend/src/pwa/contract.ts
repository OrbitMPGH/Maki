/**
 * What the service worker and the page agree on: cache names, headers, message shapes and the IDB
 * records. Imported by both sides, so nothing here may touch `window` or `document`.
 */

export const SW_URL = '/sw.js'
export const CHANNEL_NAME = 'maki-pwa'

/** Added by the SW to anything it answered from a cache in place of the network. */
export const OFFLINE_HEADER = 'X-Maki-Offline'
/** Added by the SW to a progress write it could not send and queued instead. */
export const QUEUED_HEADER = 'X-Maki-Queued'
/** The `code` of the SW's synthetic 503. The page branches on this, never on the status. */
export const OFFLINE_CODE = 'offline.unavailable'

/** The server clamps the reading time of one progress report to this many seconds. */
export const MAX_REPORT_SECONDS = 900

export const SHELL_CACHE_PREFIX = 'maki-shell-'
export const cacheNames = {
  shell: (buildId: string) => `${SHELL_CACHE_PREFIX}${buildId}`,
  assets: 'maki-assets',
  meta: 'maki-meta',
  data: (userId: number) => `maki-u${userId}-data`,
  covers: (userId: number) => `maki-u${userId}-covers`,
  chapters: (userId: number) => `maki-u${userId}-chapters`,
} as const

export type PageToWorker =
  | { type: 'user'; userId: number | null }
  | { type: 'purge-browse'; userId: number }
  | { type: 'purge-user'; userId: number }

export type WorkerToPage = { type: 'queued'; chapterId: number }

export type OfflineChapterState = 'saving' | 'saved' | 'stale' | 'error'

export interface OfflineChapter {
  userId: number
  chapterId: number
  seriesId: number
  pageVersion: string
  pageCount: number
  bytes: number
  /** ISO timestamp. */
  savedAt: string
  reason: 'manual' | 'auto'
  state: OfflineChapterState
}

export type ProgressRowState = 'pending' | 'sending'

export interface ProgressRow {
  /** Assigned by IndexedDB on insert. */
  id?: number
  userId: number
  chapterId: number
  seriesId: number
  pageIndex: number
  completed: boolean
  seconds: number
  /** ISO timestamp of the latest write folded into this row. */
  clientTime: string
  opId: string
  /** Page-side offline writes merge into the newest mergeable pending row; SW captures never do. */
  mergeable: boolean
  state: ProgressRowState
}

/** A row as read back from the queue, id always present. */
export type QueuedProgress = ProgressRow & { id: number }

/** One progress write, before it becomes a queue row. */
export interface ProgressWrite {
  userId: number
  chapterId: number
  seriesId: number
  pageIndex: number
  completed: boolean
  seconds: number
  clientTime: string
}
