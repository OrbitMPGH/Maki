import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, api } from './client'

/** A definition as stored. Its shape is documented in docs/custom-sources-plan.md; sections are edited as JSON. */
export type CustomSourceDefinition = Record<string, unknown> & {
  id?: string
  name?: string
  baseUrl?: string
  language?: string
  kind?: string
  content?: string[]
  rating?: string
  flareSolverr?: boolean
  minIntervalMs?: number
  engine?: string | null
  engineOptions?: { seriesPath?: string; chapterList?: string } | null
  series?: Section & { url?: string }
  chapters?: Section
  pages?: Section
  search?: Section | null
  catalogue?: Section | null
  sample?: { seriesUrl?: string; chapterUrl?: string; query?: string | null }
}

export type Section = Record<string, unknown>

/** Drops nulls, which the server answers with for every unset field, so stored and exported JSON stays short. */
export function compact<T>(value: T): T {
  if (Array.isArray(value)) return value.map(compact) as T
  if (value && typeof value === 'object') {
    return Object.fromEntries(
      Object.entries(value as Record<string, unknown>).filter(([, v]) => v !== null && v !== undefined).map(([k, v]) => [k, compact(v)]),
    ) as T
  }
  return value
}

export interface CustomSourceEntry {
  id: string | null
  sourceName: string | null
  name: string | null
  /** False when this build can't read it; `problem` says why. */
  valid: boolean
  problem: string | null
  linkedSeries: number
  definition: CustomSourceDefinition
}

export interface CustomSourceProblem {
  code: string
  error: string
}

export type CustomSourceStepName = 'search' | 'series' | 'chapters' | 'pages' | 'image'

export interface CustomSourceTestStep {
  name: CustomSourceStepName
  status: 'ok' | 'failed' | 'skipped'
  /** Already worded: Maki's own reason in the reader's language, or what the site said. */
  error: string | null
  detail: Record<string, unknown> | null
}

export interface CustomSourceTestReport {
  passed: boolean
  steps: CustomSourceTestStep[]
}

/** Why a save could re-key the chapters of linked series. */
export type IdentityReason = 'language' | 'host' | 'chapters' | 'imageHosts' | 'unverified'

export interface IdentityChange {
  code: 'error.customSource.identityChange'
  error: string
  reasons: IdentityReason[]
  linkedSeries: number
  changes: { before: string | null; after: string | null }[]
}

const listKey = ['custom-sources'] as const

export function useCustomSources(enabled = true) {
  return useQuery({
    queryKey: listKey,
    queryFn: () => api<CustomSourceEntry[]>('/custom-sources'),
    enabled,
  })
}

/** The problems a 400 from the custom source endpoints listed, or null for any other failure. */
export function problemsOf(error: unknown): CustomSourceProblem[] | null {
  if (!(error instanceof ApiError) || error.status !== 400) return null
  const problems = (error.payload as { problems?: CustomSourceProblem[] } | null)?.problems
  return Array.isArray(problems) ? problems : null
}

/** The identity check's findings when a save answered 409, or null. */
export function identityChangeOf(error: unknown): IdentityChange | null {
  if (!(error instanceof ApiError) || error.status !== 409) return null
  const payload = error.payload as IdentityChange | null
  return payload?.code === 'error.customSource.identityChange' ? payload : null
}

export function useTestCustomSource() {
  return useMutation({
    mutationFn: (definition: CustomSourceDefinition) =>
      api<CustomSourceTestReport>('/custom-sources/test', { method: 'POST', body: JSON.stringify(definition) }),
  })
}

/** Saving reloads the source registry server-side, so every cached view of the sources goes stale. */
function invalidate(queryClient: ReturnType<typeof useQueryClient>) {
  void queryClient.invalidateQueries({ queryKey: listKey })
  void queryClient.invalidateQueries({ queryKey: ['sources'] })
  void queryClient.invalidateQueries({ queryKey: ['settings', 'sources'] })
}

export function useSaveCustomSource() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ definition, confirm }: { definition: CustomSourceDefinition; confirm: boolean }) =>
      api<{ created: boolean; sourceName: string }>(
        `/custom-sources/${encodeURIComponent(definition.id ?? '')}${confirm ? '?confirm=true' : ''}`,
        { method: 'PUT', body: JSON.stringify(definition) },
      ),
    onSuccess: () => invalidate(queryClient),
  })
}

export function useDeleteCustomSource() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (id: string) => api<void>(`/custom-sources/${encodeURIComponent(id)}`, { method: 'DELETE' }),
    onSuccess: () => invalidate(queryClient),
  })
}

export function fetchCustomSourceExport(id: string) {
  return api<CustomSourceDefinition>(`/custom-sources/${encodeURIComponent(id)}`)
}

export interface ProbeChapterRow {
  id: string | null
  text: string | null
  number: number | null
  date: string | null
  link: string | null
}

export interface ProbeChapterGuess {
  chapters: Section
  count: number
  numbered: number
  /** The list holds the chapter address the admin pasted. */
  fromSample: boolean
  rows: ProbeChapterRow[]
}

