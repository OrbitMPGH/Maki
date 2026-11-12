import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from './client'

export type UpscaleModelStatus = 'missing' | 'downloading' | 'ready' | 'failed'

export interface UpscalingSettings {
  enabled: boolean
  models: string[]
  threads: number
  cacheMb: number
  maxThreads?: number
  /** Upscaled colour pages are stored as JPEG XL. Unset on an older server. */
  jpegXl?: boolean
  /** Whether the server has libjxl; without it `jpegXl` changes nothing. */
  jpegXlAvailable?: boolean
  /** Prepare chapters ahead. Unset on an older server. */
  prepare?: PrepareAheadSettings
}

/** Upscale the next chapters of series being read while nobody is reading. */
export interface PrepareAheadSettings {
  enabled: boolean
  /** Series read within this many days count. */
  days: number
  /** At most this many series per reader. */
  series: number
  /** Chapters prepared in each series. */
  chapters: number
  /** Space prepared pages may take, apart from the cache size. */
  mb: number
}

export interface UpscaleCatalogEntry {
  id: string
  name?: string
  /** Null until the model's manifest has been read. */
  bytes: number | null
  license: string
  author: string
  source: string
  status: UpscaleModelStatus
  error?: string | null
  /** Ships with Maki: never downloaded, cannot be deleted. */
  bundled?: boolean
}

export interface UpscalingState {
  settings: UpscalingSettings
  catalog: UpscaleCatalogEntry[]
  cache: { bytes: number; files: number }
  queue: { pending: number; working: number }
  /** What the last prepare-ahead plan holds. Unset on an older server. */
  prepared?: PreparedState
}

export interface PreparedState {
  /** Chapters the plan holds. */
  chapters: number
  /** Their pages, made or not. */
  pages: number
  /** Pages still to make. */
  waiting: number
  /** What the made pages take up. */
  bytes: number
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
