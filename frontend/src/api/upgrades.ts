import { useMutation, useQuery, useQueryClient, keepPreviousData } from '@tanstack/react-query'
import { i18n } from '@lingui/core'
import { msg, plural, t as now } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { api } from './client'
import type { ChapterFileQualityDto } from './types'

/** Mirrors `QualityTier` on the server, lowest first. */
export type QualityTierName = 'unknown' | 'aggregator' | 'scanlator' | 'official' | 'volume'

export const QUALITY_TIERS: QualityTierName[] = ['unknown', 'aggregator', 'scanlator', 'official', 'volume']

export const QUALITY_TIER_LABELS: Record<QualityTierName, MessageDescriptor> = {
  unknown: msg`Unknown`,
  aggregator: msg`Aggregator`,
  scanlator: msg`Scanlator`,
  official: msg`Official`,
  volume: msg`Volume`,
}

export interface ProfileTierDto {
  tier: QualityTierName
  allowed: boolean
}

export interface FormatScoreDto {
  formatId: number
  score: number
}

export interface UpgradeProfileDto {
  id: number
  name: string
  /** Highest priority first. */
  tiers: ProfileTierDto[]
  cutoff: QualityTierName
  upgradesEnabled: boolean
  minScoreDelta: number
  /** 0 means ignore, i.e. never stop upgrading once the cutoff tier is met. */
  upgradeUntilScore: number
  formatScores: FormatScoreDto[]
  pageTolerancePercent: number
  allowReplacingUnknown: boolean
  version: number
  seriesCount: number
}

export type UpgradeProfileInput = Omit<UpgradeProfileDto, 'id' | 'version' | 'seriesCount'>

export type FormatConditionType =
  | 'sourceIs'
  | 'sourceKindIs'
  | 'groupMatches'
  | 'releaseNameMatches'
  | 'minWidth'
  | 'imageFormatIs'
  | 'minBytesPerPage'
  | 'minPages'
  | 'languageIs'

export const FORMAT_CONDITION_TYPES: FormatConditionType[] = [
  'sourceIs',
  'sourceKindIs',
  'groupMatches',
  'releaseNameMatches',
  'minWidth',
  'imageFormatIs',
  'minBytesPerPage',
  'minPages',
  'languageIs',
]

export const FORMAT_CONDITION_TYPE_LABELS: Record<FormatConditionType, MessageDescriptor> = {
  sourceIs: msg`Source is`,
  sourceKindIs: msg`Source kind is`,
  groupMatches: msg`Group matches (regex)`,
  releaseNameMatches: msg`Release name matches (regex)`,
  minWidth: msg`Minimum width (px)`,
  imageFormatIs: msg`Image format is`,
  minBytesPerPage: msg`Minimum bytes per page`,
  minPages: msg`Minimum pages`,
  languageIs: msg`Language is`,
}

export type SourceKindName = 'aggregator' | 'scanlator' | 'official'

export const SOURCE_KINDS: SourceKindName[] = ['aggregator', 'scanlator', 'official']

export const SOURCE_KIND_LABELS: Record<SourceKindName, MessageDescriptor> = {
  aggregator: msg`Aggregator`,
  scanlator: msg`Scanlator`,
  official: msg`Official`,
}

export type ImageFormatName = 'jpg' | 'png' | 'webp' | 'avif' | 'mixed'

export const IMAGE_FORMATS: ImageFormatName[] = ['jpg', 'png', 'webp', 'avif', 'mixed']

export interface FormatConditionDto {
  type: FormatConditionType
  value: string
  required: boolean
  negate: boolean
}

export interface QualityFormatDto {
  id: number
  name: string
  conditions: FormatConditionDto[]
  version: number
  profileCount: number
}

export type QualityFormatInput = Omit<QualityFormatDto, 'id' | 'version' | 'profileCount'>

export interface CutoffUnmetRowDto {
  seriesId: number
  seriesTitle: string
  chapterId: number
  /** Null for a one-shot, same as `ChapterDto.number`. */
  chapterNumber: number | null
  chapterTitle: string | null
  fileId: number
  fileName: string
  quality: ChapterFileQualityDto
  profileId: number
  profileName: string
  cutoff: QualityTierName
}

export interface CutoffUnmetPageDto {
  rows: CutoffUnmetRowDto[]
  total: number
  page: number
  pageSize: number
}

export interface UpgradesSummaryDto {
  cutoffUnmet: number
  profilesConfigured: boolean
  /** Bytes currently held in every root's `.maki-trash`, from reverted-or-not upgrade history rows. */
  trashBytes: number
  trashFiles: number
  /** yyyy-MM-dd local, or null if the scan has never run. */
  lastScanDate: string | null
  scanRunning: boolean
}

