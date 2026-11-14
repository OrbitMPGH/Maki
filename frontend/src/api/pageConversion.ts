import { useEffect, useState } from 'react'
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, apiBlob } from './client'
import type { QualityTierName } from './upgrades'

export type PageConversionFormat = 'webp' | 'avif' | 'jxl'

/** Smallest keeps the least detail; Lossless re-encodes PNG pages losslessly and leaves JPEG pages as they are. */
export type PageConversionStop = 'smallest' | 'compact' | 'balanced' | 'high' | 'lossless'

export const PAGE_CONVERSION_STOPS: readonly PageConversionStop[] = ['smallest', 'compact', 'balanced', 'high', 'lossless']

export const DEFAULT_PAGE_CONVERSION_STOP: PageConversionStop = 'high'

/** Pixels. The server refuses a split height outside MIN to MAX. */
export const DEFAULT_SPLIT_HEIGHT = 5000
export const MIN_SPLIT_HEIGHT = 1000
export const MAX_SPLIT_HEIGHT = 13000

export interface PageConversionSettings {
  /** Black-and-white pages, and colour pages while no colour target is set. Null while conversion is off. */
  format: PageConversionFormat | null
  stop: PageConversionStop
  /** PNG pages go to lossless WebP at every stop, as they did before stops existed. */
  pngLossless: boolean
  /** Strips taller than this many pixels are cut into several pages. Null or absent leaves them whole. */
  splitHeight?: number | null
  /** Colour pages' format. Null or absent, with `colourStop`, converts them like black-and-white pages. */
  colourFormat?: PageConversionFormat | null
  /** Colour pages' stop. `lossless` needs no format. */
  colourStop?: PageConversionStop | null
}

/** One kind of page's format and stop. A Lossless target is lossless WebP whatever the format says. */
export interface PageTarget {
  format: PageConversionFormat
  stop: PageConversionStop
}

/** The colour pages' own target, or null when they convert like black-and-white pages. */
export function colourTargetOf(value: {
  format?: PageConversionFormat | null
  stop: PageConversionStop | null
  colourFormat?: PageConversionFormat | null
  colourStop?: PageConversionStop | null
}): PageTarget | null {
  if (!value.colourStop) return null
  const colour: PageTarget = { format: value.colourFormat ?? 'webp', stop: value.colourStop }
  const grey = value.format && value.stop ? { format: value.format, stop: value.stop } : null
  return grey && sameTarget(colour, grey) ? null : colour
}

export function sameTarget(a: PageTarget, b: PageTarget): boolean {
  return a.stop === b.stop && (a.stop === 'lossless' || a.format === b.format)
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
  reason: 'greyscale' | 'colour' | 'heaviest' | 'custom' | 'tall'
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
    /** New downloads and torrent imports waiting for the background queue. */
    queuedFiles: number
    trashBytes: number
    trashFiles: number
    trashDays: number
    /** How many rules decide which files are converted. */
    ruleCount: number
    /** Series whose own menu choice (Never or a fixed quality) wins over the rules. */
    overrideSeries: number
  }
  /** Pages converted at once, across the library pass and downloads. */
  parallel: {
    /** Null uses `default`. */
    limit: number | null
    default: number
    /** The server's core count. */
    max: number
  }
}

const key = ['page-conversion']

