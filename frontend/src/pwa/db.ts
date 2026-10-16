import { openDB, type DBSchema, type IDBPDatabase } from 'idb'
import { randomUUID } from '../lib/uuid'
import {
  MAX_REPORT_SECONDS,
  type OfflineChapter,
  type ProgressRow,
  type ProgressRowState,
  type ProgressWrite,
  type QueuedProgress,
} from './contract'

/**
 * The `maki-pwa` database, shared by the service worker and the page. Every record that belongs to
 * somebody carries their user id: one origin's storage is every account's storage on this device.
 */

interface PrefRecord {
  userId: number
  name: string
  value: unknown
}

interface PwaSchema extends DBSchema {
  session: { key: string; value: { userId: number | null } }
  offlineChapters: {
    key: [number, number]
    value: OfflineChapter
    indexes: { bySeries: [number, number] }
  }
  progressQueue: {
    key: number
    value: ProgressRow
    indexes: { byChapter: [number, number] }
  }
  prefs: { key: [number, string]; value: PrefRecord }
}

const DB_NAME = 'maki-pwa'
const DB_VERSION = 1
const SESSION_KEY = 'current'

let opening: Promise<IDBPDatabase<PwaSchema>> | null = null

function db(): Promise<IDBPDatabase<PwaSchema>> {
  if (opening) return opening
  const pending = openDB<PwaSchema>(DB_NAME, DB_VERSION, {
    upgrade(database, oldVersion) {
      if (oldVersion < 1) {
        database.createObjectStore('session')
        database
          .createObjectStore('offlineChapters', { keyPath: ['userId', 'chapterId'] })
          .createIndex('bySeries', ['userId', 'seriesId'])
        database
          .createObjectStore('progressQueue', { keyPath: 'id', autoIncrement: true })
          .createIndex('byChapter', ['userId', 'chapterId'])
        database.createObjectStore('prefs', { keyPath: ['userId', 'name'] })
      }
    },
    // Another context holds an older version open: close so its upgrade can go ahead, and reopen
    // on the next call.
    blocking() {
      void pending.then((d) => d.close())
      if (opening === pending) opening = null
    },
    terminated() {
      if (opening === pending) opening = null
    },
  })
  opening = pending
  pending.catch(() => {
    if (opening === pending) opening = null
  })
  return pending
}

/** Every `[userId, x]` key, whatever x is: arrays sort above numbers and strings. */
function userRange(userId: number): IDBKeyRange {
  return IDBKeyRange.bound([userId], [userId, []])
}

// Session

export async function readSessionUser(): Promise<number | null> {
  const row = await (await db()).get('session', SESSION_KEY)
  return row?.userId ?? null
}

export async function writeSessionUser(userId: number | null): Promise<void> {
  await (await db()).put('session', { userId }, SESSION_KEY)
}

// Progress queue

function byId(a: QueuedProgress, b: QueuedProgress): number {
  return a.id - b.id
}

/**
 * Splits seconds into rows no larger than the server's per-report clamp. Every row but the last is
 * closed; the last stays open for merging.
 */
function freshRows(write: ProgressWrite, seconds: number): ProgressRow[] {
  const rows: ProgressRow[] = []
  let left = Math.max(0, seconds)
  do {
    const take = Math.min(left, MAX_REPORT_SECONDS)
    left -= take
    rows.push({ ...write, seconds: take, opId: randomUUID(), mergeable: left === 0, state: 'pending', attempts: 0 })
  } while (left > 0)
  return rows
}

/**
 * A live write the SW captured on its way out, as one row. Never merged into or with anything: it
 * keeps the write's own op id, because the write may have reached the server before the connection
 * dropped. Its seconds stay whole; the server clamps them.
 */
export async function insertCapturedProgress(write: ProgressWrite & { opId: string }): Promise<void> {
  await (await db()).add('progressQueue', {
    ...write,
    seconds: Math.max(0, write.seconds),
    mergeable: false,
    state: 'pending',
    attempts: 0,
  })
}

/**
 * A write the page made while offline, folded into the chapter's newest open row in one
 * transaction: latest position, completion ORed, seconds summed, a fresh op id. A sum that would
 * pass the server's clamp closes the open row as it is and starts a new one.
 */
export async function mergeProgress(write: ProgressWrite): Promise<void> {
  const tx = (await db()).transaction('progressQueue', 'readwrite')
  const rows = (await tx.store
    .index('byChapter')
    .getAll(IDBKeyRange.only([write.userId, write.chapterId]))) as QueuedProgress[]
  const open = rows
    .filter((r) => r.mergeable && r.state === 'pending')
    .sort(byId)
    .at(-1)
  const seconds = Math.max(0, write.seconds)
  const writes: Promise<unknown>[] = []

  if (open && open.seconds + seconds <= MAX_REPORT_SECONDS) {
    writes.push(tx.store.put({
      ...open,
      seriesId: write.seriesId || open.seriesId,
      pageIndex: write.pageIndex,
      completed: open.completed || write.completed,
      seconds: open.seconds + seconds,
      clientTime: write.clientTime,
      opId: randomUUID(),
    }))
  } else {
    if (open) writes.push(tx.store.put({ ...open, mergeable: false }))
    for (const row of freshRows(write, seconds)) writes.push(tx.store.add(row))
  }
  await Promise.all([...writes, tx.done])
}