export interface UpgradeSettings {
  enabled: boolean
  defaultProfileId: number | null
  /** Local hour 0..23 the daily scan is allowed to start after. */
  scanHour: number
  /** 0 means no cap. */
  maxPerDay: number
  maxProbesPerRun: number
  quietPeriodDays: number
  /** 0 means purge trashed files on the next housekeeping pass. */
  trashRetentionDays: number
  /** Whether a series with any incognito mode other than Off (`ScrobbleOnly` or `Full`) is scanned at all. */
  scanIncognito: boolean
}

/** A file's release tier and archive stats at one point in time, as carried on a queue/history row. */
export interface QualitySnapshotDto {
  tier: QualityTierName
  sourceName: string | null
  group: string | null
  pageCount: number | null
  medianWidth: number | null
  medianHeight: number | null
  imageFormat: string | null
  sizeBytes: number | null
  score: number
}

export type UpgradeOutcome = 'pending' | 'applied' | 'rejected'

/** `QueueItemDto.upgrade`: only set for a row whose `origin` is 'upgrade'. */
export interface UpgradeQueueInfoDto {
  outcome: UpgradeOutcome
  /** A reason code from `UPGRADE_REASON_LABELS`, set once the outcome is 'rejected'. */
  reason: string | null
  before: QualitySnapshotDto
  /** What the probe expected before the full download measured it. */
  predicted: QualitySnapshotDto
  /** Filled once the full download has been measured. */
  after: QualitySnapshotDto | null
  /** Set once the outcome is 'applied'; drives the Revert action. */
  historyId: number | null
  /** Set once this upgrade has been reverted; the Revert action becomes "Reverted" instead. */
  reverted: boolean
  /** Whether the trashed original this row would restore is still on disk. */
  trashAvailable: boolean
}

export interface UpgradeHistoryRowDto {
  id: number
  seriesId: number
  seriesTitle: string
  chapterId: number
  /** Null for a one-shot, same as `ChapterDto.number`. */
  chapterNumber: number | null
  chapterTitle: string | null
  fileId: number
  fileName: string
  before: QualitySnapshotDto
  after: QualitySnapshotDto
  profileName: string
  trashBytes: number
  /** False once the trashed original has been purged by housekeeping or a previous revert. */
  trashAvailable: boolean
  createdAt: string
  revertedAt: string | null
}

export interface UpgradeHistoryPageDto {
  rows: UpgradeHistoryRowDto[]
  total: number
  page: number
  pageSize: number
}

export interface UpgradeScanResultDto {
  seriesScanned: number
  chaptersChecked: number
  candidatesProbed: number
  enqueued: number
  /**
   * Counts keyed by reason code. Mostly `UpgradeReasonCode`, plus scan-only bucket codes
   * (`UpgradeSkipReasonCode`) that never appear as an `UpgradeAttempt`/queue-row reason.
   */
  skipped: Record<string, number>
}

export type UpgradeReasonCode =
  | 'tier_not_allowed'
  | 'score_not_higher'
  | 'fewer_pages'
  | 'unmeasurable'
  | 'quiet_period'
  | 'probe_failed'
  | 'source_cooldown'
  | 'enqueued'
  | 'upgrade_rejected'
  | 'reverted_by_user'

/** Bucket codes only `UpgradeScanResultDto.skipped` carries, never an attempt/queue-row reason. */
export type UpgradeSkipReasonCode =
  | 'unmeasured'
  | 'trusted'
  | 'cutoff_met'
  | 'shared_file'
  | 'queued'
  | 'memoised'
  | 'probe_budget'
  | 'daily_cap'
  | 'unsupported_file'

/**
 * Descriptors, not strings: this table is built once when the module loads, so a rendered string
 * here would be frozen in whichever language was active then. Render through `upgradeReasonLabel`,
 * which falls back to the raw code for one this build doesn't recognise.
 */
export const UPGRADE_REASON_LABELS: Record<UpgradeReasonCode | UpgradeSkipReasonCode, MessageDescriptor> = {
  tier_not_allowed: msg`That source's tier isn't allowed by the profile`,
  score_not_higher: msg`Didn't score higher than the current file`,
  fewer_pages: msg`Has fewer pages than the current file`,
  unmeasurable: msg`Couldn't be measured`,
  quiet_period: msg`This chapter was added or upgraded too recently`,
  probe_failed: msg`The sample download failed`,
  source_cooldown: msg`That source is rate-limited right now`,
  enqueued: msg`Queued for download`,
  upgrade_rejected: msg`Rejected after downloading the full chapter`,
  reverted_by_user: msg`Reverted by a user`,
  unmeasured: msg`Not measured yet`,
  trusted: msg`Protected from upgrades`,
  cutoff_met: msg`Already meets the cutoff`,
  shared_file: msg`Shares a file with another chapter`,
  queued: msg`Already queued`,
  memoised: msg`Already checked recently`,
  probe_budget: msg`Ran out of probes for this run`,
  daily_cap: msg`Daily upgrade cap reached`,
  unsupported_file: msg`Unsupported file type`,
}

