import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { api } from './client'
import { affectedKeys } from './recommendationFeedback'

/**
 * `superseded` means the reader's own evidence already covers this manga - it is on their shelf,
 * they thumbed it, or they excluded it as a seed - so the signal reaches nothing. It outranks the
 * other roles rather than sitting beside them, because it is the answer to "why is this here
 * twice".
 */
export type AnimeSignalRole = 'positive' | 'avoided' | 'neutral' | 'superseded' | 'unmatched'

/** Which of the reader's own actions replaced a signal. Null unless the role is `superseded`. */
export type AnimeSupersededBy = 'library' | 'feedback' | 'ignored'
export type AnimeSignalStatus = 'Watching' | 'Completed' | 'OnHold' | 'Dropped' | 'Planning'

/** How much authority watched anime carry over recommendations. */
export type AnimeSignalStrength = 'subtle' | 'balanced' | 'full'

export interface AnimeSignalStrengthInfo {
  value: AnimeSignalStrength
  /** Share of a manga rating's authority: 1 means a watched anime counts as a read one. */
  ratingShare: number
  /** What a 10/10 completed anime seeds at, against the 1.0 an unrated shelf title gets. */
  topSeedWeight: number
}

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
  supersededBy: AnimeSupersededBy | null
}

/** One count per role, so nothing here has to be derived by subtraction. */
export interface AnimeSignalCounts {
  total: number
  matched: number
  positive: number
  avoided: number
  neutral: number
  superseded: number
  unmatched: number
}

export interface AnimeSignalsData {
  enabled: boolean
  instanceEnabled: boolean
  lastSyncAtUtc: string | null
  syncing: boolean
  services: string[]
  strength: AnimeSignalStrength
  /** Every level with the arithmetic behind it, so the panel never keeps its own copy. */
  strengths: AnimeSignalStrengthInfo[]
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
      api<{ enabled: boolean; strength: AnimeSignalStrength }>(
        '/recommendations/anime-signals/settings',
        { method: 'PUT', body: JSON.stringify({ enabled }) },
      ),
    onSuccess: refresh,
  })
}

/**
 * Moving the dial re-weights every anime seed, so it invalidates the recommendation queries the
 * same way the on/off switch does. `enabled` rides along because the endpoint owns both.
 */
export function useSetAnimeSignalsStrength() {
  const refresh = useRefreshAnimeSignals()
  return useMutation({
    mutationFn: ({ enabled, strength }: { enabled: boolean; strength: AnimeSignalStrength }) =>
      api<{ enabled: boolean; strength: AnimeSignalStrength }>(
        '/recommendations/anime-signals/settings',
        { method: 'PUT', body: JSON.stringify({ enabled, strength }) },
      ),
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
