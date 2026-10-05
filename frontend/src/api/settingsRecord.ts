import { useMutation, useQueryClient, type QueryKey } from '@tanstack/react-query'
import { api } from './client'

/**
 * Saves part of a settings record that several cards edit but the server takes as one PUT of the
 * whole thing. Saves to one record run one at a time, each merges its patch over the cached record
 * when it actually runs, and the server's answer goes straight into the cache. A card saving while
 * another card's save is still in flight therefore sends that card's new values, not the old ones.
 */
export function useSaveSettingsRecord<T extends object>(queryKey: QueryKey, path: string, onSaved?: () => void) {
  const queryClient = useQueryClient()
  return useMutation({
    scope: { id: path },
    mutationFn: async (patch: Partial<T>) => {
      const current = await queryClient.ensureQueryData({ queryKey, queryFn: () => api<T>(path) })
      return api<T>(path, { method: 'PUT', body: JSON.stringify({ ...current, ...patch }) })
    },
    onSuccess: (saved) => {
      queryClient.setQueryData(queryKey, saved)
      onSaved?.()
    },
  })
}
