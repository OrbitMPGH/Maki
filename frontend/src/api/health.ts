import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from './client'

export interface HealthCheck {
  id: string
  category: string
  status: string
  message: string
  url?: string
  checkedAt: string
  acknowledged: boolean
}

export interface HealthFinding {
  id: number
  fileId: number
  version: string
  kind: string
  severity: string
  message: string
  state: string
}

export interface HealthFile {
  id: number
  relativePath: string
  version: string
  rootFolderId: number
  seriesId?: number
  chapterFileId?: number
  size: number
  contentHash?: string
  status: string
  analyzedAt?: string
  findings: HealthFinding[]
  /** Only under the Duplicate filter: the row is the copy to keep, and this is the whole set. */
  copies?: DuplicateListCopy[]
  /** Only under the Unlinked filter: what this file needs, and for `outside` the folder it sits in. */
  detail?: { case: UnlinkedCase; folder?: string | null } | null
}

/** HealthMatchService.Cases: what an unlinked archive needs. */
export type UnlinkedCase = 'moved' | 'identical' | 'outside' | 'unrecognized' | 'noChapter' | 'rival' | 'import'

/**
 * How sure the match between a missing file and the file found at a new path is, strongest first.
 * Only `nameOnly` is held back from a bulk relink.
 */
export type MoveEvidence = 'identical' | 'samePages' | 'likely' | 'nameOnly'

export interface MoveRef {
  fileId: number
  version: string
  relativePath: string
  targetId: number
  targetVersion: string
  targetPath: string
  evidence: MoveEvidence
}

/** The Missing filter's answers: files found at a new path, and the rest grouped by folder. */
export interface MissingSummary {
  total: number
  /** Found by name alone; reviewed one at a time. */
  weak: number
  items: MoveRef[]
  /** The rows on this page that were found somewhere, keyed by file id. */
  rows: Record<string, { targetPath: string; evidence: MoveEvidence }>
  gone: { folder: string; count: number; items: FileRef[] }[]
}

export interface UnlinkedSummary {
  counts: { case: UnlinkedCase; count: number }[]
  outside: { folder: string; count: number }[]
  importable: { total: number; items: FileRef[] }
}

/** One copy of a set of byte-identical archives. `kept` backs chapters (or is the oldest when none does); `spare` is everything else still on disk. */
export interface DuplicateListCopy {
  id: number
  version: string
  relativePath: string
  kept: boolean
  spare: boolean
  missing: boolean
  findings: HealthFinding[]
}

export interface DuplicateCopy {
  id: number
  version: string
  relativePath: string
  size: number
  kept: boolean
  spare: boolean
  missing: boolean
  rootPath?: string
  chapters: { id: number; title?: string; number?: number }[]
}

export interface FileRef {
  id: number
  version: string
  relativePath: string
}

export interface HealthFilesPage {
  items: HealthFile[]
  total: number
  /** Only under the Duplicate filter: every spare copy the filter covers, capped at one bulk delete. */
  spares?: { total: number; items: FileRef[] }
  /** Only under the Missing filter. */
  moves?: MissingSummary
  /** Only under the Unlinked filter. */
  cases?: UnlinkedSummary
}

export interface HealthScan {
  id: number
  status: string
  completed: number
  total: number
  verify: boolean
  error?: string
}

export interface HealthOverview {
  checks: HealthCheck[]
  openFindings: number
  files: number
  scans: HealthScan[]
  roots: { id: number; path: string }[]
}

export interface Analysis {
  status: string
  hash?: string
  verified: boolean
  pages: { name: string; width: number; height: number }[]
  problems: { kind: string; severity: string; message: string }[]
}

export interface MatchChapter {
  id: number
  number?: number
  title?: string
  hasFile: boolean
}

/** `healthFileId` is null when the rival file has never been scanned, which is the one case where the two archives cannot be compared page by page. */
export interface MatchCounterpart {
  chapterFileId: number
  relativePath: string
  size: number
  sourceName: string
  healthFileId?: number
  version?: string
  status?: string
  pages: number
  pixelHeight: number
  contentHash?: string
  chapterIds: number[]
}

