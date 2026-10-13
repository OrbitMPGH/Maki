import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from './client'

export type SuwayomiAuthMode = 'none' | 'basic' | 'uilogin'

/** The password never comes back; `passwordSet` says whether one is stored. */
export interface SuwayomiConnection {
  url: string | null
  authMode: SuwayomiAuthMode
  username: string | null
  passwordSet: boolean
}

/** On save, a null password leaves the stored one alone and an empty string clears it. */
export interface SuwayomiConnectionInput {
  url: string | null
  authMode: SuwayomiAuthMode
  username: string | null
  password: string | null
}

export interface SuwayomiTestResult {
  success: boolean
  version: string
  buildType: string | null
  sourceCount: number
}

export interface SuwayomiRefreshResult {
  reachable: boolean
  /** Why Suwayomi could not be asked, already localized. The stored list is left as it was. */
  error: string | null
  installed: number
}

const connectionKey = ['settings', 'suwayomi'] as const

export function useSuwayomiConnection(enabled = true) {
  return useQuery({
    queryKey: connectionKey,
    queryFn: () => api<SuwayomiConnection>('/settings/suwayomi'),
    enabled,
  })
}

/** Saving reloads the source list server-side, so every cached view of the sources goes stale. */
function invalidateSources(queryClient: ReturnType<typeof useQueryClient>) {
  void queryClient.invalidateQueries({ queryKey: ['sources'] })
  void queryClient.invalidateQueries({ queryKey: ['settings', 'sources'] })
}

export function useSaveSuwayomiConnection() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (value: SuwayomiConnectionInput) =>
      api<SuwayomiConnection>('/settings/suwayomi', { method: 'PUT', body: JSON.stringify(value) }),
    onSuccess: (saved) => {
      queryClient.setQueryData(connectionKey, saved)
      invalidateSources(queryClient)
    },
  })
}

export function useTestSuwayomiConnection() {
  return useMutation({
    mutationFn: (value: SuwayomiConnectionInput) =>
      api<SuwayomiTestResult>('/settings/suwayomi/test', { method: 'POST', body: JSON.stringify(value) }),
  })
}

/** Reads Suwayomi's installed sources into Maki's list, where new ones arrive switched off. */
export function useRefreshSuwayomiSources() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: () => api<SuwayomiRefreshResult>('/settings/suwayomi/sources/refresh', { method: 'POST' }),
    onSuccess: () => invalidateSources(queryClient),
  })
}

/** The one language a mixed-language source's chapters are filed under. */
export function useSetSuwayomiSourceLanguage() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ id, chapterLanguage }: { id: string; chapterLanguage: string }) =>
      api<void>(`/settings/suwayomi/sources/${encodeURIComponent(id)}`, {
        method: 'PUT',
        body: JSON.stringify({ chapterLanguage }),
      }),
    onSuccess: () => invalidateSources(queryClient),
  })
}
