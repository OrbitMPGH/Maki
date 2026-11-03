import { useCallback } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api } from './client'
import type { RecommendationItem } from './hooks'

/** One row of `GET series/lookup`: just enough to tell which catalogue items the library owns. */
export interface SeriesLookupRow {
  id: number
  mangaBakaId: number | null
  coverUrl: string | null
  archived: boolean
}

// Under the list's prefix, so everything that invalidates ['series'] refreshes this too.
export const SERIES_LOOKUP_KEY = ['series', 'lookup'] as const

const seriesLookupQuery = {
  queryKey: SERIES_LOOKUP_KEY,
  queryFn: () => api<SeriesLookupRow[]>('/series/lookup'),
  staleTime: 30_000,
} as const

const hasActiveSeries = (rows: SeriesLookupRow[]) => rows.some((s) => !s.archived)

/**
 * Whether the library holds any active series. A boolean rather than the list, so a page that only
 * needs to know doesn't re-render each time the lookup refreshes.
 */
export function useHasLibrary() {
  return useQuery({ ...seriesLookupQuery, select: hasActiveSeries })
}

// Plain objects, not Maps: React Query shares a select result that comes out equal to the last
// one, so the lookups below keep their identity across refreshes that change nothing they hold.
const idsByMangaBaka = (rows: SeriesLookupRow[]) => {
  const ids: Record<number, number> = {}
  for (const s of rows) if (s.mangaBakaId != null) ids[s.mangaBakaId] = s.id
  return ids
}
const coversByMangaBaka = (rows: SeriesLookupRow[]) => {
  const covers: Record<number, string> = {}
  for (const s of rows) if (s.mangaBakaId != null && s.coverUrl) covers[s.mangaBakaId] = s.coverUrl
  return covers
}

/** Maps a catalogue (MangaBaka) item to the library series id that owns it, or null. */
export function useSeriesIdLookup() {
  const { data: ids } = useQuery({ ...seriesLookupQuery, select: idsByMangaBaka })
  return useCallback((item: RecommendationItem) => ids?.[Number(item.providerId)] ?? null, [ids])
}

/** Maps a MangaBaka id to the poster of the library series that owns it, or null. */
export function useLibraryCoverLookup() {
  const { data: covers } = useQuery({ ...seriesLookupQuery, select: coversByMangaBaka })
  return useCallback(
    (mangaBakaId: string | null | undefined) => (mangaBakaId ? covers?.[Number(mangaBakaId)] ?? null : null),
    [covers],
  )
}
