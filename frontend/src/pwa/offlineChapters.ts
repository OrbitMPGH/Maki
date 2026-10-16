import { t } from '@lingui/core/macro'
import { notifications } from '@mantine/notifications'
import { useMemo, useSyncExternalStore } from 'react'
import { api, ApiError, apiWithOrigin, getInitialize, isOfflineUnavailable, trackedFetch, UnauthorizedError } from '../api/client'
import type { HomeReadingResponse } from '../api/hooks'
import { pageUrl, type ChapterProgressDto, type ReaderManifest } from '../api/reader'
import type { ChapterDto } from '../api/types'
import { formatBytes } from '../format'
import { getConnectivity } from '../lib/connectivity'
import { cacheDeleteWhere, cacheMatch, cachePut, deleteCache } from '../lib/pwa/cache'
import { pwaSupported } from '../lib/pwa/serviceWorker'
import { estimate, requestPersistence } from '../lib/pwa/storage'
import { cacheNames, OFFLINE_CODE, OFFLINE_HEADER, type OfflineChapter } from './contract'
import {
  deleteOfflineChapter,
  deleteUserRecords,
  getOfflineChapter,
  getPref,
  listOfflineChapters,
  progressForChapter,
  putOfflineChapter,
  setPref,
  updateOfflineChapter,
} from './db'
import { onProgressDrained } from './progressQueue'

/**
 * Saves chapters to this device for reading without a connection. Runs in the page rather than the
 * service worker, which can be stopped mid-download; a closed tab leaves records in `saving`, and
 * the next start picks them up again. One chapter at a time, three pages at once.
 */

const PAGE_CONCURRENCY = 3
const REVALIDATE_MS = 3_600_000
const VERSIONS_BATCH = 500
/** How many in-progress series "Keep upcoming chapters" looks at. */
const AUTO_SERIES = 10
const MAINTAIN_DELAY_MS = 1500

const PREF_AUTO_KEEP = 'autoKeep'
const PREF_LAST_CHECK = 'lastRevalidation'

export const AUTO_KEEP_CHOICES = [0, 1, 3, 5, 10] as const

export interface SaveProgress {
  done: number
  total: number
}

export interface OfflineSnapshot {
  userId: number | null
  records: ReadonlyMap<number, OfflineChapter>
  /** Pages stored so far, for chapters being saved right now. */
  progress: ReadonlyMap<number, SaveProgress>
}

let snapshot: OfflineSnapshot = { userId: null, records: new Map(), progress: new Map() }
const listeners = new Set<() => void>()

function publish(patch: Partial<OfflineSnapshot>): void {
  snapshot = { ...snapshot, ...patch }
  for (const listener of listeners) listener()
}

function showRecord(record: OfflineChapter): void {
  if (record.userId !== snapshot.userId) return
  const records = new Map(snapshot.records)
  records.set(record.chapterId, record)
  publish({ records })
}

function hideRecords(chapterIds: Iterable<number>): void {
  const records = new Map(snapshot.records)
  const progress = new Map(snapshot.progress)
  for (const id of chapterIds) {
    records.delete(id)
    progress.delete(id)
  }
  publish({ records, progress })
}

function showProgress(chapterId: number, value: SaveProgress | null): void {
  const progress = new Map(snapshot.progress)
  if (value) progress.set(chapterId, value)
  else progress.delete(chapterId)
  publish({ progress })
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener)
  return () => {
    listeners.delete(listener)
  }
}

function getSnapshot(): OfflineSnapshot {
  return snapshot
}

/** What this device holds for the signed-in user. Empty on plain HTTP. */
export function useOfflineChapters(): OfflineSnapshot {
  return useSyncExternalStore(subscribe, getSnapshot, getSnapshot)
}

/** Series with at least one chapter saved on this device. */
export function useOfflineSeriesIds(): ReadonlySet<number> {
  const { records } = useOfflineChapters()
  return useMemo(() => {
    const ids = new Set<number>()
    for (const record of records.values()) if (record.state !== 'error') ids.add(record.seriesId)
    return ids
  }, [records])
}

let loadingFor: number | null = null
/** Settles once the store holds the current user's records; everything that acts waits for it. */
let loaded: Promise<void> = Promise.resolve()

/** Reads the signed-in user's records into the store. Works offline: it only touches IndexedDB. */
export function loadOfflineChapters(userId: number | null): Promise<void> {
  if (userId === loadingFor) return loaded
  loadingFor = userId
  loaded = load(userId)
  return loaded
}

