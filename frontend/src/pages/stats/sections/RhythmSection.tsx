import { useMemo } from 'react'
import { Skeleton } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { useActivityStats, useProgressSummary, useReadingHeatmap } from '../../../api/hooks'
import { useStatsInsights, useStatsStanding } from '../../../api/stats'
import { EmptyState } from '../../../components/ui/EmptyState'
import { Panel } from '../../../components/ui/Panel'
import { formatHour, formatNumber, formatPercent, formatReadingTime, weekdayName } from '../../../format'
import { ChartSkeleton } from '../ChartSkeleton'
import { HourMatrix } from '../charts/HourMatrix'
import { ReadingHeatmap } from '../ReadingHeatmap'
import { StatsInsight } from '../StatsSection'
import type { StatsSectionProps } from './types'

/** Sum of the 24 hourly buckets belonging to one weekday. */
function daySeconds(secondsByWeekdayHour: number[], day: number): number {
  let sum = 0
  for (let h = 0; h < 24; h++) sum += secondsByWeekdayHour[day * 24 + h] ?? 0
  return sum
}

export default function RhythmSection({ userId, range, windowLabel }: StatsSectionProps) {
  const { t, i18n } = useLingui()

  const { data: activity } = useActivityStats(range.from, range.to, userId)
  const { data: insights, isLoading } = useStatsInsights(
    range.from,
    range.to,
    userId,
    !!activity?.readTrackingAvailable,
  )
  const { data: standing } = useStatsStanding(userId, !!activity?.readTrackingAvailable)
  const { data: summary } = useProgressSummary(userId)
  const progressOn = summary?.enabled === true
  const { data: heatmap } = useReadingHeatmap(userId, progressOn)

  const insight = useMemo(() => {
    const rhythm = insights?.rhythm
    if (!rhythm || rhythm.totalSeconds === 0 || rhythm.primeStartHour === null) return null

    const start = formatHour(rhythm.primeStartHour)
    const end = formatHour((rhythm.primeStartHour + 3) % 24)
    const weekendShare = formatPercent(rhythm.weekendShare ?? 0)
    const sittingSeconds = insights?.sittings?.medianSeconds
    const sittingDuration = sittingSeconds !== undefined ? formatReadingTime(sittingSeconds) : ''

    return (
      <>
        <Trans>
          Most of your reading happens between <em>{start}</em> and <em>{end}</em>, and{' '}
          <em>{weekendShare}</em> of it lands on weekends.
        </Trans>
        {sittingSeconds !== undefined && (
          <>
            {' '}
            <Trans>
              A typical sitting runs <em>{sittingDuration}</em>.
            </Trans>
          </>
        )}
      </>
    )
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [insights, i18n.locale])

  if (activity && !activity.readTrackingAvailable) {
    return (
      <EmptyState
        compact
        title={t`Reading stats need Kavita`}
        description={t`Connect it in Settings and Maki will start tracking chapters you read. Downloads and library changes are tracked either way.`}
      />
    )
  }

  if (isLoading || !insights) {
    return (
      <div className="stats-grid-main" aria-hidden>
        <Panel p="md">
          <p className="stats-panel-title">{t`Hour of day and weekday`}</p>
          <ChartSkeleton h={200} />
        </Panel>
        <Panel p="md">
          <p className="stats-panel-title">{t`Habits in numbers`}</p>
          <Skeleton h={160} />
        </Panel>
      </div>
    )
  }

  const { rhythm, sittings, bookmarksAdded } = insights

  if (rhythm.totalSeconds === 0) {
    return (
      <EmptyState
        compact
        title={t`No timed reading yet`}
        description={t`Only Maki's reader records when you read. Chapters marked read from Kavita or OPDS apps do not carry a time.`}
      />
    )
  }

  const busiestShare =
    rhythm.busiestWeekday !== null
      ? daySeconds(rhythm.secondsByWeekdayHour, rhythm.busiestWeekday) / rhythm.totalSeconds
      : null

  const medianSecondsPerChapter = standing?.behaviour.medianSecondsPerChapter ?? null

  const primeStart = rhythm.primeStartHour !== null ? formatHour(rhythm.primeStartHour) : null
  const primeEnd = rhythm.primeStartHour !== null ? formatHour((rhythm.primeStartHour + 3) % 24) : null
  const primeShare = rhythm.primeShare !== null ? formatPercent(rhythm.primeShare) : null
  const busiestShareLabel = busiestShare !== null ? formatPercent(busiestShare) : null
  const longestSitting = sittings ? formatReadingTime(sittings.longestSeconds) : null
  const minutesPerChapter = medianSecondsPerChapter !== null ? Math.round(medianSecondsPerChapter / 60) : null
  const perSittingMedian = sittings ? sittings.chaptersPerSittingMedian : null

  const bookmarksPanel = (
    <Panel p="md">
      <p className="stats-panel-title">{t`Bookmarks`}</p>
      <p className="stats-panel-sub">{t`Added in ${windowLabel}`}</p>
      <span className="stats-fact-value" style={{ fontSize: '2rem' }}>
        {formatNumber(bookmarksAdded)}
      </span>
    </Panel>
  )

  return (
    <>
      {insight && <StatsInsight>{insight}</StatsInsight>}

      <div className="stats-grid-main">
        <Panel p="md">
          <p className="stats-panel-title">{t`Hour of day and weekday`}</p>
          <HourMatrix secondsByWeekdayHour={rhythm.secondsByWeekdayHour} />
        </Panel>

        <Panel p="md">
          <p className="stats-panel-title">{t`Habits in numbers`}</p>
          <div className="stats-facts">
            <div className="stats-fact">
              <span className="stats-fact-label">{t`Prime time`}</span>
              <span className="stats-fact-value">
                {primeStart !== null && primeEnd !== null ? (
                  <Trans>
                    {primeStart} to {primeEnd}
                  </Trans>
                ) : (
                  '-'
                )}
              </span>
              <span className="stats-fact-hint">
                {primeShare !== null ? t`${primeShare} of minutes` : '-'}
              </span>
            </div>

            <div className="stats-fact">
              <span className="stats-fact-label">{t`Busiest day`}</span>
              <span className="stats-fact-value">
                {rhythm.busiestWeekday !== null ? weekdayName(rhythm.busiestWeekday, 'long') : '-'}
              </span>
              <span className="stats-fact-hint">
                {busiestShareLabel !== null ? t`${busiestShareLabel} of your time` : '-'}
              </span>
            </div>

            <div className="stats-fact">
              <span className="stats-fact-label">{t`Typical sitting`}</span>
              <span className="stats-fact-value">
                {sittings ? formatReadingTime(sittings.medianSeconds) : '-'}
              </span>
              <span className="stats-fact-hint">
                {longestSitting !== null ? t`longest ${longestSitting}` : '-'}
              </span>
            </div>

            <div className="stats-fact">
              <span className="stats-fact-label">{t`Pace`}</span>
              <span className="stats-fact-value">
                {minutesPerChapter !== null ? t`${minutesPerChapter} min / ch` : '-'}
              </span>
              <span className="stats-fact-hint">{medianSecondsPerChapter !== null ? t`median` : '-'}</span>
            </div>

            <div className="stats-fact">
              <span className="stats-fact-label">{t`Weekend share`}</span>
              <span className="stats-fact-value">
                {rhythm.weekendShare !== null ? formatPercent(rhythm.weekendShare) : '-'}
              </span>
              <span className="stats-fact-hint">{t`of reading time`}</span>
            </div>

            <div className="stats-fact">
              <span className="stats-fact-label">{t`Sittings per week`}</span>
              <span className="stats-fact-value">
                {sittings ? sittings.perWeek.toFixed(1) : '-'}
              </span>
              <span className="stats-fact-hint">
                {perSittingMedian !== null ? t`${perSittingMedian} ch per sitting` : '-'}
              </span>
            </div>
          </div>
        </Panel>
      </div>

      {progressOn && heatmap && heatmap.length > 0 ? (
        <div className="stats-grid-main">
          <ReadingHeatmap days={heatmap} />
          {bookmarksPanel}
        </div>
      ) : (
        bookmarksPanel
      )}
    </>
  )
}
