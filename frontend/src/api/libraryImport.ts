import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { api } from './client'

/** Why a file stayed out of the series (`Maki.Api.Services.ImportSkipReason`). */
export const IMPORT_SKIP_REASONS: Record<string, MessageDescriptor> = {
  unreadable: msg`could not be read, the archive is corrupt or truncated`,
  noMatchingChapter: msg`no chapter with this number in the series`,
  unrecognized: msg`no chapter or volume number in the name`,
  duplicate: msg`a second copy of another comic in the folder, which is used instead`,
  leftBehind: msg`stays in the folder, the series folder already has a file with this name`,
}

export interface ImportRequestItem {
  folderName: string
  metadataProviderId: string
}

/** One file of an import preview (`LibraryImportPlanFile`). */
export interface ImportPlanFile {
  /** Relative to the folder the files end up in. */
  name: string
  /** The archive or folder it comes from, relative to the scanned folder. */
  source: string
  entry: string | null
  kind: 'cbz' | 'zip' | 'repack' | 'looseImages' | 'pdf'
  action: 'register' | 'build' | 'rebuildInPlace' | 'useExisting'
  aside: string | null
  size: number
  /** Read off the name, as the parser read it. Not reformatted: it is an identifier. */
  number: string | null
  volume: number | null
  volumeEnd: number | null
  /** Chapters it is expected to link to; null when linking waits for the source match. */
  chapters: string[] | null
  unlinked: string | null
  loneFile: boolean
}

/** What importing one folder would do (`LibraryImportPlan`). */
export interface ImportPlan {
  folderName: string
  error: string | null
  seriesTitle: string | null
  existingSeriesId: number | null
  folderAction: 'keep' | 'rename' | 'merge'
  targetFolderName: string | null
  seriesFolderName: string | null
  linkDeferred: boolean
  files: ImportPlanFile[] | null
  skipped: { name: string; reason: string }[] | null
  writesCover: boolean
  replacesCover: boolean
}

/** Must not exceed LibraryImportController.MaxItemsPerRequest. */
export const IMPORT_BATCH_SIZE = 50

export function useImportPlan(rootFolderId: number, items: ImportRequestItem[]) {
  return useQuery({
    queryKey: ['libraryimport', 'plan', rootFolderId, items],
    queryFn: async () => {
      const plans: ImportPlan[] = []
      for (let i = 0; i < items.length; i += IMPORT_BATCH_SIZE) {
        plans.push(
          ...(await api<ImportPlan[]>('/libraryimport/plan', {
            method: 'POST',
            body: JSON.stringify({ rootFolderId, items: items.slice(i, i + IMPORT_BATCH_SIZE) }),
          })),
        )
      }
      return plans
    },
    staleTime: 0,
    gcTime: 0,
  })
}

/** A folder dismissed from the library import of one root folder. */
export interface IgnoredImportFolder {
  id: number
  folderName: string
  createdAt: string
}

const ignoredKey = (rootFolderId: number | null) => ['libraryimport', 'ignored', rootFolderId] as const

export function useIgnoredImportFolders(rootFolderId: number | null) {
  return useQuery({
    queryKey: ignoredKey(rootFolderId),
    queryFn: () => api<IgnoredImportFolder[]>(`/libraryimport/ignored?rootFolderId=${rootFolderId}`),
    enabled: rootFolderId !== null,
  })
}

export function useIgnoreImportFolder() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (body: { rootFolderId: number; folderName: string }) =>
      api<void>('/libraryimport/ignored', { method: 'POST', body: JSON.stringify(body) }),
    onSuccess: (_data, body) => queryClient.invalidateQueries({ queryKey: ignoredKey(body.rootFolderId) }),
  })
}

export function useUnignoreImportFolder(rootFolderId: number | null) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => api<void>(`/libraryimport/ignored/${id}`, { method: 'DELETE' }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ignoredKey(rootFolderId) }),
  })
}

/** One folder of an import run, as recorded for undo (`ImportBatchFolderDto`). */
export interface ImportBatchFolder {
  id: number
  originalFolderName: string
  folderName: string
  seriesId: number | null
  seriesTitle: string
  createdSeries: boolean
  folderAction: 'keep' | 'rename' | 'merge'
  fileCount: number
  builtCount: number
  undoneAt: string | null
  /** Still waiting on its source match or file link; undo cancels that. */
  linkPending: boolean
  seriesGone: boolean
}

export interface ImportBatch {
  batchId: string
  rootFolderId: number
  createdAt: string
  folders: ImportBatchFolder[]
}

export interface ImportUndoOutcome {
  id: number
  folderName: string
  undone: boolean
  error: string | null
  warnings: string[] | null
}

const batchesKey = ['libraryimport', 'batches'] as const

export function useImportBatches(rootFolderId: number | null) {
  return useQuery({
    queryKey: [...batchesKey, rootFolderId],
    queryFn: () =>
      api<ImportBatch[]>(`/libraryimport/batches${rootFolderId === null ? '' : `?rootFolderId=${rootFolderId}`}`),
  })
}

export function useUndoImport() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (target: { batchId: string } | { folderId: number }) =>
      'batchId' in target
        ? api<ImportUndoOutcome[]>(`/libraryimport/batches/${encodeURIComponent(target.batchId)}/undo`, {
            method: 'POST',
          })
        : [await api<ImportUndoOutcome>(`/libraryimport/batches/folders/${target.folderId}/undo`, { method: 'POST' })],
    onSettled: () => queryClient.invalidateQueries({ queryKey: batchesKey }),
  })
}
