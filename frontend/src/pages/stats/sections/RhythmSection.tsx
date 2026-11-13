import { useMemo } from 'react'
import { Skeleton } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { useActivityStats, useProgressSummary, useReadingHeatmap } from '../../../api/hooks'
import { useStatsInsights, useStatsStanding } from '../../../api/stats'
import { EmptyState } from '../../../components/ui/EmptyState'
import { FigureStrip } from '../../../components/ui/FigureStrip'
import { Panel } from '../../../components/ui/Panel'
import { PanelTitle } from '../../../components/ui/PanelTitle'
import { formatDecimal, formatHour, formatNumber, formatPercent, formatTimeSpent, weekdayName } from '../../../format'
import { ReadTrackingEmpty } from '../ReadTrackingEmpty'
import { ChartSkeleton } from '../ChartSkeleton'
import { HourMatrix } from '../charts/HourMatrix'
import { ReadingHeatmap } from '../ReadingHeatmap'
import { StatsInsight } from '../StatsSection'
import { MIN_SITTINGS, MIN_TIMED_SECONDS } from '../sampleSize'
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
    const sittingCount = insights?.sittings?.count ?? 0
    if (!rhythm || rhythm.primeStartHour === null) return null
    if (rhythm.totalSeconds < MIN_TIMED_SECONDS || sittingCount < MIN_SITTINGS) return null

    const start = formatHour(rhythm.primeStartHour)
    const end = formatHour((rhythm.primeStartHour + 3) % 24)
    const weekendShare = formatPercent(rhythm.weekendShare ?? 0)
    const sittingSeconds = insights?.sittings?.medianSeconds
    const sittingDuration = sittingSeconds !== undefined ? formatTimeSpent(sittingSeconds) : ''

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
      <ReadTrackingEmpty />
    )
  }

  if (isLoading || !insights) {
    return (
      <div className="stats-grid-main" aria-hidden>
        <Panel p="md">
          <PanelTitle>{t`Hour of day and weekday`}</PanelTitle>
          <ChartSkeleton h={200} />
        </Panel>
        <Panel p="md">
          <PanelTitle>{t`Habits in numbers`}</PanelTitle>
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
        mood="asleep"
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
  const longestSitting = sittings ? formatTimeSpent(sittings.longestSeconds) : null
  const minutesPerChapter = medianSecondsPerChapter !== null ? Math.round(medianSecondsPerChapter / 60) : null
  const perSittingMedian = sittings ? sittings.chaptersPerSittingMedian : null
  const perSitting = perSittingMedian === null ? '' : formatDecimal(perSittingMedian)

  const bookmarksPanel = (
    <Panel p="md">
      <PanelTitle>{t`Bookmarks`}</PanelTitle>
      <p className="stats-panel-sub">{t`Added in ${windowLabel}`}</p>
      <span className="figure" style={{ fontSize: '2rem', color: 'var(--ink-hi)' }}>
        {formatNumber(bookmarksAdded)}
      </span>
    </Panel>
  )

  return (
    <>
      {insight && <StatsInsight>{insight}</StatsInsight>}

      <div className="stats-grid-main">
        <Panel p="md">
          <PanelTitle>{t`Hour of day and weekday`}</PanelTitle>
          <HourMatrix secondsByWeekdayHour={rhythm.secondsByWeekdayHour} />
        </Panel>

        <Panel p="md">
          <PanelTitle>{t`Habits in numbers`}</PanelTitle>
          <FigureStrip
            flush
            figures={[
              {
                label: t`Prime time`,
                value:
                  primeStart !== null && primeEnd !== null ? (
                    <Trans>
                      {primeStart} to {primeEnd}
                    </Trans>
                  ) : (
                    '-'
                  ),
                hint: primeShare !== null ? t`${primeShare} of minutes` : '-',
              },
              {
                label: t`Busiest day`,
                value: rhythm.busiestWeekday !== null ? weekdayName(rhythm.busiestWeekday, 'long') : '-',
                hint: busiestShareLabel !== null ? t`${busiestShareLabel} of your time` : '-',
              },
              {
                label: t`Typical sitting`,
                value: sittings ? formatTimeSpent(sittings.medianSeconds) : '-',
                hint: longestSitting !== null ? t`longest ${longestSitting}` : '-',
              },
              {
                label: t`Pace`,
                value: minutesPerChapter !== null ? t`${minutesPerChapter} min / ch.` : '-',
                hint: medianSecondsPerChapter !== null ? t`median` : '-',
              },
              {
                label: t`Weekend share`,
                value: rhythm.weekendShare !== null ? formatPercent(rhythm.weekendShare) : '-',
                hint: t`of reading time`,
              },
              {
                label: t`Sittings per week`,
                value: sittings ? formatDecimal(sittings.perWeek) : '-',
                hint:
                  perSittingMedian !== null && perSittingMedian > 0
                    ? t`${perSitting} ch. per sitting`
                    : undefined,
              },
            ]}
          />
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
