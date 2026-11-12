import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from './client'
import type { TorrentProposalDto } from './upgrades'

/** Stall handling and the seed targets, stored with the download client. */
export interface TorrentLifecycleSettings {
  /** Hours without progress, or without a connected seed or peer, before a torrent fails. 0 is off. */
  stallHours: number
  /** Seed ratio target for removal after seeding. Null is no ratio target. */
  seedRatio: number | null
  /** Seed time target in minutes for removal after seeding. Null is no time target. */
  seedMinutes: number | null
}

export interface SeedLimitImport {
  seedRatio: number | null
  seedMinutes: number | null
  /** Whether the client had any seed limit switched on. */
  found: boolean
}

export const MAX_STALL_HOURS = 168
export const MAX_SEED_RATIO = 1000
export const MAX_SEED_MINUTES = 525_600

const LIFECYCLE_KEY = ['settings', 'downloadclient', 'lifecycle'] as const

export function useTorrentLifecycle() {
  return useQuery({
    queryKey: LIFECYCLE_KEY,
    queryFn: () => api<TorrentLifecycleSettings>('/settings/downloadclient/lifecycle'),
  })
}

export function useSaveTorrentLifecycle() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (value: TorrentLifecycleSettings) =>
      api<TorrentLifecycleSettings>('/settings/downloadclient/lifecycle', { method: 'PUT', body: JSON.stringify(value) }),
    onSuccess: (saved) => queryClient.setQueryData(LIFECYCLE_KEY, saved),
  })
}

/** Replaces the seed targets with the download client's own default limits. */
export function useImportSeedLimits() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: () => api<SeedLimitImport>('/settings/downloadclient/lifecycle/import', { method: 'POST' }),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: LIFECYCLE_KEY }),
  })
}

/** Releases the automatic search will not take: dismissed proposals and torrents that stalled, vanished or were removed. */
export function useDeclinedReleases(enabled = true) {
  return useQuery({
    queryKey: ['upgrades', 'proposals', 'dismissed'],
    queryFn: () => api<TorrentProposalDto[]>('/upgrades/proposals?status=dismissed'),
    enabled,
  })
}

export function useUnblockRelease() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => api<void>(`/upgrades/proposals/${id}/unblock`, { method: 'POST' }),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['upgrades', 'proposals'] }),
  })
}
