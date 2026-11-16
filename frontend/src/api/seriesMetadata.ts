import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, apiUpload } from './client'
import type { LocalizedTitle, MetadataField } from './types'

export interface MetadataState {
  lockedFields: MetadataField[]
  /** After a reset: whether the provider answered. False leaves the old value until the next refresh. */
  refreshed: boolean
  /** False when a cover reset got no poster from the provider, so the current one stays. */
  coverRestored: boolean
}

/** Only the fields named in `fields` are read; a null count named there clears it. */
export interface EditMetadataRequest {
  fields: Exclude<MetadataField, 'cover'>[]
  title?: string
  overview?: string | null
  status?: string
  totalChapters?: number | null
  totalVolumes?: number | null
  genres?: string[]
  year?: number | null
  authorStory?: string | null
  authorArt?: string | null
  publisher?: string | null
  altTitles?: LocalizedTitle[]
  /** Stored lowercased, as every sort over it is ordinal. */
  sortTitle?: string
  /** One of `SERIES_TYPES`, or null to clear. */
  type?: string | null
  /** At or below the editor's own ceiling, or null to clear (only an unrestricted editor may). */
  contentRating?: string | null
}

/** One row of a series' metadata history. Values are invariant text: a status by its server name, counts as digits. */
export interface MetadataChange {
  id: number
  field: MetadataField
  /** Null when the field was empty, and always null for the synopsis and the poster. */
  oldValue: string | null
  newValue: string | null
  source: 'refresh' | 'user'
  /** Who made a user change; null for a refresh, a deleted account, or another user's login name the reader may not see. */
  userName: string | null
  changedAt: string
}

export function useSeriesMetadataHistory(seriesId: number, enabled: boolean) {
  return useQuery({
    queryKey: ['series', seriesId, 'metadata-history'],
    queryFn: () => api<MetadataChange[]>(`/series/${seriesId}/metadata/history`),
    enabled,
  })
}

/** Also refreshes the history, which sits under the series key. */
function useInvalidateSeries() {
  const queryClient = useQueryClient()
  return (seriesId: number) => {
    void queryClient.invalidateQueries({ queryKey: ['series', seriesId] })
    void queryClient.invalidateQueries({ queryKey: ['series'] })
  }
}

export function useEditSeriesMetadata() {
  const invalidate = useInvalidateSeries()
  return useMutation({
    mutationFn: ({ seriesId, ...request }: EditMetadataRequest & { seriesId: number }) =>
      api<MetadataState>(`/series/${seriesId}/metadata`, { method: 'PUT', body: JSON.stringify(request) }),
    onSuccess: (_data, { seriesId }) => invalidate(seriesId),
  })
}

export function useResetSeriesMetadata() {
  const invalidate = useInvalidateSeries()
  return useMutation({
    mutationFn: ({ seriesId, fields }: { seriesId: number; fields: MetadataField[] }) =>
      api<MetadataState>(`/series/${seriesId}/metadata/reset`, { method: 'POST', body: JSON.stringify({ fields }) }),
    onSuccess: (_data, { seriesId }) => invalidate(seriesId),
  })
}

type CoverResult = { coverUrl: string | null; lockedFields: MetadataField[] }

/** Pages in a downloaded chapter of this series, for picking one as the poster. */
export function useCoverChapterPages(seriesId: number, chapterId: number | null) {
  return useQuery({
    queryKey: ['series', seriesId, 'cover-pages', chapterId],
    queryFn: () => api<{ pageCount: number }>(`/mediacover/${seriesId}/cover/chapter/${chapterId}/pages`),
    enabled: chapterId != null,
  })
}

export type CoverPick =
  | { from: 'page'; chapterId: number; page: number }
  | { from: 'source'; mappingId: number }
  | { from: 'url'; url: string }

/** Sets the poster from a chapter page (0-based), a linked source's cover, or a URL (admins only). Locks it like an upload. */
export function usePickSeriesCover() {
  const invalidate = useInvalidateSeries()
  return useMutation({
    mutationFn: ({ seriesId, pick }: { seriesId: number; pick: CoverPick }) => {
      const { from, ...body } = pick
      return api<CoverResult>(`/mediacover/${seriesId}/cover/from-${from}`, { method: 'POST', body: JSON.stringify(body) })
    },
    onSuccess: (_data, { seriesId }) => invalidate(seriesId),
  })
}

export function useUploadSeriesCover() {
  const invalidate = useInvalidateSeries()
  return useMutation({
    mutationFn: ({ seriesId, file }: { seriesId: number; file: File }) => {
      const form = new FormData()
      form.append('file', file)
      return apiUpload<{ coverUrl: string | null; lockedFields: MetadataField[] }>(`/mediacover/${seriesId}/cover`, form)
    },
    onSuccess: (_data, { seriesId }) => invalidate(seriesId),
  })
}
