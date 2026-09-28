import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { api } from './client'
import type { NamedCount, ReadingBehaviour } from './hooks'

// The Stats page's two newer payloads. `stats/insights` follows the page's window like
// `stats/activity`; `stats/standing` does not depend on the window at all, which is why it is a
// separate call rather than more fields on either.

/** 168 buckets, Monday = 0, index `weekday * 24 + hour`, in the reader's time zone. */
export interface RhythmDto {
  secondsByWeekdayHour: number[]
  totalSeconds: number
  /** First hour of the busiest three-hour window. Wraps past midnight: 23 means 23:00 to 02:00. */
  primeStartHour: number | null
  primeShare: number | null
  /** Monday = 0. */
  busiestWeekday: number | null
  weekendShare: number | null
}

export interface SittingsDto {
  count: number
  medianSeconds: number
  longestSeconds: number
  longestStartedAt: string
  perWeek: number
  chaptersPerSittingMedian: number
}

/** Both shares are 0..1. Positive `readShare - libraryShare` means read more than owned. */
export interface GenreLeanDto {
  name: string
  readShare: number
  libraryShare: number
}

/** `decade` is null for series with no release year. */
export interface EraBucketDto {
  decade: number | null
  chapters: number
}

export interface TasteMixDto {
  lean: GenreLeanDto[]
  types: NamedCount[]
  demographics: NamedCount[]
  eras: EraBucketDto[]
}

export interface StatsInsightsDto {
  from: string
  to: string
  timeZone: string
  rhythm: RhythmDto
  /** Null when no sitting falls in the window. */
  sittings: SittingsDto | null
  taste: TasteMixDto
  bookmarksAdded: number
}

export interface BacklogSeriesDto {
  seriesId: number
  title: string
  coverUrl: string | null
  read: number
  unread: number
}

export interface BacklogDto {
  unreadChapters: number
  seriesWithUnread: number
  hoursAtPace: number | null
  top: BacklogSeriesDto[]
}

export interface MidwaySeriesDto {
  seriesId: number
  title: string
  coverUrl: string | null
  read: number
  held: number
  etaSeconds: number | null
  lastReadAt: string
}

export interface CreatorReturnDto {
  name: string
  story: boolean
  art: boolean
  seriesRead: number
  chaptersRead: number
}

/** `yours` is 1-10, `community` is MangaBaka's score rescaled to 0-10. */
export interface RatedSeriesDto {
  seriesId: number
  title: string
  coverUrl: string | null
  yours: number
  community: number
}

export interface RatingGapDto {
  meanGap: number
  rated: number
  series: RatedSeriesDto[]
}

export interface StatsStandingDto {
  behaviour: ReadingBehaviour
  backlog: BacklogDto
  midway: MidwaySeriesDto[]
  creators: CreatorReturnDto[]
  /** Null without a MangaBaka dump, or with fewer than three rated series. */
  ratings: RatingGapDto | null
  seriesFullyRead: number
}

/** Last 30 days of the download queue. `name` is a source name, or a protocol when there is none. */
export interface SourceReliabilityDto {
  name: string
  completed: number
  failed: number
  medianSecondsToComplete: number | null
}

/** `allUsers` is true for an admin, whose numbers cover everybody's requests. */
export interface RequestSummaryDto {
  allUsers: boolean
  open: number
  resolved90d: number
  medianResolveHours: number | null
}

/**
 * from/to are inclusive local dates, the same window `useActivityStats` is given. The UTC offset is
 * the fallback for a reader who has never stored a time zone.
 */
export function useStatsInsights(from: string, to: string, userId?: number, enabled = true) {
  return useQuery({
    queryKey: ['stats', 'insights', from, to, userId ?? 'me'],
    queryFn: () =>
      api<StatsInsightsDto>(
        `/stats/insights?from=${from}&to=${to}&utcOffsetMinutes=${new Date().getTimezoneOffset()}` +
          (userId ? `&userId=${userId}` : ''),
      ),
    enabled,
    placeholderData: keepPreviousData,
  })
}

export function useStatsStanding(userId?: number, enabled = true) {
  return useQuery({
    queryKey: ['stats', 'standing', userId ?? 'me'],
    queryFn: () => api<StatsStandingDto>(`/stats/standing${userId ? `?userId=${userId}` : ''}`),
    enabled,
    staleTime: 5 * 60 * 1000,
  })
}
