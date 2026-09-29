import { useMutation, useQuery, useQueryClient, keepPreviousData } from '@tanstack/react-query'
import { i18n } from '@lingui/core'
import { msg, plural, t as now } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { api } from './client'
import type { ChapterFileQualityDto } from './types'
import type { SourceOrderMode } from './hooks'

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

/** Shared with `FileQualityBadge` and `SourceCompareModal`'s per-column quality badge. */
export const QUALITY_TIER_COLOR: Record<QualityTierName, string> = {
  unknown: 'gray',
  aggregator: 'orange',
  scanlator: 'blue',
  official: 'teal',
  volume: 'indigo',
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
  /** What choosing this profile does and costs. Null when the admin left it blank. */
  description: string | null
  /** Highest priority first. */
  tiers: ProfileTierDto[]
  cutoff: QualityTierName
  upgradesEnabled: boolean
  minScoreDelta: number
  /** 0 means ignore, i.e. never stop upgrading once the cutoff tier is met. */
  upgradeUntilScore: number
  formatScores: FormatScoreDto[]
  /** Points per doubling of median page width, clamped to 500 to 2000px around 1000px. 0 is off. */
  resolutionWeight: number
  /** Points per doubling of image data per pixel, after adjusting for PNG/WebP/AVIF. 0 is off. */
  compressionWeight: number
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
  profilesConfigured: boolean
  /** Bytes currently held in every root's `.maki-trash`, from reverted-or-not upgrade history rows. */
  trashBytes: number
  trashFiles: number
  /** yyyy-MM-dd local, or null if the scan has never run. */
  lastScanDate: string | null
  scanRunning: boolean
  /** Pending torrent proposals the caller may see. */
  pendingProposals: number
  /** yyyy-MM-dd local, or null if the volume search job has never run. */
  lastVolumeSearchDate: string | null
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
  volumeSearch: boolean
  /** Bytes on the wire; the settings page shows MB. */
  torrentAutoGrabMaxBytes: number
  volumeMissingTolerance: number
  volumeSearchesPerRun: number
  proposalExpiryDays: number
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

export type UpgradeOutcome = 'pending' | 'applied' | 'parked' | 'rejected'

/** `QueueItemDto.upgrade`: only set for a row whose `origin` is 'upgrade'. */
export interface UpgradeQueueInfoDto {
  outcome: UpgradeOutcome
  /** A reason code from `UPGRADE_REASON_LABELS`, set once the outcome is 'rejected'. */
  reason: string | null
  /** Null on a torrent volume upgrade, which has no single file to compare. */
  before: QualitySnapshotDto | null
  /** What the probe expected before the full download measured it. */
  predicted: QualitySnapshotDto | null
  /** Filled once the full download has been measured. */
  after: QualitySnapshotDto | null
  /** Set once the outcome is 'applied'; drives the Revert action. */
  historyId: number | null
  /** Set instead of `historyId` on an applied torrent volume replacement; reverts as one group. */
  historyGroupId: string | null
  /** Number of library files a torrent volume upgrade replaces; absent on a chapter upgrade. */
  replacedFiles?: number
  /** Set once this upgrade has been reverted; the Revert action becomes "Reverted" instead. */
  reverted: boolean
  /** Whether the trashed original this row would restore is still on disk. */
  trashAvailable: boolean
  /**
   * A manual re-download through the compare modal or Redownload rather than the automatic
   * upgrader: the row's `origin` stays 'manual', so this is what tells the Queue/History badge
   * and copy to say "Replace" instead of "Upgrade".
   */
  force: boolean
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
  /** Shared by every row of one torrent volume replacement; null for a chapter upgrade. */
  groupId: string | null
  /** Rows in the whole group, which can exceed the rows on this page. */
  groupSize: number
}

export interface UpgradeHistoryPageDto {
  rows: UpgradeHistoryRowDto[]
  total: number
  page: number
  pageSize: number
}

/** One row per source considered for a chapter scan; `UpgradeScanResultDto.candidates` is empty
 * for series and library scans, where nobody is watching a single chapter's outcome. */
export interface UpgradeCandidateOutcomeDto {
  mappingId: number
  sourceName: string
  sourceChapterId: string
  /** A reason code from `UPGRADE_REASON_LABELS`, including `enqueued` for the winner. */
  reason: string
  probed: boolean
  pageCount: number | null
  medianWidth: number | null
  score: number | null
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
  /** Filled for a chapter scan only; empty for series and library scans. */
  candidates: UpgradeCandidateOutcomeDto[]
  /** The mapping a chapter scan queued from, or null when nothing won. */
  queuedFromMappingId: number | null
}

export type UpgradeReasonCode =
  | 'tier_not_allowed'
  | 'score_not_higher'
  | 'estimate_not_higher'
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
  | 'no_other_source'
  /** `ComparePanelQualityDto.reason` on a series with no upgrade profile; never a scan bucket. */
  | 'no_profile'
  | 'upgrades_disabled'
  | 'incognito'

/**
 * Descriptors, not strings: this table is built once when the module loads, so a rendered string
 * here would be frozen in whichever language was active then. Render through `upgradeReasonLabel`,
 * which falls back to the raw code for one this build doesn't recognise.
 */
export const UPGRADE_REASON_LABELS: Record<UpgradeReasonCode | UpgradeSkipReasonCode, MessageDescriptor> = {
  tier_not_allowed: msg`That source's tier isn't allowed by the profile`,
  score_not_higher: msg`Didn't score higher than the current file`,
  estimate_not_higher: msg`Not sampled: this source's recent chapters wouldn't score higher`,
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
  no_other_source: msg`No other source lists it`,
  probe_budget: msg`Ran out of probes for this run`,
  daily_cap: msg`Daily upgrade cap reached`,
  unsupported_file: msg`Unsupported file type`,
  no_profile: msg`No quality profile`,
  upgrades_disabled: msg`Upgrades are off for this profile`,
  incognito: msg`Incognito series are excluded`,
}

/** `LABELS[x] ?? x`: a code this build has no case for renders as-is rather than disappearing. */
export function upgradeReasonLabel(renderLabel: (m: MessageDescriptor) => string, code: string): string {
  return renderLabel(UPGRADE_REASON_LABELS[code as UpgradeReasonCode | UpgradeSkipReasonCode] ?? code)
}

/** Why a scan passed chapters or candidates over, most common first: "Label (3) · Label (1)". */
export function scanReasonSummary(
  renderLabel: (m: MessageDescriptor) => string,
  skipped: Record<string, number> | null | undefined,
): string {
  return Object.entries(skipped ?? {})
    .filter(([, count]) => count > 0)
    .sort(([, a], [, b]) => b - a)
    .map(([code, count]) => `${upgradeReasonLabel(renderLabel, code)} (${i18n.number(count)})`)
    .join(' · ')
}

export interface NumberRangeDto {
  start: number
  end: number
}

export interface ReleaseSpanDto {
  volumeStart: number | null
  volumeEnd: number | null
  chapters: NumberRangeDto[]
  /** A pack that names no volumes or chapters and covers the whole series. */
  wholeSeries: boolean
}

export interface TorrentProposalDto {
  id: number
  seriesId: number
  seriesTitle: string
  title: string
  indexer: string
  sizeBytes: number
  span: ReleaseSpanDto
  /** Codes from `PROPOSAL_REASON_LABELS`. */
  reasons: string[]
  upgradeCount: number
  alreadyMetCount: number
  skippedCount: number
  missingCount: number
  unknownCount: number
  score: number
  tier: QualityTierName
  /** pending | accepted | dismissed | expired */
  status: string
  createdAtUtc: string
  resolvedAtUtc: string | null
  queueItemId: number | null
}

export type SpanVerdictName = 'ignore' | 'proposal' | 'autoGrab'

/** `parsed` on a release search row, null when the series has no upgrade profile. */
export interface ReleaseParsedDto {
  span: ReleaseSpanDto
  tier: QualityTierName
  score: number
  verdict: SpanVerdictName
  reasons: string[]
  upgradeCount: number
  alreadyMetCount: number
  missingCount: number
  titleMatched: boolean
}

export interface SeriesVolumeSearchResultDto {
  searched: boolean
  resultCount: number
  /** Queue item id of an auto-grabbed release. */
  grabbed: number | null
  proposalId: number | null
  /** `not_eligible_<why>` when the series was not searched. */
  reason: string | null
}

export const PROPOSAL_REASON_LABELS: Record<string, MessageDescriptor> = {
  adds_missing_chapters: msg`Adds chapters you don't have yet`,
  over_budget: msg`Larger than the auto-grab size limit`,
  volume_span_unknown: msg`Its volumes aren't mapped to your chapters yet`,
  unknown_chapters: msg`Covers chapters Maki doesn't know about`,
  title_uncertain: msg`The title might be a different series`,
  nothing_to_upgrade: msg`Nothing in it beats what you have`,
  no_span: msg`No volume or chapter range in the title`,
  no_profile: msg`No quality profile`,
  whole_series_pack: msg`Whole-series pack, needs your call`,
  trailing_number: msg`Release number may be a sequel, not a chapter`,
  upgrades_disabled: msg`Automatic upgrades are off, needs your call`,
}

/** `LABELS[x] ?? x`, same as `upgradeReasonLabel`. */
export function proposalReasonLabel(renderLabel: (m: MessageDescriptor) => string, code: string): string {
  return renderLabel(PROPOSAL_REASON_LABELS[code] ?? code)
}

export type SpanVerdictKey = SpanVerdictName | 'titleUncertain'

export const SPAN_VERDICT_LABELS: Record<SpanVerdictKey, MessageDescriptor> = {
  autoGrab: msg`Auto-grab`,
  proposal: msg`Proposal`,
  ignore: msg`Not an upgrade`,
  titleUncertain: msg`Title uncertain`,
}

export const SPAN_VERDICT_COLOR: Record<SpanVerdictKey, string> = {
  autoGrab: 'var(--ok)',
  proposal: 'var(--brand)',
  ignore: 'var(--neutral)',
  titleUncertain: 'var(--warn)',
}

export function spanVerdictKey(parsed: ReleaseParsedDto): SpanVerdictKey {
  return parsed.verdict === 'ignore' && !parsed.titleMatched ? 'titleUncertain' : parsed.verdict
}

function numberRangeText(start: number, end: number): string {
  return start === end ? String(start) : now`${start} to ${end}`
}

/** "Volumes 1 to 6 + chapters 49.1 to 57", or null for a title with no range in it. */
export function releaseSpanText(span: ReleaseSpanDto): string | null {
  const { volumeStart, volumeEnd } = span
  const parts: string[] = []
  if (volumeStart != null && volumeEnd != null) {
    const range = numberRangeText(volumeStart, volumeEnd)
    parts.push(volumeStart === volumeEnd ? now`Volume ${range}` : now`Volumes ${range}`)
  }
  if (span.chapters.length > 0) {
    const ranges = span.chapters.map((c) => numberRangeText(c.start, c.end)).join(', ')
    const single = span.chapters.length === 1 && span.chapters[0].start === span.chapters[0].end
    parts.push(single ? now`chapter ${ranges}` : now`chapters ${ranges}`)
  }
  if (parts.length === 0) return span.wholeSeries ? now`Whole series` : null
  if (parts.length === 1) return parts[0]
  const [volumesText, chaptersText] = parts
  return now`${volumesText} + ${chaptersText}`
}

/** "Replaces 10 files, adds 47 chapters you don't have and 3 already at cutoff". */
export function proposalCountsText(counts: {
  upgradeCount: number
  alreadyMetCount: number
  missingCount: number
}): string {
  const { upgradeCount, alreadyMetCount, missingCount } = counts
  const parts = [plural(upgradeCount, { one: 'Replaces # file', other: 'Replaces # files' })]
  if (missingCount > 0) {
    parts.push(
      plural(missingCount, { one: "adds # chapter you don't have", other: "adds # chapters you don't have" }),
    )
  }
  if (alreadyMetCount > 0) parts.push(now`${alreadyMetCount} already at cutoff`)
  return typeof Intl.ListFormat === 'function'
    ? new Intl.ListFormat(i18n.locale || undefined).format(parts)
    : parts.join(', ')
}

const NOT_ELIGIBLE_LABELS: Record<string, MessageDescriptor> = {
  not_eligible_disabled: msg`Volume search is turned off`,
  not_eligible_no_profile: msg`This series has no quality profile`,
  not_eligible_cutoff: msg`This series' profile doesn't aim for volume releases`,
  not_eligible_nothing_below_cutoff: msg`Every file already meets the cutoff`,
  not_eligible_pending_proposal: msg`A proposal is already waiting for this series`,
  not_eligible_incognito: msg`Incognito series are excluded`,
  not_eligible_no_prowlarr: msg`Prowlarr isn't configured`,
  not_eligible_not_found: msg`Series not found`,
  not_eligible_recent: msg`This series was searched in the last week`,
  not_eligible_profile_disabled: msg`Upgrades are turned off in this series' profile`,
}

/** One line for a finished manual volume search. */
export function volumeSearchResultText(
  renderLabel: (m: MessageDescriptor) => string,
  result: SeriesVolumeSearchResultDto,
): string {
  if (result.grabbed != null) return now`Queued a volume release`
  if (result.proposalId != null) return now`Proposal created`
  if (result.reason === 'search_failed') return now`Prowlarr search failed`
  if (result.reason === 'grab_failed') return now`Could not queue the release`
  if (!result.searched) {
    const label = result.reason ? NOT_ELIGIBLE_LABELS[result.reason] : undefined
    return label ? renderLabel(label) : now`This series isn't eligible for a volume search`
  }
  if (result.resultCount === 0) return now`No releases found`
  return now`Nothing worth grabbing in the results`
}

/** One source mapping's measured track record for a series, from `GET /sourcemapping/quality`. */
export interface SourceQualityDto {
  mappingId: number
  samples: number
  medianWidth: number
  /** JPG-equivalent median. */
  bitsPerPixel: number
  imageFormat: string | null
  latestUtc: string
  /** Enough recent samples that the upgrade scan trusts them instead of sampling pages first. */
  reliable: boolean
  /** Under the series' upgrade profile; null when it has none. */
  resolutionPoints: number | null
  compressionPoints: number | null
}

/** How a series orders its sources for downloads, from `GET /sourcemapping/quality`. */
export interface SourceOrderDto {
  /** The series' own setting; null follows `defaultMode`. */
  seriesMode: SourceOrderMode | null
  defaultMode: SourceOrderMode
  mode: SourceOrderMode
  /** Mapping ids in the order a download tries them; disabled mappings last. */
  order: number[]
  sources: SourceQualityDto[]
  /** The latest source measurement run since the server started, or null. */
  scout: ScoutSnapshot | null
  /** Enabled mapping ids as best quality first would order them, whatever the mode. */
  qualityOrder: number[]
  /** Per mapping id, the tier its copies count as. Best quality first compares it before the score. */
  tiers: Record<number, QualityTierName>
}

export interface ScoutSnapshot {
  running: boolean
  /** Chapter samples planned across every source. */
  probes: number
  done: number
  /** Samples that produced a measurement; the rest failed or the source was rate-limited. */
  measured: number
  startedAtUtc: string
  finishedAtUtc: string | null
  /** The chapters being sampled, as their labels, in reading order. */
  chapters: string[]
  sources: ScoutSourceProgress[]
}

export interface ScoutSourceProgress {
  mappingId: number
  /** `skipped`: the source lists none of the sampled chapters. `failed`: nothing could be measured. */
  state: 'waiting' | 'measuring' | 'done' | 'failed' | 'skipped'
  planned: number
  done: number
  measured: number
  /** Why the last failed sample failed: `cooldown` when the source is rate-limiting, else `failed`. */
  problem: 'cooldown' | 'failed' | null
}

/** The latest scan of one series asked for since the server started. */
export interface SeriesScanStatus {
  state: 'queued' | 'running' | 'done' | 'busy' | 'failed'
  queuedAtUtc: string
  finishedAtUtc: string | null
  chaptersChecked: number
  probed: number
  queued: number
  /** Reason code to count, once done. */
  skipped: Record<string, number> | null
}

export function useSeriesUpgradeScanStatus(seriesId: number) {
  return useQuery({
    queryKey: ['upgrade-scan-status', seriesId],
    queryFn: async () =>
      (await api<SeriesScanStatus | undefined>(`/upgrades/scan/status?seriesId=${seriesId}`)) ?? null,
    refetchInterval: (query) => {
      const state = query.state.data?.state
      return state === 'queued' || state === 'running' ? 2000 : false
    },
  })
}

export function useSourceOrder(seriesId: number) {
  return useQuery({
    queryKey: ['source-order', seriesId],
    queryFn: () => api<SourceOrderDto>(`/sourcemapping/quality?seriesId=${seriesId}`),
    refetchInterval: (query) => (query.state.data?.scout?.running ? 2000 : false),
  })
}

/** Samples every linked source of the series in the background; `useSourceOrder` polls while it runs. */
export function useMeasureSources(seriesId: number) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: () =>
      api<ScoutSnapshot>('/sourcemapping/scout', { method: 'POST', body: JSON.stringify({ seriesId }) }),
    onSuccess: (scout) => {
      queryClient.setQueryData<SourceOrderDto>(['source-order', seriesId], (old) => (old ? { ...old, scout } : old))
      void queryClient.invalidateQueries({ queryKey: ['source-order', seriesId] })
    },
  })
}

