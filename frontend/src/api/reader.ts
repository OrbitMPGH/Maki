import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import type { ReaderPrefs } from '../pages/reader/prefs'
import { api, authHeaders, getInitialize } from './client'
import { useConnectionSettings } from './hooks'

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
  /** Whatever won: the series override, a reading profile, or the global defaults. */
  prefs: ReaderPrefs
  prefsSource: PrefsSource
  /** The profile in force, when `prefsSource` is `Profile`. */
  profileId: number | null
  profileName: string | null
  /** Set when the user pinned that profile by hand rather than the series' type selecting it. */
  pinnedProfileId: number | null
  /** What the series' type selects, whether or not it won. Labels the picker's "Auto" entry. */
  autoProfileId: number | null
  /** manga | manhwa | manhua | oel | other, or null when the series has no type yet. */
  seriesType: string | null
}

/** Which layer answered "what does this series look like". Mirrors `ReaderPrefsSource`. */
export type PrefsSource = 'Global' | 'Profile' | 'Series'

/** What both per-series prefs writes hand back: the freshly re-resolved answer. */
export interface ResolvedReaderPrefs {
  prefs: ReaderPrefs
  source: PrefsSource
  profileId: number | null
  profileName: string | null
  pinnedProfileId: number | null
  autoProfileId: number | null
}

export interface ChapterProgressDto {
  chapterId: number
  pageIndex: number
  pageCount: number
  completed: boolean
  /** Read state came from Kavita, not from reading it here: no page position is known. */
  external: boolean
  /**
   * Set when the chapter was explicitly marked unread here. Such a row is a tombstone, kept only
   * to stop the Kavita scan re-marking it, so it must read as unread and not as "in progress".
   */
  unreadAt: string | null
  updatedAt: string
}

/**
 * Page images are loaded by plain `<img src>`, which cannot send a header, but the request is
 * same-origin, so the browser attaches the session cookie by itself and the URL needs no credential.
 * This used to append the instance API key, which put it into browser history and into the access log
 * of every proxy the image request passed through.
 */
export async function pageUrl(chapterId: number, page: number, thumb = false): Promise<string> {
  const init = await getInitialize()
  const kind = thumb ? 'thumb' : 'page'
  return `${init.apiRoot}/reader/chapter/${chapterId}/${kind}/${page}`
}

export function useReaderManifest(chapterId: number) {
  return useQuery({
    queryKey: ['reader-manifest', chapterId],
    queryFn: () => api<ReaderManifest>(`/reader/chapter/${chapterId}`),
    enabled: Number.isFinite(chapterId) && chapterId > 0,
    // The page list of a stored archive doesn't change while the reader is open, so nothing
    // refetches mid-chapter, but `resumePage` and `completed` do change, and a cached snapshot of
    // them is poison: reopening a chapter would resume off the position it had when first opened,
    // then persist that stale page over the real one. Always refetch on mount, and see ReaderPage
    // for why the resume waits for that fetch instead of applying the cached value first.
    staleTime: Infinity,
    refetchOnMount: 'always',
  })
}

/**
 * Whether the built-in reader has ever been used. OR this with "Kavita is configured" to decide
 * whether read progress is meaningful: Kavita alone was the old gate and hides a reader-only
 * user's own progress.
 */
export function useReaderUsed() {
  return useQuery({
    queryKey: ['reader-used'],
    queryFn: () => api<{ used: boolean }>('/reader/used'),
    staleTime: 60_000,
  })
}

/**
 * Whether read progress is meaningful at all: Kavita is connected, or the built-in reader has been
 * used. Everything that renders read state gates on this, so a stale `ReadingState` row left by a
 * Kavita connection that has since been removed doesn't linger on the cards.
 */
export function useReadTracking(): boolean {
  const { data: kavita } = useConnectionSettings<{ url: string | null; apiKey: string | null }>('kavita')
  const { data: readerUsed } = useReaderUsed()
  return Boolean(kavita?.url && kavita?.apiKey) || Boolean(readerUsed?.used)
}

/**
 * Per-chapter read state, the ground truth. Deliberately not accompanied by the series'
 * high-water mark: that mark is forward-only and covers every chapter numbered below it, so
 * displaying it reported chapters read that had never been opened.
 */
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
 * reorder freely, and failures stay silent: losing a page position must never interrupt reading.
 */
export async function saveProgress(chapterId: number, pageIndex: number, completed?: boolean) {
  await api(`/reader/chapter/${chapterId}/progress`, {
    method: 'PUT',
    body: JSON.stringify({ pageIndex, completed }),
  })
}

/**
 * Position flush that survives the page being closed. Bypasses `api()` only for `keepalive`, which
 * lets the request outlive the document, but it still needs the antiforgery header, since this is a
 * cookie-authenticated PUT like any other.
 */
export async function flushProgress(chapterId: number, pageIndex: number, completed?: boolean) {
  const init = await getInitialize()
  await fetch(`${init.apiRoot}/reader/chapter/${chapterId}/progress`, {
    method: 'PUT',
    keepalive: true,
    credentials: 'same-origin',
    headers: authHeaders(),
    body: JSON.stringify({ pageIndex, completed }),
  })
}

export interface ReaderSettings {
  defaults: ReaderPrefs
  pushToKavita: boolean
  /**
   * Which account Kavita's reading is attributed to. Read-only here: it is an instance setting,
   * because Kavita is one external server behind one API key, but the reader card is where
   * "push my reads to Kavita" lives, and that toggle only does anything for this user.
   */
  kavitaUserId?: number | null
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
    mutationFn: (settings: Pick<ReaderSettings, 'defaults' | 'pushToKavita'>) =>
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
      // Both Home rails are derived from ChapterProgress, and marking unread can move a series
      // between them (or drop it from both).
      void queryClient.invalidateQueries({ queryKey: ['home'] })
    },
  })
}
