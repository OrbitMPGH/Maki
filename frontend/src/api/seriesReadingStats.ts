import { useQuery } from '@tanstack/react-query'
import { api } from './client'

export interface SeriesReadingStats {
  /** Active seconds in the built-in reader, across every chapter of the series. */
  seconds: number
  timedChapters: number
  /** Median over finished, timed chapters; null under three of them. */
  chapterMedianSeconds: number | null
  /** The same median over your other series read the same way (paged or scrolling). */
  elsewhereMedianSeconds: number | null
  /** Zero for a fully incognito series, whose time never reaches the activity log. */
  sittings: number
  firstReadAt: string
  lastReadAt: string
  bucketUnit: 'week' | 'month'
  /** Oldest first, ending at this week or month. Bucket is "2026-11-09" (week start) or "2026-11". */
  timeline: { bucket: string; seconds: number }[]
}

/**
 * Your reading time in one series. Resolves to undefined (a 204) when the built-in reader has
 * recorded none, which is the signal to show no panel at all. Keyed under `['series', id]` so the
 * read-progress push refreshes it with the series.
 */
export function useSeriesReadingStats(seriesId: number, enabled = true) {
  return useQuery({
    queryKey: ['series', seriesId, 'reading-stats'],
    queryFn: async () => (await api<SeriesReadingStats | undefined>(`/series/${seriesId}/reading-stats`)) ?? null,
    enabled: enabled && Number.isFinite(seriesId) && seriesId > 0,
  })
}