export function usePageConversion() {
  return useQuery({
    queryKey: key,
    queryFn: () => api<PageConversionStatus>('/pageconversion'),
    refetchInterval: (query) =>
      query.state.data?.pass.state === 'running' ? 3000 : query.state.data?.library.queuedFiles ? 10_000 : false,
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

export function useSavePageConversionParallel() {
  return useStatusMutation((limit: number | null) =>
    api<PageConversionStatus>('/pageconversion/parallel', { method: 'PUT', body: JSON.stringify({ limit }) }),
  )
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
  /** Black-and-white pages' stop. Required when the action is convert, null for skip. */
  stop: PageConversionStop | null
  /** Black-and-white pages' format. Null or absent takes the plan's. */
  format?: PageConversionFormat | null
  /** Colour pages' format. Null or absent, with `colourStop`, converts them like black-and-white pages. */
  colourFormat?: PageConversionFormat | null
  colourStop?: PageConversionStop | null
}

export interface PageConversionRules {
  rules: PageConversionRule[]
}

export interface PageConversionScopeRequest {
  format: PageConversionFormat | null
  stop: PageConversionStop
  pngLossless: boolean
  rules: PageConversionRule[]
  splitHeight?: number | null
  colourFormat?: PageConversionFormat | null
  colourStop?: PageConversionStop | null
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

export type SampleKind = 'greyscale' | 'colour'

/** Pages of one kind from the files one row of an unsaved plan converts. Row -1 is everything else. */
export interface RowSamplesRequest {
  plan: PageConversionScopeRequest
  row: number
  kind: SampleKind
}

export function useRowSamples(request: RowSamplesRequest | null) {
  const body = JSON.stringify(request)
  return useQuery({
    queryKey: [...key, 'samples', 'row', body],
    queryFn: () => api<PageConversionSamples>('/pageconversion/samples/row', { method: 'POST', body }),
    enabled: request !== null,
    staleTime: 5 * 60_000,
    retry: false,
    meta: { silent: true },
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

/** Where a split would cut one page: the library's tallest, or the one asked for. */
export interface PageSplitPreview {
  sample: PageConversionSample
  width: number
  height: number
  /** Rows the page is cut at, top to bottom. Empty when it stays whole. */
  cuts: number[]
}

/** The tallest page the server finds and where a split at `height` would cut it. A 404 means there is no archive to look at. */
export function usePageSplitPreview(height: number | null) {
  return useQuery({
    queryKey: [...key, 'split', height],
    queryFn: () => api<PageSplitPreview>(`/pageconversion/split?height=${height}`),
    enabled: height !== null,
    placeholderData: keepPreviousData,
    staleTime: 5 * 60_000,
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
  /** The colour pages' own target, when they convert differently. */
  colour?: PageTarget | null
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
  /** The page is in colour, so it takes the colour pages' target. */
  colour: boolean
}

function previewPath(r: PreviewRequest): string {
  const base = `/pageconversion/preview/${r.fileId}/${r.page}`
  if (r.original) return `${base}?original=true`
  const colour = r.colour ? `&colourFormat=${r.colour.format}&colourStop=${r.colour.stop}` : ''
  return `${base}?format=${r.format}&stop=${r.stop}&pngLossless=${r.pngLossless}${colour}`
}

const previewKey = (request: PreviewRequest | null) =>
  [...key, 'preview', request?.fileId ?? 0, request?.page ?? 0, request ? previewPath(request) : '']

async function fetchPreview(request: PreviewRequest) {
  const { blob, headers } = await apiBlob(previewPath(request))
  const original = Number(headers.get('X-Maki-Original-Bytes') ?? blob.size)
  const encoded = Number(headers.get('X-Maki-Encoded-Bytes') ?? blob.size)
  return {
    blob,
    originalBytes: original,
    encodedBytes: encoded,
    kept: headers.get('X-Maki-Kept') === '1',
    keptReason: headers.get('X-Maki-Kept-Reason'),
    colour: headers.get('X-Maki-Colour') === '1',
  }
}

/** One page's encoded size, from the same cache as `usePreviewPage`, so switching to it later is instant. Null when kept. */
export function usePreviewSize(request: PreviewRequest | null) {
  return useQuery({
    queryKey: previewKey(request),
    queryFn: () => fetchPreview(request!),
    enabled: request !== null,
    select: (answer) => (answer.kept ? null : answer.encodedBytes),
    staleTime: Infinity,
    gcTime: 60_000,
    retry: false,
    meta: { silent: true },
  })
}

/**
 * One page, encoded the way the pass would encode it. The previous answer stays in place while the
 * next one loads, so moving the slider never blanks the image. The caller debounces what it passes in.
 */
export function usePreviewPage(request: PreviewRequest | null) {
  const query = useQuery({
    queryKey: previewKey(request),
    queryFn: () => fetchPreview(request!),
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
      colour: answer.colour,
    })
    return () => URL.revokeObjectURL(url)
  }, [answer])

  return { data, isFetching: query.isFetching, isPlaceholder: query.isPlaceholderData, isError: query.isError, error: query.error }
}

export interface EstimateBucket {
  skip: boolean
  files: number
  bytes: number
  sampledFiles: number
  /** Expected size after the pass for the whole bucket: the 25th and 75th percentile of the sampled files. */
  expectedLow: number
  expectedHigh: number
  /** Never counted as saving. */
  hardlinkedBytes: number
  secondsLow: number
  secondsHigh: number
}

export interface EstimatePassedOver {
  hardlinkedFiles: number
  hardlinkedBytes: number
  cleanupDue: number
  alreadyCompact: number
  downloadInFlight: number
  pendingImport: number
  skippedByRule: number
  skippedByRuleBytes: number
  neverSeries: number
  neverFiles: number
  neverBytes: number
  /** Files an undo put back, which stay as they are. */
  undone: number
  /** Files an earlier pass already checked with the same answer, which this pass will not revisit. */
  alreadyChecked: number
  alreadyCheckedBytes: number
}

export interface EstimateConvertedEarlier {
  /** The wire name of the settings, or "unknown". */
  checkedWith: string
  files: number
  savedBytes: number
}

export interface EstimateResult {
  /** One per rule, in the order the request listed them. */
  rules: EstimateBucket[]
  default: EstimateBucket
  overrides: EstimateBucket
  passedOver: EstimatePassedOver
  convertedEarlier: EstimateConvertedEarlier[]
  convertedWithOriginalInTrash: number
  msPerMegapixel: number
}

export type EstimateState = 'idle' | 'running' | 'done' | 'failed' | 'cancelled'

export interface PageConversionEstimate {
  state: EstimateState
  /** Sampled files so far, out of total. */
  done: number
  total: number
  result: EstimateResult | null
  error: string | null
  startedAtUtc: string | null
  finishedAtUtc: string | null
  /** Identifies the request the result belongs to. */
  planHash: string | null
}

export interface PageConversionPlanRequest {
  settings: PageConversionSettings
  rules: PageConversionRule[]
}

const estimateKey = [...key, 'estimate']
const ESTIMATE_POLL_MS = 2000

/** The one estimate the server holds. Polls every two seconds while it runs. */
export function usePageConversionEstimate() {
  return useQuery({
    queryKey: estimateKey,
    queryFn: () => api<PageConversionEstimate>('/pageconversion/estimate'),
    refetchInterval: (query) => (query.state.data?.state === 'running' ? ESTIMATE_POLL_MS : false),
    retry: false,
  })
}

/** What `useStartPageConversionEstimate` last started: the request as sent and the server's hash for it. */
export interface EstimateRequestMark {
  requestKey: string
  planHash: string | null
}

const estimateMarkKey = [...estimateKey, 'mark']

/** The request as the server tells plans apart: rule names and ids do not change what an estimate says. */
export function estimateRequestKey(plan: PageConversionPlanRequest): string {
  return JSON.stringify({
    settings: plan.settings,
    rules: plan.rules.map((r) => ({
      conditions: r.conditions,
      action: r.action,
      stop: r.stop,
      format: r.format ?? null,
      colourFormat: r.colourFormat ?? null,
      colourStop: r.colourStop ?? null,
    })),
  })
}

/** Which request the estimate on the server was started for, as far as this tab knows. */
export function useEstimateMark() {
  return useQuery({
    queryKey: estimateMarkKey,
    queryFn: () => null as EstimateRequestMark | null,
    enabled: false,
    staleTime: Infinity,
    gcTime: Infinity,
  })
}

/** Starts an estimate for a plan that is not saved, replacing any that is running. */
export function useStartPageConversionEstimate() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (plan: PageConversionPlanRequest) =>
      api<PageConversionEstimate>('/pageconversion/estimate', { method: 'POST', body: JSON.stringify(plan) }),
    onMutate: () => queryClient.cancelQueries({ queryKey: estimateKey }),
    onSuccess: (estimate, plan) => {
      queryClient.setQueryData(estimateKey, estimate)
      queryClient.setQueryData<EstimateRequestMark>(estimateMarkKey, {
        requestKey: estimateRequestKey(plan),
        planHash: estimate.planHash,
      })
    },
  })
}

export interface SeriesConversion {
  convertedFiles: number
  savedBytes: number
  /** Files whose original is still in the trash. */
  restorableFiles: number
  restorableBytes: number
  /** The wire names of the settings the restorable files were converted with. */
  checkedWith: string[]
}

export interface UndoConversionRequest {
  setNever: boolean
  convertAgain: boolean
}

export interface UndoConversionResult {
  restored: number
  skipped: number
  reasons: string[]
}

const seriesConversionKey = (seriesId: number) => [...key, 'series', seriesId]

/** What a series' converted files can go back to. Only the admin who may undo it asks. */
export function useSeriesConversion(seriesId: number, enabled = true) {
  return useQuery({
    queryKey: seriesConversionKey(seriesId),
    queryFn: () => api<SeriesConversion>(`/series/${seriesId}/pageconversion/history`),
    enabled,
    retry: false,
    meta: { silent: true },
  })
}

export function useUndoSeriesConversion() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ seriesId, ...request }: UndoConversionRequest & { seriesId: number }) =>
      api<UndoConversionResult>(`/series/${seriesId}/pageconversion/undo`, {
        method: 'POST',
        body: JSON.stringify(request),
      }),
    onSuccess: (_result, { seriesId }) => {
      void queryClient.invalidateQueries({ queryKey: ['series'] })
      void queryClient.invalidateQueries({ queryKey: ['chapters'] })
      void queryClient.invalidateQueries({ queryKey: key, exact: true })
      void queryClient.invalidateQueries({ queryKey: seriesConversionKey(seriesId) })
    },
  })
}
