import { useEffect, useState } from 'react'
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, apiBlob } from './client'
import type { QualityTierName } from './upgrades'

export type PageConversionFormat = 'webp' | 'avif'

/** Smallest keeps the least detail; Lossless re-encodes PNG pages losslessly and leaves JPEG pages as they are. */
export type PageConversionStop = 'smallest' | 'compact' | 'balanced' | 'high' | 'lossless'

export const PAGE_CONVERSION_STOPS: readonly PageConversionStop[] = ['smallest', 'compact', 'balanced', 'high', 'lossless']

export const DEFAULT_PAGE_CONVERSION_STOP: PageConversionStop = 'high'

export interface PageConversionSettings {
  /** Null while conversion is off. */
  format: PageConversionFormat | null
  stop: PageConversionStop
  /** PNG pages go to lossless WebP at every stop, as they did before stops existed. */
  pngLossless: boolean
}

export interface PageConversionSample {
  fileId: number
  chapterId: number
  seriesId: number
  seriesTitle: string
  chapterLabel: string
  /** Zero-based. */
  page: number
  pageCount: number
  reason: 'greyscale' | 'colour' | 'heaviest' | 'custom'
  tier: string
  fileSize: number
}

export interface PageConversionSamples {
  samples: PageConversionSample[]
}

export interface PageConversionPassTotals {
  startedAtUtc: string
  finishedAtUtc: string | null
  converted: number
  unchanged: number
  hardlinked: number
  failed: number
  savedBytes: number
}

export interface PageConversionStatus {
  settings: PageConversionSettings
  pass: {
    state: 'idle' | 'running' | 'paused'
    /** Why a pass paused itself rather than being paused. */
    hold: 'lowDisk' | 'conversionOff' | 'settingsChanged' | null
    currentFile: string | null
    /** The pass in progress, or the last one that finished. */
    totals: PageConversionPassTotals | null
  }
  library: {
    convertedFiles: number
    savedBytes: number
    pendingFiles: number
    trashBytes: number
    trashFiles: number
    trashDays: number
    /** How many rules decide which files are converted. */
    ruleCount: number
    /** Series whose own menu choice (Never or a fixed quality) wins over the rules. */
    overrideSeries: number
  }
}

const key = ['page-conversion']

export function usePageConversion() {
  return useQuery({
    queryKey: key,
    queryFn: () => api<PageConversionStatus>('/pageconversion'),
    refetchInterval: (query) => (query.state.data?.pass.state === 'running' ? 3000 : false),
  })
}

function useStatusMutation<T>(fn: (input: T) => Promise<PageConversionStatus>) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: fn,
    onSuccess: (status) => queryClient.setQueryData(key, status),
  })
}

export function useStartPageConversion() {
  return useStatusMutation(() => api<PageConversionStatus>('/pageconversion/start', { method: 'POST' }))
}

export function usePausePageConversion() {
  return useStatusMutation(() => api<PageConversionStatus>('/pageconversion/pause', { method: 'POST' }))
}

/** Every present field must match. A missing field matches anything. */
export interface PageConversionConditions {
  tiers?: QualityTierName[]
  /** ChapterFile source names, case-insensitive, with a trailing `*` as the only wildcard. */
  sources?: string[]
  /** jpg | png | mixed. */
  formats?: string[]
  seriesTypes?: string[]
  /** Any of the series' user tags, by label. */
  tags?: string[]
  bppUnder?: number
  bppOver?: number
  sizeOverBytes?: number
  sizeUnderBytes?: number
}

export interface PageConversionRule {
  /** Null on a rule that has not been saved yet. */
  id?: number | null
  name: string
  conditions: PageConversionConditions
  action: 'skip' | 'convert'
  /** Required when the action is convert, null for skip. */
  stop: PageConversionStop | null
}

export interface PageConversionRules {
  rules: PageConversionRule[]
}

export interface PageConversionScopeRequest {
  format: PageConversionFormat | null
  stop: PageConversionStop
  pngLossless: boolean
  rules: PageConversionRule[]
}

export interface ScopeBucket {
  files: number
  bytes: number
}

export interface PageConversionScope {
  /** One bucket per rule, in the order the request listed them. */
  rules: ScopeBucket[]
  /** Files no rule matched, which take the default quality. */
  default: ScopeBucket
  /** Files in series whose own choice decides. */
  overrides: ScopeBucket
  alreadyCompact: ScopeBucket
  convertedEarlier: ScopeBucket
}

export const MAX_PAGE_CONVERSION_RULES = 50

const rulesKey = [...key, 'rules']

export function usePageConversionRules() {
  return useQuery({
    queryKey: rulesKey,
    queryFn: () => api<PageConversionRules>('/pageconversion/rules'),
  })
}

