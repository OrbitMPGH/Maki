import { useMutation, useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query'
import { api } from './client'
import type { ReaderManifest } from './reader'
import type { SeriesDto } from './types'

export type NoteVisibility = 'private' | 'shared'

export interface NoteDto {
  id: number
  chapterId: number
  parentId: number | null
  pageIndex: number | null
  visibility: NoteVisibility
  /** Empty for a tombstone. */
  body: string
  /** Null once the author's account is gone. */
  author: { id: number; name: string | null } | null
  isMine: boolean
  canEdit: boolean
  canDelete: boolean
  createdAt: string
  editedAt: string | null
  deleted: 'author' | 'moderator' | null
  /** Roots only. */
  replies?: NoteDto[]
}

export interface NoteCountDto {
  chapterId: number
  /** Roots I wrote, in either visibility. */
  mine: number
  /** Shared roots by anyone, so a shared root of mine counts here as well. */
  shared: number
  /** Distinct notes the viewer can see on the chapter, mine and shared counted once. */
  total: number
}

export interface NotePatch {
  body?: string
  pageIndex?: number | null
  visibility?: NoteVisibility
  clearPage?: boolean
}

export const MAX_NOTE_LENGTH = 4000

export const notesKey = (chapterId: number) => ['notes', chapterId] as const
export const noteCountsKey = (seriesId: number) => ['notes', 'counts', seriesId] as const

const validId = (id: number) => Number.isFinite(id) && id > 0

/**
 * The reader's toolbar badge and page markers come from the manifest so they draw with the first
 * paint. Whenever the list is fetched it is also the fresher answer, so the cached manifest is
 * brought in line with it instead of being refetched (a manifest refetch mid-chapter is not free).
 */
function syncManifest(queryClient: QueryClient, chapterId: number, notes: NoteDto[]) {
  const roots = notes.filter((n) => n.deleted === null)
  const noteCount = roots.length
  const notedPages = [...new Set(roots.flatMap((n) => (n.pageIndex === null ? [] : [n.pageIndex])))].sort((a, b) => a - b)
  queryClient.setQueryData<ReaderManifest>(['reader-manifest', chapterId], (old) => {
    if (!old) return old
    const same =
      old.noteCount === noteCount &&
      old.notedPages?.length === notedPages.length &&
      old.notedPages.every((p, i) => p === notedPages[i])
    return same ? old : { ...old, noteCount, notedPages }
  })
}

function notesQuery(queryClient: QueryClient, chapterId: number) {
  return {
    queryKey: notesKey(chapterId),
    queryFn: async () => {
      const notes = await api<NoteDto[]>(`/chapters/${chapterId}/notes`)
      syncManifest(queryClient, chapterId, notes)
      return notes
    },
  }
}

export function useChapterNotes(chapterId: number, enabled = true) {
  const queryClient = useQueryClient()
  return useQuery({
    ...notesQuery(queryClient, chapterId),
    enabled: enabled && validId(chapterId),
    // The panel draws its own failure and offline states.
    meta: { silent: true },
  })
}

/** Per chapter note counts for the series page's chapter table. */
export function useSeriesNoteCounts(seriesId: number, enabled = true) {
  return useQuery({
    queryKey: noteCountsKey(seriesId),
    queryFn: () => api<NoteCountDto[]>(`/series/${seriesId}/notes/counts`),
    enabled: enabled && validId(seriesId),
    meta: { silent: true },
  })
}

/** Only used to warn before a shared note on a series that hides your reading. */
export function useSeriesIncognito(seriesId: number, enabled: boolean) {
  return useQuery({
    queryKey: ['series', seriesId],
    queryFn: () => api<SeriesDto>(`/series/${seriesId}`),
    enabled: enabled && validId(seriesId),
    staleTime: 60_000,
    meta: { silent: true },
    select: (series) => series.incognito === 'Full',
  })
}

/**
 * Refetches what a change to this chapter's notes touches. The list is refetched when something is
 * showing it, and also when only the reader's manifest is cached, so its badge follows a note
 * someone else wrote while the panel was shut.
 */
export function refreshNotes(queryClient: QueryClient, seriesId: number, chapterId: number) {
  void queryClient.invalidateQueries({ queryKey: notesKey(chapterId) })
  void queryClient.invalidateQueries({ queryKey: noteCountsKey(seriesId) })
  if (queryClient.getQueryData(['reader-manifest', chapterId])) {
    void queryClient.fetchQuery({ ...notesQuery(queryClient, chapterId), staleTime: 0 }).catch(() => {})
  }
}

export function useNoteActions(chapterId: number, seriesId: number) {
  const queryClient = useQueryClient()
  // Success and failure both refetch: a refused change (a thread that gained a reply, a note
  // someone removed) is a sign the list on screen is out of date.
  const settled = () => refreshNotes(queryClient, seriesId, chapterId)
  const meta = { silent: true }

  const create = useMutation({
    mutationFn: (body: { body: string; pageIndex: number | null; visibility: NoteVisibility }) =>
      api<NoteDto>(`/chapters/${chapterId}/notes`, { method: 'POST', body: JSON.stringify(body) }),
    onSettled: settled,
    meta,
  })
  const reply = useMutation({
    mutationFn: ({ rootId, body }: { rootId: number; body: string }) =>
      api<NoteDto>(`/notes/${rootId}/replies`, { method: 'POST', body: JSON.stringify({ body }) }),
    onSettled: settled,
    meta,
  })
  const update = useMutation({
    mutationFn: ({ id, patch }: { id: number; patch: NotePatch }) =>
      api<NoteDto>(`/notes/${id}`, { method: 'PATCH', body: JSON.stringify(patch) }),
    onSettled: settled,
    meta,
  })
  const remove = useMutation({
    mutationFn: (id: number) => api<void>(`/notes/${id}`, { method: 'DELETE' }),
    onSettled: settled,
    meta,
  })

  return { create, reply, update, remove }
}

/**
 * Where a note sits for display. A page past the end of the current file shows as a whole chapter
 * note rather than disappearing, so a caller that knows the page count passes it.
 */
export function notePage(note: NoteDto, pageCount?: number): number | null {
  if (note.pageIndex === null) return null
  if (pageCount !== undefined && note.pageIndex >= pageCount) return null
  return note.pageIndex
}
