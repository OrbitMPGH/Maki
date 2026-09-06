import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from './client'

export interface HealthCheck { id: string; category: string; status: string; message: string; url?: string; checkedAt: string; acknowledged: boolean }
export interface HealthFinding { id: number; fileId: number; version: string; kind: string; severity: string; message: string; state: string }
export interface HealthFile { id: number; relativePath: string; version: string; rootFolderId: number; seriesId?: number; chapterFileId?: number; size: number; contentHash?: string; status: string; analyzedAt?: string; findings: HealthFinding[] }
export interface HealthScan { id: number; status: string; completed: number; total: number; error?: string }
export interface HealthOverview { checks: HealthCheck[]; openFindings: number; files: number; scans: HealthScan[]; roots: {id: number; path: string}[] }
/** A set of pages that are the same picture. One entry per set, never one per pair. */
export interface PageGroup { kind: 'blank' | 'exact' | 'similar'; pages: number[]; distance: number }
export interface Analysis { status: string; hash?: string; pages: {name: string; width: number; height: number}[]; problems: {kind: string; severity: string; message: string}[]; groups: PageGroup[] }
export interface MatchChapter { id: number; number?: number; title?: string; hasFile: boolean }
/** `healthFileId` is null when the rival file has never been scanned, which is the one case where the two archives cannot be compared page by page. */
export interface MatchCounterpart { chapterFileId: number; relativePath: string; size: number; sourceName: string; healthFileId?: number; version?: string; status?: string; pages: number; pixelHeight: number; contentHash?: string; chapterIds: number[] }
/** What an unlinked archive would have linked to. Null on files that already back a chapter. */
export interface UnlinkedMatch { recognized: boolean; label: string; seriesId?: number; seriesTitle?: string; chapters: MatchChapter[]; counterparts: MatchCounterpart[] }
export interface FileDetail { file: HealthFile; analysis: Analysis; findings: HealthFinding[]; chapters: {id: number; title?: string; number?: number; wanted: boolean}[]; mappings: {id: number; sourceName: string}[]; match: UnlinkedMatch | null }
export interface HealthOperation { id: number; fileId: number; kind: string; status: string; version: string; error?: string; createdAt: string }
export interface OperationDetail { operation: HealthOperation; file: HealthFile; chapters: {id: number; title?: string; wanted: boolean}[]; candidates: {chapterId: number; analysis: Analysis}[]; requiresReset: boolean }
export interface HealthOptions { warningPercent: number; errorPercent: number; warningGiB: number; errorGiB: number; backupDays: number; timeZone: string | null; scanHour: number; automaticScanning: boolean }
export function useHealthData<T>(path = '', enabled = true) {
  return useQuery({ queryKey: ['health-workspace', path], queryFn: () => api<T>(`/health${path}`), enabled, refetchInterval: 5000, refetchIntervalInBackground: false })
}
export function useHealthAction() {
  const qc = useQueryClient()
  return useMutation({ mutationFn: ({path, body = {}, method = 'POST'}: {path: string; body?: unknown; method?: string}) => api<HealthOperation>(`/health${path}`, {method, body: JSON.stringify(body)}), onSuccess: () => { void qc.invalidateQueries({queryKey: ['health-workspace']}); void qc.invalidateQueries({queryKey: ['health']}) } })
}
