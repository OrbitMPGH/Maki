import { useMutation, useQuery, useQueryClient, keepPreviousData } from '@tanstack/react-query'
import { msg } from '@lingui/core/macro'
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
}

export interface UpgradeSettings {
  enabled: boolean
  defaultProfileId: number | null
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
