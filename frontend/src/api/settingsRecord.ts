import { useMutation, useQueryClient, type QueryKey } from '@tanstack/react-query'
import { api } from './client'

interface SettingsRecordOptions<T> {
  /** Show a patch in the cache the moment it is made, instead of when the PUT returns. */
  optimistic?: boolean
  /** How a patch lands on a record; a shallow spread unless a nested field needs its own merge. */
  merge?: (current: T, patch: Partial<T>) => T
}

/**
 * Saves part of a settings record that several cards edit but the server takes as one PUT of the
 * whole thing. Saves to one record run one at a time, and each merges its patch over a fresh read
 * of the record when it actually runs, so neither another card's in-flight save nor another tab's
 * save is reverted. The server's answer goes straight into the cache.
 */
export function useSaveSettingsRecord<T extends object>(
  queryKey: QueryKey,
  path: string,
  onSaved?: () => void,
  { optimistic = false, merge = (current, patch) => ({ ...current, ...patch }) }: SettingsRecordOptions<T> = {},
) {
  const queryClient = useQueryClient()
  return useMutation({
    scope: { id: path },
    mutationFn: async (patch: Partial<T>) => {
      const current = await api<T>(path)
      return api<T>(path, { method: 'PUT', body: JSON.stringify(merge(current, patch)) })
    },
    onMutate: optimistic
      ? (patch) => {
          queryClient.setQueryData<T>(queryKey, (old) => (old ? merge(old, patch) : old))
        }
      : undefined,
    onError: optimistic ? () => void queryClient.invalidateQueries({ queryKey }) : undefined,
    onSuccess: (saved) => {
      // While later saves are queued the cache already shows their values; this answer would undo them.
      const queued = queryClient.isMutating({ predicate: (m) => m.options.scope?.id === path }) > 1
      if (!optimistic || !queued) queryClient.setQueryData(queryKey, saved)
      onSaved?.()
    },
  })
}
