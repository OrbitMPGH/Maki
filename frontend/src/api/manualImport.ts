import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from './client'
import type { ImportDecision, ImportDecisionResultDto, ImportPlanChapterDto, ImportPlanFileDto } from './types'

export interface ManualImportFolder {
  name: string
  path: string
}

export interface ManualImportFile {
  name: string
  path: string
  size: number
  chapter: string | null
  volume: number | null
  volumeEnd: number | null
}

/** `parent` is "" for the top list and null when there is nothing above. */
export interface ManualImportListing {
  path: string
  parent: string | null
  folders: ManualImportFolder[]
  files: ManualImportFile[]
}

export interface ManualImportPlanDto {
  parsingFingerprint: string
  path: string
  seriesId: number
  seriesTitle: string
  releaseName: string
  files: ImportPlanFileDto[]
  error: string | null
  hasConflicts: boolean
  newChapterCount: number
  replacedFileCount: number
  chapters: ImportPlanChapterDto[]
}

/** Admins browse the whole host; everyone else starts from their own root folders. */
export function useManualImportBrowse(path: string | null) {
  return useQuery({
    queryKey: ['manualimport', 'browse', path ?? ''],
    queryFn: () =>
      api<ManualImportListing>(`/manualimport/browse${path ? `?path=${encodeURIComponent(path)}` : ''}`),
    placeholderData: keepPreviousData,
  })
}

export function useManualImportPlan(path: string | null, seriesId: number | null) {
  return useQuery({
    queryKey: ['manualimport', 'plan', path, seriesId],
    queryFn: () =>
      api<ManualImportPlanDto>('/manualimport/plan', {
        method: 'POST',
        body: JSON.stringify({ path, seriesId }),
      }),
    enabled: path !== null && seriesId !== null,
  })
}

export function useManualImport() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (body: {
      path: string
      seriesId: number
      mode: Exclude<ImportDecision, 'Reject'>
      skipFiles?: string[]
      move: boolean
      parsingFingerprint?: string
    }) => api<ImportDecisionResultDto>('/manualimport/import', { method: 'POST', body: JSON.stringify(body) }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['manualimport'] })
      void queryClient.invalidateQueries({ queryKey: ['queue-history'] })
      void queryClient.invalidateQueries({ queryKey: ['series'] })
    },
  })
}