async function load(userId: number | null): Promise<void> {
  if (userId !== snapshot.userId) {
    cancelAll()
    warmedSeries.clear()
  }
  if (userId === null || !pwaSupported()) {
    publish({ userId, records: new Map(), progress: new Map() })
    return
  }
  try {
    const list = await listOfflineChapters(userId)
    if (loadingFor !== userId) return
    publish({ userId, records: new Map(list.map((r) => [r.chapterId, r])), progress: new Map() })
  } catch {
    if (loadingFor === userId) publish({ userId, records: new Map(), progress: new Map() })
  }
}

// Jobs

interface Job {
  userId: number
  chapterId: number
  seriesId: number
  reason: OfflineChapter['reason']
  cancelled: boolean
}

type Outcome = 'done' | 'offline' | 'quota' | 'failed' | 'cancelled'

const jobs: Job[] = []
let current: Job | null = null
let working = false
let persistenceAsked = false
/** Series whose pages for browsing were already fetched by this tab, so a batch warms them once. */
const warmedSeries = new Set<number>()

function cancelAll(): void {
  for (const job of jobs) job.cancelled = true
  jobs.length = 0
  if (current) current.cancelled = true
}

function cancelJobs(chapterIds: ReadonlySet<number>): void {
  for (const job of jobs) if (chapterIds.has(job.chapterId)) job.cancelled = true
  if (current && chapterIds.has(current.chapterId)) current.cancelled = true
}

function enqueue(job: Omit<Job, 'cancelled'>): void {
  const live = (j: Job) => !j.cancelled && j.userId === job.userId && j.chapterId === job.chapterId
  if ((current && live(current)) || jobs.some(live)) return
  jobs.push({ ...job, cancelled: false })
  void work()
}

async function work(): Promise<void> {
  if (working) return
  working = true
  try {
    while (jobs.length > 0) {
      const job = jobs.shift()!
      if (job.cancelled || job.userId !== snapshot.userId) continue
      current = job
      const outcome = await save(job)
      current = null
      if (outcome === 'offline') {
        // The records stay as they are and the next start, or the connection coming back, resumes them.
        jobs.length = 0
      } else if (outcome === 'quota') {
        const left = jobs.splice(0)
        for (const rest of left) if (!rest.cancelled) await markFailed(rest)
      }
    }
  } finally {
    current = null
    working = false
  }
}

function chapterPath(pathname: string): number | null {
  const match = /\/reader\/chapter\/(\d+)(?:\/page\/\d+)?$/.exec(pathname)
  return match ? Number(match[1]) : null
}

function pathOf(url: string): URL | null {
  try {
    return new URL(url, window.location.origin)
  } catch {
    return null
  }
}

/** The manifests and pages of these chapters. */
async function deletePages(userId: number, chapterIds: ReadonlySet<number>): Promise<void> {
  await cacheDeleteWhere(cacheNames.chapters(userId), (url) => {
    const parsed = pathOf(url)
    const id = parsed ? chapterPath(parsed.pathname) : null
    return id !== null && chapterIds.has(id)
  })
}

/** Pages of an older copy of the chapter's file, once the current one is stored. */
async function deleteOtherVersions(userId: number, chapterId: number, version: string): Promise<void> {
  await cacheDeleteWhere(cacheNames.chapters(userId), (url) => {
    const parsed = pathOf(url)
    if (!parsed || !parsed.pathname.includes('/page/') || chapterPath(parsed.pathname) !== chapterId) return false
    return parsed.searchParams.get('v') !== version
  })
}

function isQuotaError(error: unknown): boolean {
  return error instanceof DOMException && (error.name === 'QuotaExceededError' || error.code === 22)
}

/** The connection went, or the session did: stop and resume later rather than mark anything failed. */
function isInterrupted(error: unknown): boolean {
  return (
    error instanceof TypeError ||
    error instanceof UnauthorizedError ||
    isOfflineUnavailable(error) ||
    getConnectivity() === 'offline'
  )
}

/** Stores one page and returns its size. A page already stored is only measured. */
async function savePage(cacheName: string, url: string): Promise<number> {
  const stored = await cacheMatch(cacheName, url)
  if (stored) return (await stored.blob()).size
  const res = await trackedFetch(url, { credentials: 'same-origin' })
  if (!res.ok) throw new ApiError(res.status, '', res.headers.has(OFFLINE_HEADER) ? OFFLINE_CODE : null)
  const size = (await res.clone().blob()).size
  await cachePut(cacheName, url, res)
  return size
}

