import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from './client'
import type { CatalogueCredit, DiscoverFeedRequest } from './hooks'

/** The creators and studios the signed-in user follows. Mirrors `FollowedCreatorsSpec`. */
export interface FollowedCreators {
  creators?: CatalogueCredit[] | null
}

const KEY = ['discover-following']

export function useFollowedCreators(enabled = true) {
  return useQuery({
    queryKey: KEY,
    queryFn: () => api<FollowedCreators>('/recommendations/discover/following'),
    enabled,
    staleTime: 60 * 60 * 1000,
  })
}

export function useSaveFollowedCreators() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (creators: CatalogueCredit[]) =>
      api<FollowedCreators>('/recommendations/discover/following', {
        method: 'PUT',
        body: JSON.stringify({ creators }),
      }),
    onSuccess: (saved) => queryClient.setQueryData(KEY, saved),
  })
}

export function sameCredit(a: CatalogueCredit, b: CatalogueCredit) {
  return (a.role ?? null) === (b.role ?? null) && a.name.trim().toLowerCase() === b.name.trim().toLowerCase()
}

/** Follow state for one creator, and the switch that flips it. */
export function useFollowToggle(credit: CatalogueCredit | null) {
  const { data } = useFollowedCreators(credit != null)
  const save = useSaveFollowedCreators()
  const creators = data?.creators ?? []
  const following = credit != null && creators.some((c) => sameCredit(c, credit))
  const toggle = () => {
    if (!credit) return
    save.mutate(following ? creators.filter((c) => !sameCredit(c, credit)) : [...creators, credit])
  }
  return { following, toggle, pending: save.isPending, ready: data !== undefined }
}

export const FOLLOWING_RAIL_KEY = 'following'
export const FOLLOWING_RAIL_SIZE = 40
/** How far back a release still counts as new. Older work is the back catalogue the creator page lists. */
export const FOLLOWING_RAIL_MONTHS = 12

/**
 * The follow rail as a catalogue browse: followed creators' releases from the last
 * {@link FOLLOWING_RAIL_MONTHS} months, newest first, minus anything the reader already owns or has
 * given an opinion on. Null when they follow nobody.
 */
export function followingFeedRequest(
  creators: CatalogueCredit[] | null | undefined,
  limit = FOLLOWING_RAIL_SIZE,
): DiscoverFeedRequest | null {
  if (!creators?.length) return null
  return {
    feed: 'Popular',
    filters: { credits: creators },
    sort: 'newest',
    excludeInteracted: true,
    releasedWithinMonths: FOLLOWING_RAIL_MONTHS,
    limit,
  }
}
