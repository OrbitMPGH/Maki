import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import type { ReaderPrefs } from '../pages/reader/prefs'
import { api, getInitialize } from './client'

export interface ReaderManifest {
  chapterId: number
  seriesId: number
  seriesTitle: string
  label: string
  number: number | null
  volume: number | null
  language: string
  pageCount: number
  resumePage: number
  completed: boolean
  previousChapterId: number | null
  nextChapterId: number | null
  /** The series' override merged over the global defaults. */
  prefs: ReaderPrefs
  prefsOverridden: boolean
}

export interface ChapterProgressDto {
  chapterId: number
  pageIndex: number
  pageCount: number
  completed: boolean
  updatedAt: string
}

/**
 * Page images are loaded by plain `<img src>`, which cannot send the X-Api-Key header, so the
 * key rides in the query string — the same escape hatch the SignalR connection already uses and
 * which ApiKeyMiddleware accepts. Deliberately not an auth carve-out like cover art has.
 */
export async function pageUrl(chapterId: number, page: number, thumb = false): Promise<string> {
  const init = await getInitialize()
  const kind = thumb ? 'thumb' : 'page'
  return `${init.apiRoot}/reader/chapter/${chapterId}/${kind}/${page}?apikey=${encodeURIComponent(init.apiKey)}`
}

export function useReaderManifest(chapterId: number) {
  return useQuery({
    queryKey: ['reader-manifest', chapterId],
    queryFn: () => api<ReaderManifest>(`/reader/chapter/${chapterId}`),
    enabled: Number.isFinite(chapterId) && chapterId > 0,
    // The page list of a stored archive doesn't change while the reader is open.
    staleTime: Infinity,
  })
}

/**
 * Whether the built-in reader has ever been used. OR this with "Kavita is configured" to decide
 * whether read progress is meaningful — Kavita alone was the old gate and hides a reader-only
 * user's own progress.
 */
export function useReaderUsed() {
  return useQuery({
    queryKey: ['reader-used'],
    queryFn: () => api<{ used: boolean }>('/reader/used'),
    staleTime: 60_000,
  })
}

export function useSeriesReadProgress(seriesId: number, enabled = true) {
  return useQuery({
    queryKey: ['reader-progress', seriesId],
    queryFn: () => api<ChapterProgressDto[]>(`/reader/series/${seriesId}/progress`),
    enabled: enabled && Number.isFinite(seriesId) && seriesId > 0,
  })
}

export function useContinueReading(seriesId: number, enabled = true) {
  return useQuery({
    queryKey: ['reader-continue', seriesId],
    queryFn: () =>
      api<{ chapterId: number; page: number } | null>(`/reader/series/${seriesId}/continue`).catch(
        () => null,
      ),
    enabled: enabled && Number.isFinite(seriesId) && seriesId > 0,
    meta: { silent: true },
  })
}

/**
 * Fire-and-forget position write. `pageIndex` is absolute so a debounced client may retry or
 * reorder freely, and failures stay silent — losing a page position must never interrupt reading.
 */
export async function saveProgress(chapterId: number, pageIndex: number, completed?: boolean) {
  await api(`/reader/chapter/${chapterId}/progress`, {
    method: 'PUT',
    body: JSON.stringify({ pageIndex, completed }),
  })
}

/** Position flush that survives the page being closed; `keepalive` allows the API-key header. */
export async function flushProgress(chapterId: number, pageIndex: number, completed?: boolean) {
  const init = await getInitialize()
  await fetch(`${init.apiRoot}/reader/chapter/${chapterId}/progress`, {
    method: 'PUT',
    keepalive: true,
    headers: { 'X-Api-Key': init.apiKey, 'Content-Type': 'application/json' },
    body: JSON.stringify({ pageIndex, completed }),
  })
}

export interface ReaderSettings {
  defaults: ReaderPrefs
  pushToKavita: boolean
}

export function useReaderSettings() {
  return useQuery({
    queryKey: ['settings', 'reader'],
    queryFn: () => api<ReaderSettings>('/settings/reader'),
  })
}

export function useSaveReaderSettings() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (settings: ReaderSettings) =>
      api<ReaderSettings>('/settings/reader', { method: 'PUT', body: JSON.stringify(settings) }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['settings', 'reader'] })
    },
  })
}

export interface KavitaImportStatus {
  running: boolean
  finishedAt: string | null
  result: { seriesMatched: number; chaptersMarked: number; seriesUnmatched: number } | null
  error: string | null
}

export function useKavitaReadImport() {
  const [polling, setPolling] = useState(false)
  const query = useQuery({
    queryKey: ['reader-kavita-import'],
    queryFn: () => api<KavitaImportStatus>('/reader/import/kavita'),
    // Only poll while an import is actually in flight.
    refetchInterval: polling ? 1500 : false,
  })

  useEffect(() => {
    setPolling(query.data?.running ?? false)
  }, [query.data?.running])

  const queryClient = useQueryClient()
  const start = useMutation({
    mutationFn: () => api('/reader/import/kavita', { method: 'POST' }),
    onSuccess: () => {
      setPolling(true)
      void queryClient.invalidateQueries({ queryKey: ['reader-kavita-import'] })
    },
  })

  // A finished import changes read state across the whole library.
  const finishedAt = query.data?.finishedAt
  useEffect(() => {
    if (!finishedAt) return
    void queryClient.invalidateQueries({ queryKey: ['series'] })
    void queryClient.invalidateQueries({ queryKey: ['reader-progress'] })
    void queryClient.invalidateQueries({ queryKey: ['reader-used'] })
  }, [finishedAt, queryClient])

  return { status: query.data, start }
}

export interface BookmarkDto {
  id: number
  chapterId: number
  pageIndex: number
  createdAt: string
}

export function useBookmarks(chapterId: number) {
  return useQuery({
    queryKey: ['reader-bookmarks', chapterId],
    queryFn: () => api<BookmarkDto[]>(`/reader/chapter/${chapterId}/bookmarks`),
    enabled: Number.isFinite(chapterId) && chapterId > 0,
  })
}

export function useToggleBookmark(chapterId: number) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (page: number) =>
      api<{ bookmarked: boolean }>(`/reader/chapter/${chapterId}/bookmark/${page}`, { method: 'PUT' }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['reader-bookmarks', chapterId] })
    },
  })
}

export function useSetChapterRead(seriesId: number) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ chapterId, read }: { chapterId: number; read: boolean }) =>
      api(`/reader/chapter/${chapterId}/${read ? 'read' : 'unread'}`, { method: 'POST' }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['reader-progress', seriesId] })
      void queryClient.invalidateQueries({ queryKey: ['reader-continue', seriesId] })
      void queryClient.invalidateQueries({ queryKey: ['series', seriesId] })
      void queryClient.invalidateQueries({ queryKey: ['series'] })
    },
  })
}
