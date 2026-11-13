import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { api } from './client'

/** Mirrors `AutoTagFields` on the server. Persisted in each rule, so append only. */
export type AutoTagField = 'genre' | 'type' | 'contentRating' | 'addedVia' | 'addedBy' | 'rootFolder'

/** Mirrors `AutoTagAddedVia` on the server. Persisted, append only. */
export type AutoTagAddedVia = 'manual' | 'discover' | 'request' | 'importList' | 'mihon' | 'libraryImport'

export const AUTO_TAG_FIELDS: AutoTagField[] = ['genre', 'type', 'contentRating', 'addedVia', 'addedBy', 'rootFolder']

export const AUTO_TAG_ADDED_VIA: AutoTagAddedVia[] = [
  'manual',
  'discover',
  'request',
  'importList',
  'mihon',
  'libraryImport',
]

/** Descriptors, not strings: render with `useLabel()`, so a language switch reaches them. */
export const AUTO_TAG_FIELD_LABELS: Record<AutoTagField, MessageDescriptor> = {
  genre: msg`Genre`,
  type: msg`Type`,
  contentRating: msg`Content rating`,
  addedVia: msg`How it was added`,
  addedBy: msg`Added by`,
  rootFolder: msg`Root folder`,
}

export const AUTO_TAG_ADDED_VIA_LABELS: Record<AutoTagAddedVia, MessageDescriptor> = {
  manual: msg`Search or the add page`,
  discover: msg`Discover`,
  request: msg`Approved request`,
  importList: msg`Import list`,
  mihon: msg`Mihon import`,
  libraryImport: msg`Library import`,
}

export interface AutoTagCondition {
  field: AutoTagField
  value: string
}

export interface AutoTagRule {
  id: number
  tagId: number
  enabled: boolean
  /** The first group alone, kept for older clients. */
  conditions: AutoTagCondition[]
  /** Alternatives: the rule matches when every condition of any one group holds. */
  groups: AutoTagCondition[][]
}

export interface SaveAutoTagRule {
  tagId: number
  enabled: boolean
  groups: AutoTagCondition[][]
}

const KEY = ['auto-tag-rules']

export function useAutoTagRules(enabled = true) {
  return useQuery({ queryKey: KEY, queryFn: () => api<AutoTagRule[]>('/tags/rules'), enabled })
}

export function useSaveAutoTagRule() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ id, rule }: { id: number | null; rule: SaveAutoTagRule }) =>
      api<AutoTagRule>(id == null ? '/tags/rules' : `/tags/rules/${id}`, {
        method: id == null ? 'POST' : 'PUT',
        body: JSON.stringify(rule),
      }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: KEY }),
  })
}

export function useDeleteAutoTagRule() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => api<void>(`/tags/rules/${id}`, { method: 'DELETE' }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: KEY }),
  })
}

/** How many existing series "Apply to existing series" would tag. Fetched when the confirm opens. */
export function useAutoTagRulePreview(id: number | null) {
  return useQuery({
    queryKey: [...KEY, 'preview', id],
    queryFn: () => api<{ count: number }>(`/tags/rules/${id}/preview`),
    enabled: id != null,
    staleTime: 0,
    gcTime: 0,
  })
}

export function useApplyAutoTagRule() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => api<{ tagged: number }>(`/tags/rules/${id}/apply`, { method: 'POST' }),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ['tags'] })
      void qc.invalidateQueries({ queryKey: ['series'] })
    },
  })
}