/** `LABELS[x] ?? x`: a code this build has no case for renders as-is rather than disappearing. */
export function upgradeReasonLabel(renderLabel: (m: MessageDescriptor) => string, code: string): string {
  return renderLabel(UPGRADE_REASON_LABELS[code as UpgradeReasonCode | UpgradeSkipReasonCode] ?? code)
}

/**
 * One line for a finished `UpgradeScanResultDto`: how many were queued, and, since a run mostly
 * skips things, the skipped counts by reason so "Scan finished" isn't the only feedback an admin
 * gets when nothing got enqueued.
 */
export function upgradeScanResultText(
  renderLabel: (m: MessageDescriptor) => string,
  result: UpgradeScanResultDto,
): string {
  const { enqueued } = result
  const queuedText = plural(enqueued, { one: '# upgrade queued', other: '# upgrades queued' })
  const skippedEntries = Object.entries(result.skipped).filter(([, count]) => count > 0)
  if (skippedEntries.length === 0) return queuedText
  const entryTexts = skippedEntries.map(([code, count]) => {
    const label = upgradeReasonLabel(renderLabel, code)
    return now`${count} ${label}`
  })
  // Locale-aware "a, b and c" where available; a plain comma join is an acceptable fallback rather
  // than another translated string for something this incidental.
  const skippedText =
    typeof Intl.ListFormat === 'function'
      ? new Intl.ListFormat(i18n.locale || undefined).format(entryTexts)
      : entryTexts.join(', ')
  return now`${queuedText} - skipped ${skippedText}`
}

export function useUpgradeProfiles() {
  return useQuery({
    queryKey: ['upgrade-profiles'],
    queryFn: () => api<UpgradeProfileDto[]>('/upgrade-profiles'),
  })
}

/**
 * Every write invalidates the cutoff-unmet views too: a profile edit can change which files count.
 * `series-files` as well, since the Files tab's Quality column is scored the same way and a tier or
 * score-table change can flip its cutoffMet just as easily as a chapter row's.
 */
function useUpgradeProfileMutation<TArgs>(fn: (args: TArgs) => Promise<unknown>) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: fn,
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['upgrade-profiles'] })
      void queryClient.invalidateQueries({ queryKey: ['upgrades'] })
      void queryClient.invalidateQueries({ queryKey: ['series'] })
      void queryClient.invalidateQueries({ queryKey: ['chapters'] })
      void queryClient.invalidateQueries({ queryKey: ['series-files'] })
    },
  })
}

export function useCreateUpgradeProfile() {
  return useUpgradeProfileMutation((input: UpgradeProfileInput) =>
    api<UpgradeProfileDto>('/upgrade-profiles', { method: 'POST', body: JSON.stringify(input) }),
  )
}

export function useUpdateUpgradeProfile() {
  return useUpgradeProfileMutation(({ id, ...input }: UpgradeProfileInput & { id: number }) =>
    api<UpgradeProfileDto>(`/upgrade-profiles/${id}`, { method: 'PUT', body: JSON.stringify(input) }),
  )
}

export function useDeleteUpgradeProfile() {
  return useUpgradeProfileMutation((id: number) => api(`/upgrade-profiles/${id}`, { method: 'DELETE' }))
}

export function useQualityFormats() {
  return useQuery({
    queryKey: ['quality-formats'],
    queryFn: () => api<QualityFormatDto[]>('/quality-formats'),
  })
}

function useQualityFormatMutation<TArgs>(fn: (args: TArgs) => Promise<unknown>) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: fn,
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['quality-formats'] })
      // Deleting a format also strips it from every profile's format scores server side.
      void queryClient.invalidateQueries({ queryKey: ['upgrade-profiles'] })
      void queryClient.invalidateQueries({ queryKey: ['upgrades'] })
      void queryClient.invalidateQueries({ queryKey: ['series-files'] })
    },
  })
}

export function useCreateQualityFormat() {
  return useQualityFormatMutation((input: QualityFormatInput) =>
    api<QualityFormatDto>('/quality-formats', { method: 'POST', body: JSON.stringify(input) }),
  )
}

export function useUpdateQualityFormat() {
  return useQualityFormatMutation(({ id, ...input }: QualityFormatInput & { id: number }) =>
    api<QualityFormatDto>(`/quality-formats/${id}`, { method: 'PUT', body: JSON.stringify(input) }),
  )
}