export function useSetSourceOrderMode(seriesId: number) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (mode: SourceOrderMode | null) =>
      api<void>('/sourcemapping/ordermode', { method: 'PUT', body: JSON.stringify({ seriesId, mode }) }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['source-order', seriesId] })
    },
  })
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

export function useRevertUpgradeGroup() {
  return useUpgradeMutation((groupId: string) =>
    api<unknown>(`/upgrades/history/group/${groupId}/revert`, { method: 'POST' }),
  )
}

export function useTorrentProposals(seriesId?: number, enabled = true) {
  return useQuery({
    queryKey: ['upgrades', 'proposals', seriesId],
    queryFn: () =>
      api<TorrentProposalDto[]>(
        `/upgrades/proposals?status=pending${seriesId != null ? `&seriesId=${seriesId}` : ''}`,
      ),
    enabled,
  })
}

function useProposalMutation(action: 'grab' | 'dismiss') {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ id }: { id: number; seriesId: number }) =>
      api<{ queueItemId: number } | void>(`/upgrades/proposals/${id}/${action}`, { method: 'POST' }),
    onSuccess: (_result, { seriesId }) => {
      void queryClient.invalidateQueries({ queryKey: ['upgrades'] })
      void queryClient.invalidateQueries({ queryKey: ['queue'] })
      void queryClient.invalidateQueries({ queryKey: ['series', seriesId] })
    },
  })
}