export function useSavePageConversionPlan() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (plan: { settings: PageConversionSettings; rules: PageConversionRule[] }) =>
      api<PageConversionStatus>('/pageconversion/plan', {
        method: 'PUT',
        body: JSON.stringify(plan),
      }),
    onSuccess: (status) => {
      queryClient.setQueryData(key, status)
      void queryClient.invalidateQueries({ queryKey: rulesKey, exact: true })
    },
  })
}

export type PageConversionScopeResult = PageConversionScope & {
  /** The caller's identifiers for the request's rules, so the counts follow a rule that moved. */
  ruleKeys: string[]
}

/**
 * How many files and bytes each rule would decide, before anything is saved. The previous answer
 * stays in place while the next one loads. The caller debounces what it passes in.
 */
export function usePageConversionScope(request: PageConversionScopeRequest | null, ruleKeys: readonly string[]) {
  const body = JSON.stringify(request)
  return useQuery({
    queryKey: [...key, 'scope', body, ruleKeys.join(',')],
    queryFn: async (): Promise<PageConversionScopeResult> => ({
      ...(await api<PageConversionScope>('/pageconversion/scope', { method: 'POST', body })),
      ruleKeys: [...ruleKeys],
    }),
    enabled: request !== null,
    placeholderData: keepPreviousData,
    staleTime: 30_000,
    gcTime: 60_000,
    retry: false,
    meta: { silent: true },
  })
}

export function usePageConversionSamples() {
  return useQuery({
    queryKey: [...key, 'samples'],
    queryFn: () => api<PageConversionSamples>('/pageconversion/samples'),
    staleTime: 5 * 60_000,
  })
}

/** The suggested page of a file the admin chose themselves. A 404 means it is not a cbz or zip. */
export function usePageConversionSample(fileId: number | null) {
  return useQuery({
    queryKey: [...key, 'samples', fileId],
    queryFn: () => api<PageConversionSample>(`/pageconversion/samples/${fileId}`),
    enabled: fileId !== null,
    retry: false,
    meta: { silent: true },
  })
}

export interface PreviewRequest {
  fileId: number
  /** Zero-based. */
  page: number
  format: PageConversionFormat
  stop: PageConversionStop
  pngLossless: boolean
  /** The stored page as it is, ignoring format and stop. */
  original?: boolean
}

export interface PreviewPage {
  /** An object URL, revoked when the request changes or the caller unmounts. */
  url: string
  originalBytes: number
  encodedBytes: number
  /** The page was left as it is. */
  kept: boolean
  /** Why the page was kept, as the server names it. Null when it was not kept or the server gave no reason. */
  keptReason: string | null
}

function previewPath(r: PreviewRequest): string {
  const base = `/pageconversion/preview/${r.fileId}/${r.page}`
  if (r.original) return `${base}?original=true`
  return `${base}?format=${r.format}&stop=${r.stop}&pngLossless=${r.pngLossless}`
}

/**
 * One page, encoded the way the pass would encode it. The previous answer stays in place while the
 * next one loads, so moving the slider never blanks the image. The caller debounces what it passes in.
 */
export function usePreviewPage(request: PreviewRequest | null) {
  const path = request ? previewPath(request) : ''
  const query = useQuery({
    queryKey: [...key, 'preview', request?.fileId ?? 0, request?.page ?? 0, path],
    queryFn: async () => {
      const { blob, headers } = await apiBlob(path)
      const original = Number(headers.get('X-Maki-Original-Bytes') ?? blob.size)
      const encoded = Number(headers.get('X-Maki-Encoded-Bytes') ?? blob.size)
      return {
        blob,
        originalBytes: original,
        encodedBytes: encoded,
        kept: headers.get('X-Maki-Kept') === '1',
        keptReason: headers.get('X-Maki-Kept-Reason'),
      }
    },
    enabled: request !== null,
    // Only a change of format or stop keeps the old image up; another page starts from nothing, so
    // the wipe never lays one page's pixels over another's.
    placeholderData: (previous, previousQuery) =>
      previousQuery?.queryKey[2] === request?.fileId && previousQuery?.queryKey[3] === request?.page
        ? previous
        : undefined,
    staleTime: Infinity,
    gcTime: 60_000,
    retry: false,
    meta: { silent: true },
  })

  const answer = query.data
  const [data, setData] = useState<PreviewPage | undefined>(undefined)
  useEffect(() => {
    if (!answer) {
      setData(undefined)
      return
    }
    const url = URL.createObjectURL(answer.blob)
    setData({
      url,
      originalBytes: answer.originalBytes,
      encodedBytes: answer.encodedBytes,
      kept: answer.kept,
      keptReason: answer.keptReason,
    })
    return () => URL.revokeObjectURL(url)
  }, [answer])

  return { data, isFetching: query.isFetching, isPlaceholder: query.isPlaceholderData, isError: query.isError, error: query.error }
}