export function useDeleteQualityFormat() {
  return useQualityFormatMutation((id: number) => api(`/quality-formats/${id}`, { method: 'DELETE' }))
}

/**
 * `enabled` defaults to true for a series' own cutoff-unmet list (nothing gates that), but Activity's
 * instance-wide Upgrades tab passes `tab === 'upgrades'` so switching to Queue doesn't keep re-running
 * a library-wide evaluation every focus/poll.
 */
export function useCutoffUnmet(page: number, pageSize = 50, seriesId?: number, enabled = true) {
  return useQuery({
    queryKey: ['upgrades', 'cutoff-unmet', { page, pageSize, seriesId }],
    queryFn: () =>
      api<CutoffUnmetPageDto>(
        `/upgrades/cutoff-unmet?page=${page}&pageSize=${pageSize}${seriesId != null ? `&seriesId=${seriesId}` : ''}`,
      ),
    placeholderData: keepPreviousData,
    enabled,
  })
}

export function useUpgradesSummary(enabled = true) {
  return useQuery({
    queryKey: ['upgrades', 'summary'],
    queryFn: () => api<UpgradesSummaryDto>('/upgrades/summary'),
    enabled,
  })
}

export function useUpgradeSettings() {
  return useQuery({
    queryKey: ['settings', 'upgrades'],
    queryFn: () => api<UpgradeSettings>('/settings/upgrades'),
  })
}

export function useSaveUpgradeSettings() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (value: UpgradeSettings) =>
      api<UpgradeSettings>('/settings/upgrades', { method: 'PUT', body: JSON.stringify(value) }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['settings', 'upgrades'] })
      // The default profile applies to every series that has no profile of its own, so changing it
      // can flip cutoffMet/score for most of the library, not just one series.
      void queryClient.invalidateQueries({ queryKey: ['upgrades'] })
      void queryClient.invalidateQueries({ queryKey: ['chapters'] })
      void queryClient.invalidateQueries({ queryKey: ['series-files'] })
    },
  })
}

/**
 * `enabled` defaults to true; the Activity Upgrades tab passes `tab === 'upgrades'` like
 * `useCutoffUnmet` so switching to Queue stops paging through history in the background.
 */
export function useUpgradeHistory(page: number, pageSize = 25, seriesId?: number, enabled = true) {
  return useQuery({
    queryKey: ['upgrades', 'history', { page, pageSize, seriesId }],
    queryFn: () =>
      api<UpgradeHistoryPageDto>(
        `/upgrades/history?page=${page}&pageSize=${pageSize}${seriesId != null ? `&seriesId=${seriesId}` : ''}`,
      ),
    placeholderData: keepPreviousData,
    enabled,
  })
}

/**
 * Shared invalidation for every mutation below: a revert or a trusted flip can change which files
 * are cutoff-unmet (`upgrades`), the chapter and Files-tab quality columns (`chapters`,
 * `series-files`), and a revert also re-touches the live queue row and its history entry.
 */
function useUpgradeMutation<TArgs, TResult>(fn: (args: TArgs) => Promise<TResult>) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: fn,
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['upgrades'] })
      void queryClient.invalidateQueries({ queryKey: ['chapters'] })
      void queryClient.invalidateQueries({ queryKey: ['series-files'] })
      void queryClient.invalidateQueries({ queryKey: ['queue'] })
      void queryClient.invalidateQueries({ queryKey: ['queue-history'] })
    },
  })
}

export function useRevertUpgrade() {
  return useUpgradeMutation((historyId: number) =>
    api<UpgradeHistoryRowDto>(`/upgrades/history/${historyId}/revert`, { method: 'POST' }),
  )
}

export function useSetFileTrusted() {
  return useUpgradeMutation(({ fileId, trusted }: { fileId: number; trusted: boolean }) =>
    api<{ trusted: boolean }>(`/chapter-files/${fileId}/trusted`, {
      method: 'POST',
      body: JSON.stringify({ trusted }),
    }),
  )
}

/** A library-wide scan starts in the background; a single series' scan runs synchronously. */
export function isUpgradeScanStarted(result: UpgradeScanResultDto | { started: true }): result is { started: true } {
  return 'started' in result
}

export function useRunUpgradeScan() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (seriesId?: number) =>
      api<UpgradeScanResultDto | { started: true }>('/upgrades/scan', {
        method: 'POST',
        body: JSON.stringify({ seriesId: seriesId ?? null }),
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['upgrades'] })
      void queryClient.invalidateQueries({ queryKey: ['queue'] })
      void queryClient.invalidateQueries({ queryKey: ['queue-history'] })
    },
  })
}
