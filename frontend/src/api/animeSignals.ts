import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { api } from './client'
import { affectedKeys } from './recommendationFeedback'

export type AnimeSignalRole = 'positive' | 'avoided' | 'neutral' | 'unmatched'
export type AnimeSignalStatus = 'Watching' | 'Completed' | 'OnHold' | 'Dropped' | 'Planning'

/**
 * One work, not one list row. The server folds the same show listed on two trackers together and
 * averages a franchise's seasons into a single opinion, so a reader who scrobbles to both AniList
 * and MyAnimeList sees each title once.
 */
export interface AnimeSignalEntry {
  /** Stable row key across syncs. */
  key: string
  /** Every tracker that listed this work. */
  services: string[]
  /** Distinct anime behind the entry: 3 means three seasons were averaged into it. */
  animeCount: number
  title: string
  /** Averaged across the seasons that carry a score, so it is often fractional. */
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
