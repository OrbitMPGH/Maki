import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from './client'
import type { AutoTagCondition, AutoTagField } from './autoTagRules'

/** What a root folder rule may test: every auto-tag field but the root folder it decides. */
export const ROOT_FOLDER_RULE_FIELDS: AutoTagField[] = ['genre', 'type', 'contentRating', 'addedVia', 'addedBy']

export interface RootFolderRule {
  id: number
  rootFolderId: number
  position: number
  enabled: boolean
  groups: AutoTagCondition[][]
}

export interface SaveRootFolderRule {
  rootFolderId: number
  enabled: boolean
  groups: AutoTagCondition[][]
}

/** Where the rules send a series; both null when no rule matched. */
export interface RootFolderRoute {
  rootFolderId: number | null
  ruleId: number | null
}

const KEY = ['root-folder-rules']

export function useRootFolderRules(enabled = true) {
  return useQuery({ queryKey: KEY, queryFn: () => api<RootFolderRule[]>('/rootfolder/rules'), enabled })
}

export function useSaveRootFolderRule() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ id, rule }: { id: number | null; rule: SaveRootFolderRule }) =>
      api<RootFolderRule>(id == null ? '/rootfolder/rules' : `/rootfolder/rules/${id}`, {
        method: id == null ? 'POST' : 'PUT',
        body: JSON.stringify(rule),
      }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: KEY }),
  })
}

export function useDeleteRootFolderRule() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => api<void>(`/rootfolder/rules/${id}`, { method: 'DELETE' }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: KEY }),
  })
}

export function useReorderRootFolderRules() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (ids: number[]) =>
      api<RootFolderRule[]>('/rootfolder/rules/order', { method: 'PUT', body: JSON.stringify({ ids }) }),
    onSuccess: (rules) => qc.setQueryData(KEY, rules),
  })
}

/** Which rule would route a library series where. */
export function useRootFolderRuleTest(seriesId: number | null) {
  return useQuery({
    queryKey: [...KEY, 'test', seriesId],
    queryFn: () => api<RootFolderRoute>(`/rootfolder/rules/test?seriesId=${seriesId}`),
    enabled: seriesId != null,
    staleTime: 0,
  })
}

/**
 * The folder the rules pick for a series about to be added, for the add form to preselect.
 * `addedBy` is the requester when an admin approves a request.
 */
export function useRootFolderRoute(
  providerId: string | null | undefined,
  addedFrom: 'library' | 'recommendation' | 'request',
  addedBy?: number | null,
) {
  return useQuery({
    queryKey: [...KEY, 'route', providerId, addedFrom, addedBy ?? null],
    queryFn: () => {
      const params = new URLSearchParams({ providerId: providerId!, addedFrom })
      if (addedBy != null) params.set('addedBy', String(addedBy))
      return api<RootFolderRoute>(`/rootfolder/route?${params}`)
    },
    enabled: !!providerId,
    staleTime: 60_000,
  })
}
