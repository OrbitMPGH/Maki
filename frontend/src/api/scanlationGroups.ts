import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from './client'

/** Preferred groups in order, and groups never taken. Names match case-insensitively and per member of a joint credit. */
export interface ScanlationGroupLists {
  preferred: string[]
  blocked: string[]
}

export interface SeenScanlationGroup {
  name: string
  /** Chapters credited to the group, a joint release counting for each member. */
  chapters: number
}

export interface GlobalScanlationGroups extends ScanlationGroupLists {
  /** Groups the library's linked chapters are credited to, most chapters first. */
  seen: SeenScanlationGroup[]
}

export interface SeriesScanlationGroups extends ScanlationGroupLists {
  /** What applies where the series' own lists say nothing. */
  global: ScanlationGroupLists
  /** Every group with an upload of this series, read live from its sources. */
  seen: SeenScanlationGroup[]
  /** The series' sources that offer a choice of uploads. */
  sources: string[]
  /** Those whose listing failed just now, so their groups are missing from `seen`. */
  unreachable: string[]
}

export function useGlobalScanlationGroups(enabled: boolean) {
  return useQuery({
    queryKey: ['scanlationgroups'],
    queryFn: () => api<GlobalScanlationGroups>('/scanlationgroups'),
    enabled,
  })
}

export function useSaveGlobalScanlationGroups() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (lists: ScanlationGroupLists) =>
      api<ScanlationGroupLists>('/scanlationgroups', { method: 'PUT', body: JSON.stringify(lists) }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['scanlationgroups'] })
    },
  })
}

/** Lists the series' uploads on every source that has a choice, so it is only asked for while the dialog is open. */
export function useSeriesScanlationGroups(seriesId: number, enabled: boolean) {
  return useQuery({
    queryKey: ['scanlationgroups', 'series', seriesId],
    queryFn: () => api<SeriesScanlationGroups>(`/scanlationgroups/series/${seriesId}`),
    enabled,
    staleTime: 60_000,
  })
}

export function useSaveSeriesScanlationGroups(seriesId: number) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (lists: ScanlationGroupLists) =>
      api<ScanlationGroupLists>(`/scanlationgroups/series/${seriesId}`, { method: 'PUT', body: JSON.stringify(lists) }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['scanlationgroups', 'series', seriesId] })
    },
  })
}