/** The series page and cover, so the chapter can be reached offline and not only opened. */
async function warm(manifest: ReaderManifest): Promise<void> {
  const { seriesId, chapterId, seriesCoverUrl } = manifest
  const requests: Promise<unknown>[] = [api(`/reader/chapter/${chapterId}/bookmarks`)]
  if (!warmedSeries.has(seriesId)) {
    warmedSeries.add(seriesId)
    requests.push(
      api(`/series/${seriesId}`),
      api(`/chapter?seriesId=${seriesId}`),
      api(`/reader/series/${seriesId}/progress`),
    )
    if (seriesCoverUrl?.startsWith('/')) requests.push(fetch(seriesCoverUrl, { credentials: 'same-origin' }))
  }
  await Promise.allSettled(requests)
}

async function markFailed(job: Job): Promise<void> {
  const record = await updateOfflineChapter(job.userId, job.chapterId, { state: 'error' })
  if (record) showRecord(record)
}

async function quotaToast(): Promise<void> {
  const usage = await estimate()
  const used = usage ? formatBytes(usage.usage) : null
  const quota = usage ? formatBytes(usage.quota) : null
  notifications.show({
    color: 'var(--danger)',
    title: t`Not enough space on this device`,
    message:
      used && quota
        ? t`Maki is using ${used} of ${quota} here. Remove some saved chapters and try again.`
        : t`Remove some saved chapters and try again.`,
  })
}

async function save(job: Job): Promise<Outcome> {
  const { userId, chapterId } = job
  const cacheName = cacheNames.chapters(userId)
  try {
    if (!persistenceAsked) {
      persistenceAsked = true
      await requestPersistence()
    }
    // Live only: a manifest answered from the device would store whatever version it last saw.
    const { data: manifest, servedOffline } = await apiWithOrigin<ReaderManifest>(`/reader/chapter/${chapterId}`)
    if (servedOffline) return 'offline'
    if (job.cancelled) return 'cancelled'

    const init = await getInitialize()
    await cachePut(
      cacheName,
      `${init.apiRoot}/reader/chapter/${chapterId}`,
      new Response(JSON.stringify(manifest), { headers: { 'Content-Type': 'application/json' } }),
    )
    const record = await updateOfflineChapter(userId, chapterId, {
      seriesId: manifest.seriesId,
      pageVersion: manifest.pageVersion,
      pageCount: manifest.pageCount,
    })
    if (!record || job.cancelled) return 'cancelled'
    showRecord(record)

    const urls = await Promise.all(
      Array.from({ length: manifest.pageCount }, (_, i) => pageUrl(chapterId, i, false, manifest.pageVersion)),
    )
    let next = 0
    let done = 0
    let bytes = 0
    let failure: unknown = null
    showProgress(chapterId, { done, total: urls.length })
    const worker = async () => {
      while (next < urls.length && failure === null && !job.cancelled) {
        const url = urls[next++]
        try {
          bytes += await savePage(cacheName, url)
        } catch (err) {
          failure ??= err
          return
        }
        done++
        showProgress(chapterId, { done, total: urls.length })
      }
    }
    await Promise.all(Array.from({ length: PAGE_CONCURRENCY }, worker))
    if (failure !== null) throw failure
    if (job.cancelled) return 'cancelled'

    await warm(manifest)
    await deleteOtherVersions(userId, chapterId, manifest.pageVersion)
    const saved = await updateOfflineChapter(userId, chapterId, {
      state: 'saved',
      bytes,
      savedAt: new Date().toISOString(),
    })
    if (!saved) return 'cancelled'
    showRecord(saved)
    return 'done'
  } catch (err) {
    if (job.cancelled) return 'cancelled'
    if (isQuotaError(err)) {
      await markFailed(job)
      await quotaToast()
      return 'quota'
    }
    if (isInterrupted(err)) return 'offline'
    // Gone from the server, or out of reach for this account: an automatic copy just goes.
    if (job.reason === 'auto' && err instanceof ApiError && err.status === 404) {
      await removeFor(userId, [chapterId])
      return 'failed'
    }
    await markFailed(job)
    return 'failed'
  } finally {
    showProgress(chapterId, null)
    // A removal that landed mid-download deleted the pages already stored, but not the ones after.
    if (job.cancelled && !(await getOfflineChapter(userId, chapterId).catch(() => undefined))) {
      await deletePages(userId, new Set([chapterId])).catch(() => {})
    }
  }
}