/** The user's queue, oldest first. */
export async function listProgress(userId: number): Promise<QueuedProgress[]> {
  const rows = (await (await db()).getAllFromIndex('progressQueue', 'byChapter', userRange(userId))) as QueuedProgress[]
  return rows.sort(byId)
}

/** The user's queued rows for one chapter, oldest first. */
export async function progressForChapter(userId: number, chapterId: number): Promise<QueuedProgress[]> {
  const rows = (await (await db()).getAllFromIndex(
    'progressQueue',
    'byChapter',
    IDBKeyRange.only([userId, chapterId]),
  )) as QueuedProgress[]
  return rows.sort(byId)
}

/**
 * Moves a row between pending and sending, and records refused sends when `attempts` is given.
 * Leaves its op id alone. False when the row is gone. A row once handed to the network is closed
 * for merging: a send that failed by timing out may still have landed, and only its unchanged op
 * id lets the server recognise the retry.
 */
export async function setProgressState(id: number, state: ProgressRowState, attempts?: number): Promise<boolean> {
  const tx = (await db()).transaction('progressQueue', 'readwrite')
  const row = await tx.store.get(id)
  const mergeable = row ? row.mergeable && state !== 'sending' : false
  await Promise.all([row && tx.store.put({ ...row, state, mergeable, attempts: attempts ?? row.attempts }), tx.done])
  return row !== undefined
}

export async function deleteProgress(id: number): Promise<void> {
  await (await db()).delete('progressQueue', id)
}

/** Puts every `sending` row back to `pending`, for rows a closed tab left mid-send. */
export async function resetSendingProgress(): Promise<void> {
  const tx = (await db()).transaction('progressQueue', 'readwrite')
  let cursor = await tx.store.openCursor()
  while (cursor) {
    if (cursor.value.state === 'sending') await cursor.update({ ...cursor.value, state: 'pending' })
    cursor = await cursor.continue()
  }
  await tx.done
}

// Offline chapters

export async function getOfflineChapter(userId: number, chapterId: number): Promise<OfflineChapter | undefined> {
  return (await db()).get('offlineChapters', [userId, chapterId])
}

export async function putOfflineChapter(record: OfflineChapter): Promise<void> {
  await (await db()).put('offlineChapters', record)
}

/** Patches a record in one transaction. Returns the result, or undefined when there was none. */
export async function updateOfflineChapter(
  userId: number,
  chapterId: number,
  patch: Partial<Omit<OfflineChapter, 'userId' | 'chapterId'>>,
): Promise<OfflineChapter | undefined> {
  const tx = (await db()).transaction('offlineChapters', 'readwrite')
  const row = await tx.store.get([userId, chapterId])
  const next = row ? { ...row, ...patch } : undefined
  await Promise.all([next && tx.store.put(next), tx.done])
  return next
}

export async function deleteOfflineChapter(userId: number, chapterId: number): Promise<void> {
  await (await db()).delete('offlineChapters', [userId, chapterId])
}

export async function listOfflineChapters(userId: number): Promise<OfflineChapter[]> {
  return (await db()).getAll('offlineChapters', userRange(userId))
}

export async function listOfflineChaptersForSeries(userId: number, seriesId: number): Promise<OfflineChapter[]> {
  return (await db()).getAllFromIndex('offlineChapters', 'bySeries', IDBKeyRange.only([userId, seriesId]))
}

// Prefs

export async function getPref<T>(userId: number, name: string): Promise<T | undefined> {
  const row = await (await db()).get('prefs', [userId, name])
  return row?.value as T | undefined
}

export async function setPref(userId: number, name: string, value: unknown): Promise<void> {
  await (await db()).put('prefs', { userId, name, value })
}

export async function deletePref(userId: number, name: string): Promise<void> {
  await (await db()).delete('prefs', [userId, name])
}

/** Removes every record of one user: saved chapter records, queued progress and prefs. */
export async function deleteUserRecords(userId: number): Promise<void> {
  const tx = (await db()).transaction(['offlineChapters', 'progressQueue', 'prefs'], 'readwrite')
  const range = userRange(userId)
  await tx.objectStore('offlineChapters').delete(range)
  await tx.objectStore('prefs').delete(range)
  let cursor = await tx.objectStore('progressQueue').index('byChapter').openCursor(range)
  while (cursor) {
    await cursor.delete()
    cursor = await cursor.continue()
  }
  await tx.done
}
