import type { QueryClient, QueryKey } from '@tanstack/react-query'

export interface OptimisticPatch {
  key: QueryKey
  /** Only called when the key holds data; an empty cache is left empty. */
  update: (old: never) => unknown
  /** False for a heavy list that only needs a refetch when the request failed. */
  refetchOnSuccess?: boolean
}

const inFlight = new Map<string, number>()
const flightKey = (key: QueryKey) => JSON.stringify(key)

type Snapshot = { previous: [QueryKey, unknown][] }

/**
 * `onMutate`/`onError` for a mutation whose result the client already knows, so a switch or select
 * shows the new value at once instead of snapping back until the request returns. The listed keys
 * get the expected value before the request goes out and their old value back if it fails. The
 * error toast comes from the global MutationCache, so a failed save still says so.
 */
export function optimistic<TVars>(queryClient: QueryClient, patches: (vars: TVars) => OptimisticPatch[]) {
  return {
    onMutate: async (vars: TVars): Promise<Snapshot> => {
      const list = patches(vars)
      for (const p of list) inFlight.set(flightKey(p.key), (inFlight.get(flightKey(p.key)) ?? 0) + 1)
      await Promise.all(list.map((p) => queryClient.cancelQueries({ queryKey: p.key, exact: true })))
      const previous = list.map((p): [QueryKey, unknown] => [p.key, queryClient.getQueryData(p.key)])
      for (const p of list) {
        queryClient.setQueryData(p.key, (old: unknown) => (old === undefined ? old : p.update(old as never)))
      }
      return { previous }
    },
    onError: (_err: unknown, _vars: TVars, snapshot: Snapshot | undefined) => {
      for (const [key, data] of snapshot?.previous ?? []) queryClient.setQueryData(key, data)
    },
    onSettled: (_data: unknown, err: unknown, vars: TVars) => {
      for (const p of patches(vars)) {
        const k = flightKey(p.key)
        const left = (inFlight.get(k) ?? 1) - 1
        if (left > 0) inFlight.set(k, left)
        else inFlight.delete(k)
        if (left === 0 && (err || p.refetchOnSuccess !== false)) {
          void queryClient.invalidateQueries({ queryKey: p.key, exact: true })
        }
      }
    },
  }
}

/**
 * {@link optimistic} for one settings object: the fields the save sends are merged into its cache.
 * `seededBySave` is for a save whose response is written to the cache, so a refetch would only replace it.
 */
export function optimisticMerge<TVars, TData extends object>(
  queryClient: QueryClient,
  key: QueryKey,
  toPatch: (vars: TVars) => Partial<TData>,
  { seededBySave = false }: { seededBySave?: boolean } = {},
) {
  return optimistic<TVars>(queryClient, (vars) => [
    { key, refetchOnSuccess: !seededBySave, update: (old: TData) => ({ ...old, ...toPatch(vars) }) },
  ])
}

/** {@link optimistic} for one series: its detail query and its row in the cached library list. */
export function optimisticSeries<TVars extends { seriesId: number }, TSeries extends { id: number }>(
  queryClient: QueryClient,
  toPatch: (vars: TVars) => Partial<TSeries>,
) {
  return optimistic<TVars>(queryClient, (vars) => {
    const patch = toPatch(vars)
    return [
      { key: ['series', vars.seriesId], update: (old: TSeries) => ({ ...old, ...patch }) },
      {
        key: ['series'],
        refetchOnSuccess: false,
        update: (old: TSeries[]) => old.map((s) => (s.id === vars.seriesId ? { ...s, ...patch } : s)),
      },
    ]
  })
}