async function saveFor(
  userId: number,
  chapters: { chapterId: number; seriesId: number }[],
  reason: OfflineChapter['reason'],
): Promise<void> {
  for (const { chapterId, seriesId } of chapters) {
    const existing = await getOfflineChapter(userId, chapterId)
    if (existing) {
      // Asking for a chapter by hand keeps it from being cleaned up automatically later.
      const patch: Partial<OfflineChapter> = {}
      if (reason === 'manual' && existing.reason === 'auto') patch.reason = 'manual'
      if (existing.state === 'error') patch.state = 'saving'
      const record = Object.keys(patch).length
        ? await updateOfflineChapter(userId, chapterId, patch)
        : existing
      if (record) showRecord(record)
      if (record && record.state !== 'saved') enqueue({ userId, chapterId, seriesId, reason: record.reason })
      continue
    }
    const record: OfflineChapter = {
      userId,
      chapterId,
      seriesId,
      pageVersion: '',
      pageCount: 0,
      bytes: 0,
      savedAt: new Date().toISOString(),
      reason,
      state: 'saving',
    }
    await putOfflineChapter(record)
    showRecord(record)
    enqueue({ userId, chapterId, seriesId, reason })
  }
}

async function removeFor(userId: number, chapterIds: number[]): Promise<void> {
  if (chapterIds.length === 0) return
  const ids = new Set(chapterIds)
  cancelJobs(ids)
  for (const id of ids) await deleteOfflineChapter(userId, id)
  if (userId === snapshot.userId) hideRecords(ids)
  await deletePages(userId, ids)
}

async function signedIn(): Promise<number | null> {
  if (!pwaSupported()) return null
  await loaded
  return snapshot.userId
}

// Actions for the UI

export async function saveChaptersOffline(chapters: { chapterId: number; seriesId: number }[]): Promise<void> {
  const userId = await signedIn()
  if (userId !== null) await saveFor(userId, chapters, 'manual')
}

export async function removeChaptersOffline(chapterIds: number[]): Promise<void> {
  const userId = await signedIn()
  if (userId !== null) await removeFor(userId, chapterIds)
}

export async function removeSeriesOffline(seriesId: number): Promise<void> {
  const ids = [...snapshot.records.values()].filter((r) => r.seriesId === seriesId).map((r) => r.chapterId)
  await removeChaptersOffline(ids)
}

function readChapterIds(progress: ChapterProgressDto[]): Set<number> {
  return new Set(progress.filter((p) => p.completed && p.unreadAt === null).map((p) => p.chapterId))
}

/** Saves every downloaded, unread chapter of a series. Returns how many were not saved yet. */
export async function saveUnreadOffline(seriesId: number): Promise<number> {
  const userId = await signedIn()
  if (userId === null) return 0
  const [chapters, progress] = await Promise.all([
    api<ChapterDto[]>(`/chapter?seriesId=${seriesId}`),
    api<ChapterProgressDto[]>(`/reader/series/${seriesId}/progress`),
  ])
  const read = readChapterIds(progress)
  const wanted = chapters.filter((c) => c.hasFile && !read.has(c.id) && !snapshot.records.has(c.id))
  await saveFor(userId, wanted.map((c) => ({ chapterId: c.id, seriesId })), 'manual')
  return wanted.length
}

/**
 * Everything this user keeps on the device: saved chapters, the browsing copies, queued progress
 * and per-device settings. The caller warns about unsent progress first.
 */
export async function removeEverythingOffline(): Promise<void> {
  const userId = await signedIn()
  if (userId === null) return
  cancelAll()
  await Promise.all([
    deleteCache(cacheNames.chapters(userId)),
    deleteCache(cacheNames.data(userId)),
    deleteCache(cacheNames.covers(userId)),
  ])
  await deleteUserRecords(userId)
  warmedSeries.clear()
  if (userId === snapshot.userId) publish({ records: new Map(), progress: new Map() })
}

export async function getAutoKeep(userId: number): Promise<number> {
  return (await getPref<number>(userId, PREF_AUTO_KEEP).catch(() => undefined)) ?? 0
}

export async function setAutoKeep(count: number): Promise<void> {
  const userId = await signedIn()
  if (userId === null) return
  await setPref(userId, PREF_AUTO_KEEP, count)
  requestOfflineMaintenance()
}

// Keeping the device current

/** Picks up chapters a closed tab or a lost connection left half saved. */
export async function resumeOfflineSaves(): Promise<void> {
  const userId = await signedIn()
  if (userId === null) return
  try {
    for (const record of await listOfflineChapters(userId)) {
      if (record.state === 'saving' || record.state === 'stale') {
        enqueue({ userId, chapterId: record.chapterId, seriesId: record.seriesId, reason: record.reason })
      }
    }
  } catch {
    // IndexedDB unavailable; nothing to resume.
  }
}

