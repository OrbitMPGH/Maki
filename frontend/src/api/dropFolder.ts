import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from './client'

export type DropFolderPlacement = 'move' | 'copy'

export interface DropFolderSettings {
  enabled: boolean
  path: string | null
  intervalMinutes: number
  placement: DropFolderPlacement
}

/** `unmatched`, `ambiguous`, `failed` or `nothingNew`. */
export type DropFolderPendingReason = 'unmatched' | 'ambiguous' | 'failed' | 'nothingNew'

export interface DropFolderPending {
  name: string
  path: string
  reason: DropFolderPendingReason
  seriesTitles: string[]
  detail: string | null
  seenAt: string
  isFolder: boolean
  folder: string | null
}

export interface DropFolderStatus {
  enabled: boolean
  path: string | null
  lastRunAt: string | null
  lastError: string | null
  pending: DropFolderPending[]
}

export function useDropFolderSettings() {
  return useQuery({
    queryKey: ['dropfolder', 'settings'],
    queryFn: () => api<DropFolderSettings>('/dropfolder/settings'),
  })
}

export function useSaveDropFolderSettings() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (body: DropFolderSettings) =>
      api<DropFolderSettings>('/dropfolder/settings', { method: 'PUT', body: JSON.stringify(body) }),
    onSuccess: (data) => {
      queryClient.setQueryData(['dropfolder', 'settings'], data)
      void queryClient.invalidateQueries({ queryKey: ['dropfolder', 'status'] })
    },
  })
}

/** Admin only; pass `enabled` false for anyone else, whom the endpoint answers with a 403. */
export function useDropFolderStatus(enabled = true) {
  return useQuery({
    queryKey: ['dropfolder', 'status'],
    queryFn: () => api<DropFolderStatus>('/dropfolder'),
    enabled,
    refetchInterval: 60_000,
  })
}

export function useRunDropFolder() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: () => api<DropFolderStatus>('/dropfolder/run', { method: 'POST' }),
    onSuccess: (data) => {
      queryClient.setQueryData(['dropfolder', 'status'], data)
      void queryClient.invalidateQueries({ queryKey: ['queue-history'] })
    },
  })
}
