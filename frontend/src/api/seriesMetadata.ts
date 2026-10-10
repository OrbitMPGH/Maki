import { useMutation, useQueryClient } from '@tanstack/react-query'
import { api, apiUpload } from './client'
import type { MetadataField } from './types'

export interface MetadataState {
  lockedFields: MetadataField[]
  /** After a reset: whether the provider answered. False leaves the old value until the next refresh. */
  refreshed: boolean
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
}

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