/**
 * At most hourly: asks which saved chapters this user can still read, and at what version. A
 * chapter left out is gone (deleted, no file, access revoked) and goes from the device too; a
 * changed version is downloaded again.
 */
async function revalidate(userId: number): Promise<void> {
  const last = await getPref<number>(userId, PREF_LAST_CHECK)
  if (last !== undefined && Date.now() - last < REVALIDATE_MS) return
  const records = await listOfflineChapters(userId)
  const gone: number[] = []
  const stale: OfflineChapter[] = []
  for (let i = 0; i < records.length; i += VERSIONS_BATCH) {
    const batch = records.slice(i, i + VERSIONS_BATCH)
    const { data, servedOffline } = await apiWithOrigin<{ id: number; pageVersion: string }[]>(
      '/reader/chapters/versions',
      { method: 'POST', body: JSON.stringify({ ids: batch.map((r) => r.chapterId) }) },
    )
    if (servedOffline) return
    const versions = new Map(data.map((v) => [v.id, v.pageVersion]))
    for (const record of batch) {
      const version = versions.get(record.chapterId)
      if (version === undefined) gone.push(record.chapterId)
      else if (record.state === 'saved' && version !== record.pageVersion) stale.push(record)
    }
  }
  if (userId !== snapshot.userId) return
  await removeFor(userId, gone)
  for (const record of stale) {
    const next = await updateOfflineChapter(userId, record.chapterId, { state: 'stale' })
    if (!next) continue
    showRecord(next)
    enqueue({ userId, chapterId: next.chapterId, seriesId: next.seriesId, reason: next.reason })
  }
  await setPref(userId, PREF_LAST_CHECK, Date.now())
}

/**
 * "Keep upcoming chapters": the next N unread downloaded chapters of each series in progress (the
 * series Home's continue reading shows) are saved, and automatic copies already read, with nothing
 * left in the progress queue, are removed. Chapters saved by hand are never removed here.
 */
async function autoKeep(userId: number): Promise<void> {
  const keep = await getAutoKeep(userId)
  const progressCache = new Map<number, Promise<ChapterProgressDto[]>>()
  const progressOf = (seriesId: number) => {
    let pending = progressCache.get(seriesId)
    if (!pending) {
      pending = api<ChapterProgressDto[]>(`/reader/series/${seriesId}/progress`)
      progressCache.set(seriesId, pending)
    }
    return pending
  }

  if (keep > 0) {
    const { continueReading } = await api<HomeReadingResponse>(`/home/reading?limit=${AUTO_SERIES}`)
    const toSave: { chapterId: number; seriesId: number }[] = []
    for (const item of continueReading.slice(0, AUTO_SERIES)) {
      const [chapters, progress] = await Promise.all([
        api<ChapterDto[]>(`/chapter?seriesId=${item.seriesId}`),
        progressOf(item.seriesId),
      ])
      const read = readChapterIds(progress)
      const from = Math.max(0, chapters.findIndex((c) => c.id === item.chapterId))
      for (const c of chapters.slice(from).filter((c) => c.hasFile && !read.has(c.id)).slice(0, keep)) {
        toSave.push({ chapterId: c.id, seriesId: item.seriesId })
      }
    }
    if (userId !== snapshot.userId) return
    const have = new Set((await listOfflineChapters(userId)).map((r) => r.chapterId))
    await saveFor(userId, toSave.filter((c) => !have.has(c.chapterId)), 'auto')
  }

  const prune: number[] = []
  for (const record of await listOfflineChapters(userId)) {
    if (record.reason !== 'auto') continue
    const progress = await progressOf(record.seriesId).catch(() => null)
    if (!progress || !readChapterIds(progress).has(record.chapterId)) continue
    if ((await progressForChapter(userId, record.chapterId)).length > 0) continue
    prune.push(record.chapterId)
  }
  if (userId === snapshot.userId) await removeFor(userId, prune)
}

let maintaining = false
let maintainTimer: ReturnType<typeof setTimeout> | undefined

async function maintain(): Promise<void> {
  const userId = await signedIn()
  if (userId === null || maintaining || getConnectivity() === 'offline') return
  maintaining = true
  try {
    await revalidate(userId)
    await autoKeep(userId)
  } catch {
    // Offline again, or the server refused; the next start or drain tries again.
  } finally {
    maintaining = false
  }
}

/** Revalidation and "Keep upcoming chapters", on start and after the progress queue drains. */
export function requestOfflineMaintenance(): void {
  clearTimeout(maintainTimer)
  maintainTimer = setTimeout(() => void maintain(), MAINTAIN_DELAY_MS)
}

onProgressDrained(requestOfflineMaintenance)
