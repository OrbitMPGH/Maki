import { useMutation, useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query'
import { api } from './client'
import { CUSTOM_RAIL_PREFIX } from './customRails'
import { DISCOVER_SECTIONS, HOME_SECTIONS, type DiscoverRail, type HomeSectionKey, type RecommendationItem } from './hooks'

/**
 * Where a title was saved from. `savedFrom` is one of `rail:{section key}`, `rail:{custom rail id}`,
 * `search`, `catalogue`, `creator`, `series`, `anime`, `importlist` or `other`; the detail carries
 * what the label needs and the key cannot (a creator's name, a custom rail's title).
 */
export interface ShortlistSource {
  savedFrom: string
  savedFromDetail?: string | null
}

export const OTHER_SOURCE: ShortlistSource = { savedFrom: 'other' }
export const SEARCH_SOURCE: ShortlistSource = { savedFrom: 'search' }
export const CATALOGUE_SOURCE: ShortlistSource = { savedFrom: 'catalogue' }
export const SERIES_SOURCE: ShortlistSource = { savedFrom: 'series' }
export const ANIME_SOURCE: ShortlistSource = { savedFrom: 'anime' }

const SECTION_SOURCES = new Map<string, ShortlistSource>(
  DISCOVER_SECTIONS.map((key) => [key, { savedFrom: `rail:${key}` }]),
)

/** The stable source for one Discover section, so memoized cards do not see a new object per render. */
export function sectionSource(key: string): ShortlistSource {
  return SECTION_SOURCES.get(key) ?? OTHER_SOURCE
}

const HOME_SOURCES = new Map<string, ShortlistSource>(HOME_SECTIONS.map((key) => [key, { savedFrom: `rail:${key}` }]))

/** The stable source for one Home section. */
export function homeSectionSource(key: HomeSectionKey): ShortlistSource {
  return HOME_SOURCES.get(key) ?? OTHER_SOURCE
}

/** The source for a Discover rail, which is also what its "Show more" view saves from. */
export function railSource(rail: Pick<DiscoverRail, 'key' | 'title' | 'genre'>): ShortlistSource {
  if ((DISCOVER_SECTIONS as readonly string[]).includes(rail.key)) return sectionSource(rail.key)
  if (rail.key.startsWith(CUSTOM_RAIL_PREFIX)) return { savedFrom: rail.key, savedFromDetail: rail.title }
  if (rail.genre) return sectionSource('genres')
  return OTHER_SOURCE
}

export interface ShortlistStatusChange {
  from: string
  to: string
}

export interface ShortlistLibraryState {
  seriesId: number | null
  archived: boolean
}

export interface ShortlistEntry {
  /** The stored key. Row-level writes address this; cards and the modal use `item.providerId`. */
  mangaBakaId: number
  /** The title as it was saved. */
  title: string
  /** Null when the catalogue no longer has the title. */
  item: RecommendationItem | null
  /** The catalogue type (`manga`, `manhwa`, ...), set with `item`. */
  type: string | null
  hasAnime: boolean
  note: string | null
  savedFrom: string
  savedFromDetail: string | null
  createdAtUtc: string
  statusChange: ShortlistStatusChange | null
  newChapters: number | null
  library: ShortlistLibraryState | null
}

export interface ShortlistList {
  /** False before the catalogue is downloaded; `entries` is then empty. */
  catalogueAvailable: boolean
  entries: ShortlistEntry[]
}

export interface ShortlistIdsResponse {
  /** Every visible entry's id, a merged entry under both its stored and its canonical id. */
  ids: number[]
  /** Visible entries, a merged one counted once. */
  count: number
}

/** What DELETE returns, and what restore takes back. */
export interface RemovedShortlistEntry {
  mangaBakaId: number
  title: string
  note: string | null
  savedFrom: string
  savedFromDetail: string | null
  savedStatus: string | null
  savedTotalChapters: number | null
  createdAtUtc: string
}

const IDS_KEY = ['shortlist-ids'] as const
const LIST_KEY = ['shortlist'] as const

const fetchIds = () => api<ShortlistIdsResponse>('/shortlist/ids')
const toCount = (data: ShortlistIdsResponse) => data.count

/** How many titles are on the list, for the tab count. */
export function useShortlistCount() {
  return useQuery({
    queryKey: IDS_KEY,
    queryFn: fetchIds,
    staleTime: 10 * 60 * 1000,
    select: toCount,
  })
}

/** One boolean per card, so a save re-renders the card that changed and not every card on the page. */
export function useIsShortlisted(providerId: string): boolean {
  const id = Number(providerId)
  const { data } = useQuery({
    queryKey: IDS_KEY,
    queryFn: fetchIds,
    staleTime: 10 * 60 * 1000,
    select: (data) => data.ids.includes(id),
  })
  return data ?? false
}

export function useShortlist(enabled = true) {
  return useQuery({
    queryKey: LIST_KEY,
    queryFn: () => api<ShortlistList>('/shortlist'),
    enabled,
  })
}

function patchList(client: QueryClient, update: (entries: ShortlistEntry[]) => ShortlistEntry[]) {
  client.setQueryData<ShortlistList>(LIST_KEY, (list) => (list ? { ...list, entries: update(list.entries) } : list))
}

function invalidateShortlist(client: QueryClient) {
  void client.invalidateQueries({ queryKey: IDS_KEY })
  void client.invalidateQueries({ queryKey: LIST_KEY })
}

function patchIds(client: QueryClient, id: number, present: boolean) {
  client.setQueryData<ShortlistIdsResponse>(IDS_KEY, (data) => {
    const ids = data?.ids ?? []
    const count = data?.count ?? 0
    const had = ids.includes(id)
    if (had === present) return data ?? { ids, count }
    return present
      ? { ids: [...ids, id], count: count + 1 }
      : { ids: ids.filter((x) => x !== id), count: Math.max(0, count - 1) }
  })
}

export function useSaveToShortlist() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: ({ id, source, note }: { id: number; source: ShortlistSource; note?: string }) =>
      api<void>(`/shortlist/${id}`, {
        method: 'PUT',
        body: JSON.stringify({
          savedFrom: source.savedFrom,
          savedFromDetail: source.savedFromDetail ?? undefined,
          note: note || undefined,
        }),
      }),
    onMutate: async ({ id }) => {
      await client.cancelQueries({ queryKey: IDS_KEY })
      const wasSaved = client.getQueryData<ShortlistIdsResponse>(IDS_KEY)?.ids.includes(id) ?? false
      patchIds(client, id, true)
      return { wasSaved }
    },
    onError: (_error, { id }, context) => patchIds(client, id, context?.wasSaved ?? false),
    onSettled: () => invalidateShortlist(client),
  })
}

