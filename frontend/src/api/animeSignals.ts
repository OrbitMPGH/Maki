import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { api } from './client'
import { affectedKeys } from './recommendationFeedback'

export type AnimeSignalRole = 'positive' | 'avoided' | 'neutral' | 'unmatched'
export type AnimeSignalStatus = 'Watching' | 'Completed' | 'OnHold' | 'Dropped' | 'Planning'

export interface AnimeSignalEntry {
  service: string
  animeId: number
  title: string
  score: number | null
  status: AnimeSignalStatus
  mangaBakaId: number | null
  mangaTitle: string | null
  role: AnimeSignalRole
}

export interface AnimeSignalCounts {
  total: number
  matched: number
  positive: number
  avoided: number
  ignored: number
}

export interface AnimeSignalsData {
  enabled: boolean
  instanceEnabled: boolean
  lastSyncAtUtc: string | null
  syncing: boolean
  services: string[]
  counts: AnimeSignalCounts
  entries: AnimeSignalEntry[]
}

export interface AnimeSignalSyncResult {
  fetched: number
  matched: number
  removed: number
  looked: number
  lastSyncAtUtc: string | null
}

/**
 * Polls every 4s while a sync is in flight so the "Sync now" button and the counts settle back to
 * idle on their own; otherwise a plain fetch, since the section is hidden while it loads.
 */
export function useAnimeSignals() {
  return useQuery({
    queryKey: ['anime-signals'],
    queryFn: () => api<AnimeSignalsData>('/recommendations/anime-signals'),
    refetchInterval: (query) => (query.state.data?.syncing ? 4000 : false),
  })
}

function useRefreshAnimeSignals() {
  const client = useQueryClient()
  return () => {
    void client.invalidateQueries({ queryKey: ['anime-signals'] })
    for (const key of affectedKeys) void client.invalidateQueries({ queryKey: [key] })
  }
}

export function useSetAnimeSignalsEnabled() {
  const refresh = useRefreshAnimeSignals()
  return useMutation({
    mutationFn: (enabled: boolean) =>
      api<{ enabled: boolean }>('/recommendations/anime-signals/settings', {
        method: 'PUT', body: JSON.stringify({ enabled }),
      }),
    onSuccess: refresh,
  })
}

export function useSyncAnimeSignals() {
  const refresh = useRefreshAnimeSignals()
  return useMutation({
    mutationFn: () => api<AnimeSignalSyncResult>('/recommendations/anime-signals/sync', { method: 'POST' }),
    onSuccess: refresh,
  })
}
