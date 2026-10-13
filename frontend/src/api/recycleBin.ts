import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, ApiError } from './client'

export type RecycleReason = 'deleteFile' | 'removeChapter' | 'seriesDelete'

export interface RecycleBinEntry {
  id: number
  seriesId: number
  seriesTitle: string
  /** False once the series was removed, or moved to another root folder: a restore then leaves the file unlinked. */
  seriesExists: boolean
  fileName: string
  relativePath: string
  size: number
  reason: RecycleReason
  deletedAt: string
  deletedBy: string | null
  expiresAt: string
  /** The entry is there but its file is gone from the bin folder. */
  missing: boolean
  chapters: { number: number | null; volume: number | null; language: string }[]
}

export interface RecycleBin {
  retentionDays: number
  totalBytes: number
  entries: RecycleBinEntry[]
}

/** True when a delete failed only because the recycle bin could not take the files. */
export function offersPermanentDelete(error: unknown): boolean {
  return error instanceof ApiError && error.permanentDeleteAvailable
}

export function useRecycleBin(enabled = true) {
  return useQuery({
    queryKey: ['recycle-bin'],
    queryFn: () => api<RecycleBin>('/recyclebin'),
    enabled,
  })
}

/** What the delete dialogs quote. Falls back to the server default until it has loaded. */
export function useRecycleBinDays(enabled = true): number {
  const { data } = useQuery({
    queryKey: ['recycle-bin', 'retention'],
    queryFn: () => api<{ days: number }>('/recyclebin/retention'),
    enabled,
    staleTime: 60_000,
  })
  return data?.days ?? 14
}

function useInvalidateLibrary() {
  const queryClient = useQueryClient()
  return () => {
    void queryClient.invalidateQueries({ queryKey: ['recycle-bin'] })
    void queryClient.invalidateQueries({ queryKey: ['chapters'] })
    void queryClient.invalidateQueries({ queryKey: ['series-files'] })
    void queryClient.invalidateQueries({ queryKey: ['series'] })
  }
}

export function useRestoreFromRecycleBin() {
  const invalidate = useInvalidateLibrary()
  return useMutation({
    mutationFn: (id: number) =>
      api<{ linked: boolean; chapters: number }>(`/recyclebin/${id}/restore`, { method: 'POST' }),
    onSuccess: invalidate,
  })
}

export function useDeleteFromRecycleBin() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => api<void>(`/recyclebin/${id}`, { method: 'DELETE' }),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['recycle-bin'] }),
  })
}

export function useEmptyRecycleBin() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: () => api<{ deleted: number; failed: number }>('/recyclebin', { method: 'DELETE' }),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['recycle-bin'] }),
  })
}

export function useSetRecycleBinRetention() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (days: number) =>
      api<void>('/recyclebin/retention', { method: 'PUT', body: JSON.stringify({ days }) }),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['recycle-bin'] }),
  })
}
