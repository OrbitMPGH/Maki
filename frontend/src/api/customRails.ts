import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { api } from './client'
import type { DiscoverRail, RecommendationFilters, RecommendationItem, RecommendationSeed, SearchDefaults } from './hooks'

export type CustomRailSource = 'library' | 'recommendations' | 'catalogue'
export type CustomRailPlacement = 'home' | 'discover'
export type CustomRailSort = 'added' | 'read' | 'title' | 'popular' | 'rating' | 'newest' | 'oldest'

/**
 * A custom rail's saved settings. Mirrors `CustomRailSpec` on the server; never rename a field, the
 * stored blob is read case-insensitively and a mismatch silently falls back to the default.
 */
export interface CustomRailSpec {
  source: CustomRailSource
  filters?: SearchDefaults | null
  sort?: CustomRailSort | null
  /** Catalogue only. */
  excludeOwned?: boolean
  /** Recommendations only. Empty means the whole library, like the Recommended tab. */
  seeds?: RecommendationSeed[] | null
  obscurity?: number
  diversity?: number
}

export interface CustomRail {
  id: number
  /** Typed by the user. Data, never translated. */
  name: string
  placement: CustomRailPlacement
  spec: CustomRailSpec
  sortOrder: number
}

export interface CustomRailItems {
  source: CustomRailSource
  items: RecommendationItem[]
  /** Library rails: the matching series' ids, in order. */
  seriesIds: number[]
  /** `catalogue` when the local MangaBaka database cannot answer. */
  unavailable?: string | null
}

export const RAIL_SORTS: Record<CustomRailSource, CustomRailSort[]> = {
  library: ['added', 'read', 'title', 'popular'],
  catalogue: ['popular', 'rating', 'newest', 'oldest'],
  recommendations: [],
}

/** Descriptors, rendered with `useLabel()`. */
export const RAIL_SORT_LABELS: Record<CustomRailSort, MessageDescriptor> = {
  added: msg`Recently added`,
  read: msg`Recently read`,
  title: msg`Title`,
  popular: msg`Most popular`,
  rating: msg`Top rated`,
  newest: msg`Newest`,
  oldest: msg`Oldest`,
}

export const RAIL_SOURCE_LABELS: Record<CustomRailSource, MessageDescriptor> = {
  library: msg`Your library`,
  recommendations: msg`Recommendations`,
  catalogue: msg`Catalogue`,
}

/** Key prefix of a custom rail handed to Discover's "Show more" view, so it can load the rail's filters. */
export const CUSTOM_RAIL_PREFIX = 'rail:'

/** The Discover-side "Show more" for a catalogue rail: the rail as the expand view expects it. */
export function customRailAsDiscoverRail(rail: CustomRail, items: RecommendationItem[]): DiscoverRail {
  return {
    key: `${CUSTOM_RAIL_PREFIX}${rail.id}`,
    title: rail.name,
    feed: 'Popular',
    genre: null,
    items,
    filters: specFilters(rail.spec.filters),
    sort: (rail.spec.sort as DiscoverRail['sort']) ?? 'popular',
    excludeOwned: rail.spec.excludeOwned ?? false,
  }
}

/** A stored filter spec as wire filters, nulls dropped. Same rules as `filtersFromSpec`. */
function specFilters(spec: SearchDefaults | null | undefined): RecommendationFilters {
  return Object.fromEntries(
    Object.entries(spec ?? {}).filter(([, v]) => v != null && !(Array.isArray(v) && v.length === 0)),
  ) as RecommendationFilters
}

export function useCustomRails(placement?: CustomRailPlacement) {
  return useQuery({
    queryKey: ['custom-rails', placement ?? 'all'],
    queryFn: () => api<CustomRail[]>(`/rails${placement ? `?placement=${placement}` : ''}`),
    staleTime: 60 * 60 * 1000,
  })
}

function useInvalidateRails() {
  const queryClient = useQueryClient()
  return () => {
    void queryClient.invalidateQueries({ queryKey: ['custom-rails'] })
    void queryClient.invalidateQueries({ queryKey: ['custom-rail-items'] })
    // The Home layout lists Home rails by key, and the server merges them in on read.
    void queryClient.invalidateQueries({ queryKey: ['settings', 'ui'] })
  }
}

export interface SaveCustomRail {
  name: string
  placement: CustomRailPlacement
  spec: CustomRailSpec
}

/**
 * Puts a rail into (or takes it out of) the cached lists straight away, so a page layout being
 * edited sees it before the refetch lands.
 */
function usePatchRailLists() {
  const queryClient = useQueryClient()
  return (change: (rails: CustomRail[], placement: string) => CustomRail[]) => {
    for (const placement of ['all', 'home', 'discover']) {
      queryClient.setQueryData<CustomRail[]>(['custom-rails', placement], (rails) =>
        rails ? change(rails, placement) : rails,
      )
    }
  }
}

export function useCreateCustomRail() {
  const invalidate = useInvalidateRails()
  const patch = usePatchRailLists()
  return useMutation({
    mutationFn: (body: SaveCustomRail) =>
      api<CustomRail>('/rails', { method: 'POST', body: JSON.stringify(body) }),
    onSuccess: (rail) => {
      patch((rails, placement) =>
        placement === 'all' || placement === rail.placement ? [...rails, rail] : rails,
      )
      invalidate()
    },
  })
}

export function useUpdateCustomRail() {
  const invalidate = useInvalidateRails()
  return useMutation({
    mutationFn: ({ id, ...body }: Partial<SaveCustomRail> & { id: number }) =>
      api<CustomRail>(`/rails/${id}`, { method: 'PUT', body: JSON.stringify(body) }),
    onSuccess: invalidate,
  })
}

export function useDeleteCustomRail() {
  const invalidate = useInvalidateRails()
  const patch = usePatchRailLists()
  return useMutation({
    mutationFn: (id: number) => api<void>(`/rails/${id}`, { method: 'DELETE' }),
    onSuccess: (_result, id) => {
      patch((rails) => rails.filter((r) => r.id !== id))
      invalidate()
    },
  })
}

export function useCustomRailItems(id: number, limit: number, enabled = true) {
  return useQuery({
    queryKey: ['custom-rail-items', id, limit],
    queryFn: () => api<CustomRailItems>(`/rails/${id}/items?limit=${limit}`),
    enabled,
    staleTime: 60 * 1000,
    retry: false,
    meta: { silent: true },
  })
}

/** Live match count for the editor. Null while unknown, including when the search index is off. */
export function useCustomRailCount(spec: CustomRailSpec | null) {
  return useQuery({
    queryKey: ['custom-rail-count', spec],
    queryFn: () =>
      api<{ count: number | null }>('/rails/count', {
        method: 'POST',
        body: JSON.stringify({ spec }),
      }).then((r) => r.count),
    enabled: spec != null,
    staleTime: 60 * 1000,
    retry: false,
    meta: { silent: true },
  })
}