export interface ProbeSiteResult {
  draft: CustomSourceDefinition
  viaFlareSolverr: boolean
  title: string | null
  seriesId: string
  engine: { engine: string; options: { seriesPath: string; chapterList: string } } | null
  chapters: ProbeChapterGuess[]
  pages: {
    pages: Section
    count: number
    preview: string[]
    image: { ok: boolean; format: string | null; bytes: number; error: string | null }
  } | null
  /** Why the chapter page couldn't be read, in the site's own words. */
  chapterError: string | null
  /** A source that already reads this site; `custom` is its custom id when it is one. */
  hostTaken: { name: string; displayName: string; custom: string | null } | null
}

export interface ProbeSearchResult {
  search: Section | null
  catalogue: Section | null
  results: number
  titles: string[]
  tried: { url: string; found: boolean; error: string | null }[]
}

export function useProbeSite() {
  return useMutation({
    mutationFn: (body: { seriesUrl: string; chapterUrl: string | null }) =>
      api<ProbeSiteResult>('/custom-sources/probe', { method: 'POST', body: JSON.stringify(body) }),
  })
}

export function useProbeSearch() {
  return useMutation({
    mutationFn: (body: { baseUrl: string; seriesTemplate: string; seriesUrl: string; query: string }) =>
      api<ProbeSearchResult>('/custom-sources/probe/search', { method: 'POST', body: JSON.stringify(body) }),
  })
}

export type RepositoryEntryState = 'available' | 'installed' | 'updateAvailable' | 'detached' | 'idTaken' | 'needsNewerMaki'

export interface CustomRepositoryEntry {
  id: string
  name: string
  language: string
  version: number
  state: RepositoryEntryState
  installedVersion: number | null
}

export interface CustomRepository {
  url: string
  name: string
  autoUpdate: boolean
  /** Served over https, which "Keep up to date" needs. */
  secure: boolean
  lastCheckedUtc: string | null
  /** Already worded: the last refresh's failure, or why an update waits. */
  lastError: string | null
  definitions: CustomRepositoryEntry[]
}

export interface CustomSourceUpdatePreview {
  id: string
  fromVersion: number
  toVersion: number
  current: Record<string, unknown>
  next: Record<string, unknown>
  changedFields: string[]
}

const repositoriesKey = ['custom-sources', 'repositories'] as const

export function useCustomRepositories(enabled = true) {
  return useQuery({
    queryKey: repositoriesKey,
    queryFn: () => api<CustomRepository[]>('/custom-sources/repositories'),
    enabled,
  })
}

function useRepositoryMutation<T, R>(fn: (body: T) => Promise<R>) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: fn,
    onSuccess: () => {
      invalidate(queryClient)
      void queryClient.invalidateQueries({ queryKey: repositoriesKey })
    },
  })
}

export const useAddRepository = () =>
  useRepositoryMutation((url: string) =>
    api<CustomRepository>('/custom-sources/repositories', { method: 'POST', body: JSON.stringify({ url }) }))

export const useRemoveRepository = () =>
  useRepositoryMutation((url: string) =>
    api<{ detached: number }>(`/custom-sources/repositories?url=${encodeURIComponent(url)}`, { method: 'DELETE' }))

export const useSetRepositoryAutoUpdate = () =>
  useRepositoryMutation((body: { url: string; autoUpdate: boolean }) =>
    api<void>('/custom-sources/repositories/autoupdate', { method: 'PUT', body: JSON.stringify(body) }))

export const useRefreshRepositories = () =>
  useRepositoryMutation((url: string | null) =>
    api<CustomRepository[]>('/custom-sources/repositories/refresh', { method: 'POST', body: JSON.stringify({ url }) }))

export const useInstallFromRepository = () =>
  useRepositoryMutation(({ url, id, confirm }: { url: string; id: string; confirm: boolean }) =>
    api<{ created: boolean; sourceName: string }>(
      `/custom-sources/repositories/install${confirm ? '?confirm=true' : ''}`,
      { method: 'POST', body: JSON.stringify({ url, id }) },
    ))

export const useApplyCustomSourceUpdate = () =>
  useRepositoryMutation(({ id, confirm }: { id: string; confirm: boolean }) =>
    api<{ created: boolean; sourceName: string }>(
      `/custom-sources/${encodeURIComponent(id)}/update${confirm ? '?confirm=true' : ''}`, { method: 'POST' }))

export const useRevertCustomSource = () =>
  useRepositoryMutation(({ id, confirm }: { id: string; confirm: boolean }) =>
    api<{ created: boolean; sourceName: string }>(
      `/custom-sources/${encodeURIComponent(id)}/revert${confirm ? '?confirm=true' : ''}`, { method: 'POST' }))

export function useCustomSourceUpdatePreview(id: string | null) {
  return useQuery({
    queryKey: ['custom-sources', 'update', id],
    queryFn: () => api<CustomSourceUpdatePreview>(`/custom-sources/${encodeURIComponent(id ?? '')}/update`),
    enabled: id !== null,
    retry: false,
  })
}

/** The step results a 400 from an install carried when the definition failed its test. */
export function failedStepsOf(error: unknown): CustomSourceTestStep[] | null {
  if (!(error instanceof ApiError) || error.status !== 400) return null
  const steps = (error.payload as { steps?: CustomSourceTestStep[] } | null)?.steps
  return Array.isArray(steps) ? steps : null
}
