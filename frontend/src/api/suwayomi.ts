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

export interface SuwayomiSourceRow {
  id: string
  /** The name Maki knows it by, `suwayomi-{id}`. */
  sourceName: string
  name: string
  displayName: string
  lang: string
  contentWarning: string | null
  isNsfw: boolean
  /** Mihon's `all`/`multi`: the admin has to say which language its chapters are in. */
  mixedLanguage: boolean
  /** Null when Suwayomi could not be asked; false when it no longer has the source. */
  installed: boolean | null
  exposed: boolean
  chapterLanguage: string | null
  /** Series linked to it, so removing it can say what stops syncing. */
  mappings: number
}

export interface SuwayomiSources {
  reachable: boolean
  /** Why Suwayomi could not be asked, already localized. */
  error: string | null
  sources: SuwayomiSourceRow[]
}

export interface SuwayomiSourceChoice {
  id: string
  chapterLanguage: string | null
}

const connectionKey = ['settings', 'suwayomi'] as const
const sourcesKey = ['settings', 'suwayomi', 'sources'] as const

export function useSuwayomiConnection() {
  return useQuery({
    queryKey: connectionKey,
    queryFn: () => api<SuwayomiConnection>('/settings/suwayomi'),
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
      void queryClient.invalidateQueries({ queryKey: sourcesKey })
    },
  })
}

export function useTestSuwayomiConnection() {
  return useMutation({
    mutationFn: (value: SuwayomiConnectionInput) =>
      api<SuwayomiTestResult>('/settings/suwayomi/test', { method: 'POST', body: JSON.stringify(value) }),
  })
}

/** Reaches Suwayomi, so it only runs while the picker is open. */
export function useSuwayomiSources(enabled: boolean) {
  return useQuery({
    queryKey: sourcesKey,
    queryFn: () => api<SuwayomiSources>('/settings/suwayomi/sources'),
    enabled,
    staleTime: 0,
  })
}

export function useSaveSuwayomiSources() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (sources: SuwayomiSourceChoice[]) =>
      api<SuwayomiSources>('/settings/suwayomi/sources', { method: 'PUT', body: JSON.stringify({ sources }) }),
    onSuccess: (saved) => {
      queryClient.setQueryData(sourcesKey, saved)
      invalidateSources(queryClient)
    },
  })
}