export function useUpdateShortlistNote() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: ({ id, note }: { id: number; note: string }) =>
      api<void>(`/shortlist/${id}`, { method: 'PATCH', body: JSON.stringify({ note }) }),
    onMutate: ({ id, note }) => {
      patchList(client, (entries) =>
        entries.map((e) => (e.mangaBakaId === id || e.item?.providerId === String(id) ? { ...e, note: note.trim() || null } : e)),
      )
    },
    onSettled: () => void client.invalidateQueries({ queryKey: LIST_KEY }),
  })
}

export function useAcknowledgeShortlistChange() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: ({ id }: { id: number }) => api<void>(`/shortlist/${id}/acknowledge`, { method: 'POST' }),
    onSettled: () => invalidateShortlist(client),
  })
}

/** Resolves to the removed entry, or undefined when there was none. Hand it to {@link useRestoreShortlistEntry} to undo. */
export function useRemoveFromShortlist() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: ({ id }: { id: number }) =>
      api<RemovedShortlistEntry | undefined>(`/shortlist/${id}`, { method: 'DELETE' }),
    onMutate: async ({ id }) => {
      await client.cancelQueries({ queryKey: IDS_KEY })
      await client.cancelQueries({ queryKey: LIST_KEY })
      patchIds(client, id, false)
      patchList(client, (entries) => entries.filter((e) => e.mangaBakaId !== id && e.item?.providerId !== String(id)))
    },
    onError: (_error, { id }) => patchIds(client, id, true),
    onSettled: () => invalidateShortlist(client),
  })
}

export function useRestoreShortlistEntry() {
  const client = useQueryClient()
  return useMutation({
    mutationFn: (entry: RemovedShortlistEntry) =>
      api<void>(`/shortlist/${entry.mangaBakaId}/restore`, { method: 'POST', body: JSON.stringify(entry) }),
    onMutate: async (entry) => {
      await client.cancelQueries({ queryKey: IDS_KEY })
      patchIds(client, entry.mangaBakaId, true)
    },
    onError: (_error, entry) => patchIds(client, entry.mangaBakaId, false),
    onSettled: () => invalidateShortlist(client),
  })
}
