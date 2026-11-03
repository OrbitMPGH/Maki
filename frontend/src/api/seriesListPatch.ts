import type { QueryClient } from '@tanstack/react-query'
import { api } from './client'
import { SERIES_LOOKUP_KEY, type SeriesLookupRow } from './seriesLookup'
import type { SeriesDto } from './types'

const LIST_KEY = ['series'] as const
// Past this many series in one flush, one full refetch is cheaper than the patch.
const MAX_PATCH = 50
// A patch only covers the series an event named, so while events keep arriving the whole list is
// still refetched this often, which also picks up anything that changed without an event.
const FULL_EVERY_MS = 30_000

/**
 * Keeps the cached library list current through a download burst without rebuilding it on the
 * server per flush: the series the events named are fetched from `GET series/summary` (the list's
 * own rows) and swapped into the cached list. Their detail queries are refetched as before.
 */
export function createSeriesListRefresher(queryClient: QueryClient) {
  const pending = new Set<number>()
  // Patch number that last wrote each series, so a slower older response never overwrites a newer one.
  const written = new Map<number, number>()
  let seq = 0
  return {
    mark(seriesId: number) {
      pending.add(seriesId)
    },
    flush() {
      const ids = [...pending]
      pending.clear()
      if (ids.length === 0) return
      for (const id of ids) void queryClient.invalidateQueries({ queryKey: ['series', id] })
      // An import can bring a series the lookup has never seen; nothing else in it moves on an import.
      const lookup = queryClient.getQueryData<SeriesLookupRow[]>(SERIES_LOOKUP_KEY)
      if (lookup && ids.some((id) => !lookup.some((row) => row.id === id))) {
        void queryClient.invalidateQueries({ queryKey: SERIES_LOOKUP_KEY })
      }
      void patchList(queryClient, ids, ++seq, written)
    },
  }
}

async function patchList(queryClient: QueryClient, ids: number[], mine: number, written: Map<number, number>) {
  const query = queryClient.getQueryCache().find<SeriesDto[]>({ queryKey: LIST_KEY, exact: true })
  const refetchAll = () => void queryClient.invalidateQueries({ queryKey: LIST_KEY, exact: true })
  if (
    !query?.state.data ||
    query.getObserversCount() === 0 ||
    query.state.fetchStatus === 'fetching' ||
    ids.length > MAX_PATCH ||
    Date.now() - query.state.dataUpdatedAt >= FULL_EVERY_MS
  ) {
    refetchAll()
    return
  }

  const startedFrom = query.state.dataUpdatedAt
  let rows: SeriesDto[]
  try {
    rows = await api<SeriesDto[]>(`/series/summary?${ids.map((id) => `ids=${id}`).join('&')}`)
  } catch {
    refetchAll()
    return
  }
  // A full fetch that landed or started meanwhile may predate these rows or postdate them; refetch
  // rather than guess which.
  const now = queryClient.getQueryState(LIST_KEY)
  if (now?.dataUpdatedAt !== startedFrom || now.fetchStatus === 'fetching') {
    refetchAll()
    return
  }
  const ours = ids.filter((id) => (written.get(id) ?? 0) < mine)
  for (const id of ours) written.set(id, mine)
  // Keeps the time of the last full fetch, which is what FULL_EVERY_MS and staleTime measure from.
  queryClient.setQueryData<SeriesDto[]>(LIST_KEY, (list) => list && mergeRows(list, ours, rows.filter((row) => ours.includes(row.id))), {
    updatedAt: startedFrom,
  })
}

/**
 * `list` with the rows for `ids` replaced by `rows`. A requested id missing from `rows` is gone (deleted
 * or no longer visible) and leaves the list; a row the list lacks goes in at its title, the order the
 * server sends the list in.
 */
export function mergeRows(list: SeriesDto[], ids: number[], rows: SeriesDto[]): SeriesDto[] {
  const fresh = new Map(rows.map((row) => [row.id, row]))
  const asked = new Set(ids)
  const next: SeriesDto[] = []
  for (const s of list) {
    if (!asked.has(s.id)) next.push(s)
    else {
      const row = fresh.get(s.id)
      if (row) next.push(row)
      fresh.delete(s.id)
    }
  }
  for (const row of fresh.values()) {
    const at = next.findIndex((s) => s.sortTitle > row.sortTitle)
    next.splice(at === -1 ? next.length : at, 0, row)
  }
  return next
}
