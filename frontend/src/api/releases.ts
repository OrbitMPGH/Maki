import { useQuery } from '@tanstack/react-query'
import { api } from './client'

/** Mirrors `CadenceState` on the server. */
export type CadenceState = 'Unknown' | 'Irregular' | 'Expected' | 'Late' | 'Stalled'

/** Mirrors `RefreshTier` on the server: why the next check is when it is. */
export type RefreshTier = 'Fixed' | 'Unknown' | 'JustReleased' | 'Active' | 'Slowing' | 'Quiet' | 'Announced'

/**
 * When a series' next chapter is likely. The window is a span drawn from the series' own release
 * history with a measured confidence; `announcedAt` is the only date, and only because a source
 * (MANGA Plus) stated it.
 */
export interface ReleaseOutlookDto {
  state: CadenceState
  releaseDays: number
  /** Median gap between release days, in days. */
  interval: number | null
  /** Median distance of a gap from `interval`, in days. */
  spread: number | null
  lastRelease: string | null
  windowStart: string | null
  windowEnd: string | null
  /** 0..1, how often the last releases landed inside the window drawn for them. */
  confidence: number
  hits: number
  checked: number
  announcedAt: string | null
  announcedBy: string | null
  /** False when the monitored refresh no longer visits the series. */
  refreshing: boolean
  refreshTier: RefreshTier
  lastCheckAt: string | null
  nextCheckAt: string | null
}

export interface UpcomingSeriesDto {
  seriesId: number
  title: string
  coverUrl: string | null
  outlook: ReleaseOutlookDto
}

/** Mirrors `ReleaseCadence.HighConfidence` / `MinConfidence`. */
export const HIGH_CONFIDENCE = 0.75
export const MIN_CONFIDENCE = 0.55

export type ConfidenceLevel = 'high' | 'medium' | 'low'

export function confidenceLevel(confidence: number): ConfidenceLevel {
  return confidence >= HIGH_CONFIDENCE ? 'high' : confidence >= MIN_CONFIDENCE ? 'medium' : 'low'
}

/** Under `['series', id]`, so whatever refreshes the series page refreshes this too. */
export function useSeriesOutlook(seriesId: number) {
  return useQuery({
    queryKey: ['series', seriesId, 'outlook'],
    queryFn: () => api<ReleaseOutlookDto>(`/series/${seriesId}/outlook`),
  })
}

export function useUpcoming(enabled = true) {
  return useQuery({
    queryKey: ['upcoming'],
    queryFn: () => api<UpcomingSeriesDto[]>('/upcoming'),
    enabled,
  })
}
