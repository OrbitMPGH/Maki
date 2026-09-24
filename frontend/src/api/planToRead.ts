import { useMutation, useQuery, useQueryClient, type UseMutationResult, type UseQueryResult } from '@tanstack/react-query'
import { api } from './client'

export interface PlanToReadEntry {
  providerId: number
  title: string
  coverUrl: string | null
  origin: 'taste' | 'trending' | 'manual'
  addedAtUtc: string
  inLibrarySeriesId: number | null
  requestStatus: 'pending' | null
}

/** The Shortlist, newest first. */
export function usePlanToRead(): UseQueryResult<PlanToReadEntry[]> {
  return useQuery({
    queryKey: ['plan-to-read'],
    queryFn: () => api<PlanToReadEntry[]>('/plan-to-read'),
  })
}

export function usePlanToReadAdd(): UseMutationResult<
  PlanToReadEntry, Error, { providerId: number; origin: 'taste' | 'trending' | 'manual'; clientMutationId: string }
> {
  const client = useQueryClient()
  return useMutation({
    mutationFn: ({ providerId, origin, clientMutationId }) =>
      api<PlanToReadEntry>(`/plan-to-read/${providerId}`, {
        method: 'PUT', body: JSON.stringify({ origin, clientMutationId }),
      }),
    onSettled: () => { void client.invalidateQueries({ queryKey: ['plan-to-read'] }) },
  })
}

export function usePlanToReadRemove(): UseMutationResult<void, Error, number> {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (providerId: number) => api<void>(`/plan-to-read/${providerId}`, { method: 'DELETE' }),
    onSettled: () => { void client.invalidateQueries({ queryKey: ['plan-to-read'] }) },
  })
}