export function useGrabProposal() {
  return useProposalMutation('grab')
}

export function useDismissProposal() {
  return useProposalMutation('dismiss')
}

/** A series search runs synchronously and returns its result; the library-wide job answers `{ started: true }`. */
export function useVolumeSearch() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (seriesId?: number) =>
      api<SeriesVolumeSearchResultDto | { started: true }>('/upgrades/volume-search', {
        method: 'POST',
        body: JSON.stringify({ seriesId: seriesId ?? null }),
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['upgrades'] })
      void queryClient.invalidateQueries({ queryKey: ['queue'] })
      void queryClient.invalidateQueries({ queryKey: ['series'] })
    },
  })
}

export function isVolumeSearchStarted(
  result: SeriesVolumeSearchResultDto | { started: true },
): result is { started: true } {
  return 'started' in result
}

export function useSetFileTrusted() {
  return useUpgradeMutation(({ fileId, trusted }: { fileId: number; trusted: boolean }) =>
    api<{ trusted: boolean }>(`/chapter-files/${fileId}/trusted`, {
      method: 'POST',
      body: JSON.stringify({ trusted }),
    }),
  )
}

/**
 * Starts a library-wide scan (no `seriesId`) or a single series' scan in the background. Both write the series' last-scan
 * columns, so `series` and `chapters` are invalidated alongside the usual upgrade views: the Details
 * tab's "Last upgrade scan" line and the chapter list's quality badges can change from either kind of
 * run, not just from a chapter-level upgrade.
 */
