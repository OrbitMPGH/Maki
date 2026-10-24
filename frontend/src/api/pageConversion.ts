import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from './client'

export type PageConversionFormat = 'off' | 'webp' | 'avif'

export interface PageConversionSettings {
  format: PageConversionFormat
  webpQuality: number
  avifQuality: number
}

export interface PageConversionPassTotals {
  startedAtUtc: string
  finishedAtUtc: string | null
  converted: number
  unchanged: number
  hardlinked: number
  failed: number
  savedBytes: number
}

export interface PageConversionStatus {
  settings: PageConversionSettings
  pass: {
    state: 'idle' | 'running' | 'paused'
    /** Why a pass paused itself rather than being paused. */
    hold: 'lowDisk' | 'conversionOff' | null
    currentFile: string | null
    /** The pass in progress, or the last one that finished. */
    totals: PageConversionPassTotals | null
  }
  library: {
    convertedFiles: number
    savedBytes: number
    pendingFiles: number
    trashBytes: number
    trashFiles: number
    trashDays: number
  }
}

const key = ['page-conversion']

export function usePageConversion() {
  return useQuery({
    queryKey: key,
    queryFn: () => api<PageConversionStatus>('/pageconversion'),
    refetchInterval: (query) => (query.state.data?.pass.state === 'running' ? 3000 : false),
  })
}

function useStatusMutation<T>(fn: (input: T) => Promise<PageConversionStatus>) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: fn,
    onSuccess: (status) => queryClient.setQueryData(key, status),
  })
}

export function useSavePageConversion() {
  return useStatusMutation((settings: PageConversionSettings) =>
    api<PageConversionStatus>('/pageconversion/settings', {
      method: 'PUT',
      body: JSON.stringify(settings),
    }),
  )
}

export function useStartPageConversion() {
  return useStatusMutation(() => api<PageConversionStatus>('/pageconversion/start', { method: 'POST' }))
}

export function usePausePageConversion() {
  return useStatusMutation(() => api<PageConversionStatus>('/pageconversion/pause', { method: 'POST' }))
}
