import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from './client'

export type UpscaleModelStatus = 'missing' | 'downloading' | 'ready' | 'failed'

export interface UpscalingSettings {
  enabled: boolean
  models: string[]
  threads: number
  cacheMb: number
  maxThreads?: number
}

export interface UpscaleCatalogEntry {
  id: string
  name?: string
  bytes: number
  license: string
  author: string
  source: string
  status: UpscaleModelStatus
  error?: string | null
}

export interface UpscalingState {
  settings: UpscalingSettings
  catalog: UpscaleCatalogEntry[]
  cache: { bytes: number; files: number }
  queue: { pending: number; working: number }
}

const key = ['upscaling']

export function useUpscaling() {
  return useQuery({
    queryKey: key,
    queryFn: () => api<UpscalingState>('/upscaling'),
    refetchInterval: (query) =>
      query.state.data?.catalog.some((m) => m.status === 'downloading') ? 3000 : false,
  })
}

function useStateMutation<T>(fn: (input: T) => Promise<UpscalingState>) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: fn,
    onSuccess: (state) => {
      if (state && 'settings' in state) queryClient.setQueryData(key, state)
    },
    onSettled: () => queryClient.invalidateQueries({ queryKey: key }),
  })
}

export function useSaveUpscaling() {
  return useStateMutation((settings: UpscalingSettings) =>
    api<UpscalingState>('/upscaling/settings', { method: 'PUT', body: JSON.stringify(settings) }),
  )
}

export function useDownloadUpscaleModel() {
  return useStateMutation((id: string) =>
    api<UpscalingState>(`/upscaling/models/${encodeURIComponent(id)}/download`, { method: 'POST' }),
  )
}

export function useDeleteUpscaleModel() {
  return useStateMutation((id: string) =>
    api<UpscalingState>(`/upscaling/models/${encodeURIComponent(id)}`, { method: 'DELETE' }),
  )
}

export function useClearUpscaleCache() {
  return useStateMutation(() => api<UpscalingState>('/upscaling/cache/clear', { method: 'POST' }))
}

export function useUpscaleBenchmark() {
  return useMutation({
    mutationFn: (model: string) =>
      api<{ seconds: number }>('/upscaling/benchmark', { method: 'POST', body: JSON.stringify({ model }) }),
  })
}