export function useRunUpgradeScan() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (seriesId?: number) =>
      api<{ started: true }>('/upgrades/scan', {
        method: 'POST',
        body: JSON.stringify({ seriesId: seriesId ?? null }),
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['upgrades'] })
      void queryClient.invalidateQueries({ queryKey: ['queue'] })
      void queryClient.invalidateQueries({ queryKey: ['queue-history'] })
      void queryClient.invalidateQueries({ queryKey: ['series'] })
      void queryClient.invalidateQueries({ queryKey: ['chapters'] })
      void queryClient.invalidateQueries({ queryKey: ['upgrade-scan-status'] })
    },
  })
}

/**
 * "Upgrade now" for one chapter: runs a scan scoped to it and reports every candidate considered,
 * shown in `UpgradeNowResultModal`. Ignores the global switch and the quiet period like the series
 * scan does, but still honours trusted, cutoff-met and the memo.
 */
export function useUpgradeChapterNow() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (chapterId: number) =>
      api<UpgradeScanResultDto>('/upgrades/scan', {
        method: 'POST',
        body: JSON.stringify({ chapterId }),
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['upgrades'] })
      void queryClient.invalidateQueries({ queryKey: ['queue'] })
      void queryClient.invalidateQueries({ queryKey: ['queue-history'] })
      void queryClient.invalidateQueries({ queryKey: ['chapters'] })
      void queryClient.invalidateQueries({ queryKey: ['series'] })
    },
  })
}