/**
 * What an unlinked archive would have linked to. Null on files that already back a chapter.
 * `labelKind` is a discriminator rather than rendered text: the page needs both a title-case badge
 * and a lower-case mid-sentence form, and lowercasing a translation is wrong for a language that
 * capitalizes the noun regardless of position. `number` is the chapter number or the volume's start.
 */
export interface UnlinkedMatch {
  recognized: boolean
  labelKind: 'chapter' | 'volume' | 'volumes' | 'unrecognized'
  number?: number
  volumeEnd?: number
  seriesId?: number
  seriesTitle?: string
  chapters: MatchChapter[]
  counterparts: MatchCounterpart[]
}

export interface FileDetail {
  file: HealthFile
  analysis: Analysis
  findings: HealthFinding[]
  chapters: { id: number; title?: string; number?: number; wanted: boolean }[]
  mappings: { id: number; sourceName: string; priority: number }[]
  match: UnlinkedMatch | null
  /** For a missing file: where it most likely went. */
  moved: {
    targetId: number
    targetVersion: string
    targetPath: string
    targetSize: number
    targetPages: number
    evidence: MoveEvidence
  } | null
  /** The chapter file record's own size and page count, for comparing with what is on disk. */
  recordSize: number | null
  recordPages: number | null
  /** Every byte-identical copy, this file included; empty when there is no other copy. */
  copies: DuplicateCopy[]
  /** Where this file's chapters move if it is deleted: an identical copy in the same series folder. */
  relinkTo: { id: number; relativePath: string } | null
}

export interface HealthOperation {
  id: number
  fileId: number
  kind: string
  status: string
  version: string
  error?: string
  createdAt: string
  finishedAt?: string | null
  /** What the operation acts on; the list endpoint fills these, the detail endpoint leaves them null. */
  relativePath?: string | null
  seriesId?: number | null
  seriesTitle?: string | null
  coverUrl?: string | null
}

/** One kind of operation that ended one way: the total, and the newest few rows. */
export interface OperationGroup {
  /** Opaque; pass to `/operations/group` to load the rest of the rows. */
  key: string
  kind: string
  status: string
  error?: string | null
  count: number
  rows: HealthOperation[]
}

export interface OperationDetail {
  operation: HealthOperation
  file: HealthFile
  chapters: { id: number; title?: string; number?: number; wanted: boolean }[]
  /** A deletion whose chapters move to this identical copy instead of losing their file. */
  relinkTo: { id: number; relativePath: string } | null
  candidates: { chapterId: number; analysis: Analysis }[]
  requiresReset: boolean
  /** Failed, and the file is still the version it failed on, so the same request can run again. */
  retryable: boolean
}

/** The series behind a grouped source warning, and how far a refresh of them has got. */
export interface SourceFailures {
  series: { id: number; seriesId: number; title: string; error: string; lastRefresh?: string }[]
  refreshing: boolean
  done: number
  total: number
}

export interface HealthOptions {
  warningPercent: number
  errorPercent: number
  warningGiB: number
  errorGiB: number
  backupDays: number
  timeZone: string | null
  scanHour: number
  automaticScanning: boolean
  scanWorkers: number
}

/** How often a query refetches: a fixed number of ms, false for never, or decided from the latest data. */
type HealthPoll<T> = number | false | ((data: T | undefined) => number | false)

/** No polling unless a caller asks: lists and detail views refetch when something invalidates them. */
export function useHealthData<T>(path = '', enabled = true, poll: HealthPoll<T> = false) {
  return useQuery({
    queryKey: ['health-workspace', path],
    queryFn: () => api<T>(`/health${path}`),
    enabled,
    refetchInterval: typeof poll === 'function' ? (query) => poll(query.state.data as T | undefined) : poll,
    refetchIntervalInBackground: false,
  })
}

/** Failures are shown inline by whichever page or dialog owns the mutation, so no global toast. */
export function useHealthAction() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ path, body = {}, method = 'POST' }: { path: string; body?: unknown; method?: string }) =>
      api<HealthOperation>(`/health${path}`, { method, body: JSON.stringify(body) }),
    meta: { silent: true },
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ['health-workspace'] })
      void qc.invalidateQueries({ queryKey: ['health'] })
    },
  })
}
