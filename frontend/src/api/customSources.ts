import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, api } from './client'

/** A definition as stored. Its shape is documented in docs/custom-sources-plan.md; the editor treats it as JSON. */
export type CustomSourceDefinition = Record<string, unknown> & { id?: string; name?: string }

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
