import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from './client'

export interface HealthCheck { id: string; category: string; status: string; message: string; url?: string; checkedAt: string; acknowledged: boolean }
export interface HealthFinding { id: number; fileId: number; version: string; kind: string; severity: string; message: string; state: string }
export interface HealthFile { id: number; relativePath: string; version: string; rootFolderId: number; seriesId?: number; chapterFileId?: number; size: number; contentHash?: string; status: string; analyzedAt?: string; findings: HealthFinding[] }
export interface HealthScan { id: number; status: string; completed: number; total: number; verify: boolean; error?: string }
export interface HealthOverview { checks: HealthCheck[]; openFindings: number; files: number; scans: HealthScan[]; roots: {id: number; path: string}[] }
export interface Analysis { status: string; hash?: string; verified: boolean; pages: {name: string; width: number; height: number}[]; problems: {kind: string; severity: string; message: string}[] }
export interface MatchChapter { id: number; number?: number; title?: string; hasFile: boolean }
/** `healthFileId` is null when the rival file has never been scanned, which is the one case where the two archives cannot be compared page by page. */
export interface MatchCounterpart { chapterFileId: number; relativePath: string; size: number; sourceName: string; healthFileId?: number; version?: string; status?: string; pages: number; pixelHeight: number; contentHash?: string; chapterIds: number[] }
/**
 * What an unlinked archive would have linked to. Null on files that already back a chapter.
 * `labelKind` is a discriminator rather than rendered text: the page needs both a title-case badge
 * and a lower-case mid-sentence form, and lowercasing a translation is wrong for a language that
 * capitalizes the noun regardless of position. `number` is the chapter number or the volume's start.
 */
export interface UnlinkedMatch { recognized: boolean; labelKind: 'chapter' | 'volume' | 'volumes' | 'unrecognized'; number?: number; volumeEnd?: number; seriesId?: number; seriesTitle?: string; chapters: MatchChapter[]; counterparts: MatchCounterpart[] }
export interface FileDetail { file: HealthFile; analysis: Analysis; findings: HealthFinding[]; chapters: {id: number; title?: string; number?: number; wanted: boolean}[]; mappings: {id: number; sourceName: string; priority: number}[]; match: UnlinkedMatch | null }
export interface HealthOperation { id: number; fileId: number; kind: string; status: string; version: string; error?: string; createdAt: string }
export interface OperationDetail { operation: HealthOperation; file: HealthFile; chapters: {id: number; title?: string; wanted: boolean}[]; candidates: {chapterId: number; analysis: Analysis}[]; requiresReset: boolean }
/** The series behind a grouped source warning, and how far a refresh of them has got. */
export interface SourceFailures { series: {id: number; seriesId: number; title: string; error: string; lastRefresh?: string}[]; refreshing: boolean; done: number; total: number }
export interface HealthOptions { warningPercent: number; errorPercent: number; warningGiB: number; errorGiB: number; backupDays: number; timeZone: string | null; scanHour: number; automaticScanning: boolean; scanWorkers: number }
export function useHealthData<T>(path = '', enabled = true) {
  return useQuery({ queryKey: ['health-workspace', path], queryFn: () => api<T>(`/health${path}`), enabled, refetchInterval: 5000, refetchIntervalInBackground: false })
}
export function useHealthAction() {
  const qc = useQueryClient()
  return useMutation({ mutationFn: ({path, body = {}, method = 'POST'}: {path: string; body?: unknown; method?: string}) => api<HealthOperation>(`/health${path}`, {method, body: JSON.stringify(body)}), onSuccess: () => { void qc.invalidateQueries({queryKey: ['health-workspace']}); void qc.invalidateQueries({queryKey: ['health']}) } })
}
