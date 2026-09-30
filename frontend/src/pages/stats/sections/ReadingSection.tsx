import { Skeleton, SegmentedControl, SimpleGrid, Stack, Group } from '@mantine/core'
import { BarChart } from '@mantine/charts'
import {
  IconBook2,
  IconBookmark,
  IconCalendarStats,
  IconClock,
  IconHistory,
  IconHourglassLow,
  IconTrophy,
} from '@tabler/icons-react'
import { Trans, Plural, Select, useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'
import { useState } from 'react'
import { useActivityStats, type ActivityTotals } from '../../../api/hooks'
import { useStatsInsights, useStatsStanding } from '../../../api/stats'
import { EmptyState } from '../../../components/ui/EmptyState'
import { Panel } from '../../../components/ui/Panel'
import { SectionHeader } from '../../../components/ui/SectionHeader'
import { formatDate, formatHour, formatMonthBucket, formatNumber, formatReadingTime, monthName } from '../../../format'
import { ActivityFeed } from '../ActivityFeed'
import { ChartSkeleton } from '../ChartSkeleton'
import { MidwayList } from '../MidwayList'
import { RankList } from '../RankList'
import { delta, type DateRange } from '../StatsRange'
import { StatsInsight } from '../StatsSection'
import type { StatsSectionProps } from './types'

/** "2026-03" → "Mar 26"; "2026-03-14" → "14 Mar". */
function bucketLabel(bucket: string): string {
  const parts = bucket.split('-')
  if (parts.length === 2) return formatMonthBucket(bucket)
  const month = monthName(Number(parts[1]), 'short')
  return `${Number(parts[2])} ${month}`
}

/** Inclusive day count of a local-date range, for "12 of 30 days active". */
function daysInRange({ from, to }: DateRange): number {
  const f = new Date(`${from}T00:00:00`)
  const d = new Date(`${to}T00:00:00`)
  return Math.round((d.getTime() - f.getTime()) / 86_400_000) + 1
}

function ReadingSkeleton() {
  return (
    <Stack gap="lg" aria-hidden>
      <Panel p={0}>
        <div className="stats-figures">
          {[58, 72, 50, 64, 80, 68].map((width) => (
            <div className="stats-figure" key={width}>
              <Skeleton h={26} w={width} my={4} />
              <Skeleton h={8} w={width + 16} mt={6} />
            </div>
          ))}
        </div>
      </Panel>
      <div className="stats-grid-main">
        <Panel p="md">
          <ChartSkeleton h={240} />
        </Panel>
        <Panel p="md">
          <ChartSkeleton h={240} />
        </Panel>
      </div>
    </Stack>
  )
}

/** The opening sentence: how much got read, over how many days, and against last time. */
function ReadingInsight({
  totals,
  prevTotals,
  primeStartHour,
}: {
  totals: ActivityTotals
  prevTotals: ActivityTotals | undefined
  primeStartHour: number | null
}) {
  if (totals.chaptersRead === 0 && totals.readingSeconds === 0) {
    return null
  }

  const chapters = totals.chaptersRead
  const days = totals.daysActive
  const hour = primeStartHour !== null ? formatHour(primeStartHour) : null

  let kind: 'up' | 'down' | 'flat' | 'none' = 'none'
  let pct = 0
  if (prevTotals) {
    const d = delta(chapters, prevTotals.chaptersRead)
    if (d === null) {
      kind = 'none'
    } else if (d === 0) {
      kind = 'flat'
    } else {
      kind = d > 0 ? 'up' : 'down'
      pct = Math.abs(Math.round(d * 100))
    }
  }

  const trend = (
    <span className="stats-insight-dim">
      <Select
        value={kind}
        _up={`Up ${pct}% on the period before.`}
        _down={`Down ${pct}% on the period before.`}
        _flat="About the same as the period before."
        _none=""
        other=""
      />
    </span>
  )

  return (
    <StatsInsight>
      {hour !== null ? (
        <Trans>
          <em>
            <Plural value={chapters} one="# chapter" other="# chapters" />
          </em>{' '}
          across <Plural value={days} one="# reading day" other="# reading days" />
          <span className="stats-insight-dim">
            , most of it after <em>{hour}</em>
          </span>
          .{' '}
          {trend}
        </Trans>
      ) : (
        <Trans>
          <em>
            <Plural value={chapters} one="# chapter" other="# chapters" />
          </em>{' '}
          across <Plural value={days} one="# reading day" other="# reading days" />.{' '}
          {trend}
        </Trans>
      )}
    </StatsInsight>
  )
}

export default function ReadingSection({ userId, range, previous, windowLabel }: StatsSectionProps) {
  const { t } = useLingui()
  const [metric, setMetric] = useState<'chapters' | 'time'>('chapters')

  const { data: activity } = useActivityStats(range.from, range.to, userId)
  const { data: prevActivity } = useActivityStats(
    previous?.from ?? range.from,
    previous?.to ?? range.to,
    userId,
    previous !== null,
  )
  const { data: insights } = useStatsInsights(range.from, range.to, userId, !!activity?.readTrackingAvailable)
  const { data: standing } = useStatsStanding(userId, !!activity?.readTrackingAvailable)

  if (!activity) {
    return <ReadingSkeleton />
  }

  if (!activity.readTrackingAvailable) {
    return (
      <EmptyState
        compact
        title={t`Reading stats need Kavita`}
        description={t`Connect it in Settings and Maki will start tracking chapters you read. Downloads and library changes are tracked either way.`}
      />
    )
  }

  const comparing = previous !== null && prevActivity !== undefined
  let deltaLabel: string | undefined
  if (previous) {
    const fromLabel = formatDate(previous.from)
    const toLabel = formatDate(previous.to)
    deltaLabel = t`vs ${fromLabel} to ${toLabel}`
  }
  const compare = (pick: (totals: ActivityTotals) => number) =>
    comparing ? delta(pick(activity.totals), pick(prevActivity!.totals)) : undefined

  const windowDays = daysInRange(range)
  const primeStartHour =
    insights && insights.rhythm.totalSeconds > 0 ? insights.rhythm.primeStartHour : null

  const figures: { key: string; label: string; value: string; delta?: number | null }[] = [
    {
      key: 'chapters',
      label: t`Chapters`,
      value: formatNumber(activity.totals.chaptersRead),
      delta: compare((p) => p.chaptersRead),
    },
    {
      key: 'time',
      label: t`Time read`,
      value: formatReadingTime(activity.totals.readingSeconds),
      delta: compare((p) => p.readingSeconds),
    },
    {
      key: 'pages',
      label: t`Pages`,
      value: formatNumber(activity.totals.pagesRead),
      delta: compare((p) => p.pagesRead),
    },
    {
      key: 'days',
      label: t`Days active`,
      value: `${formatNumber(activity.totals.daysActive)}/${formatNumber(windowDays)}`,
      delta: compare((p) => p.daysActive),
    },
    {
      key: 'started',
      label: t`Started`,
      value: formatNumber(activity.totals.seriesStarted),
      delta: compare((p) => p.seriesStarted),
    },
    {
      key: 'finished',
      label: t`Finished`,
      value: formatNumber(activity.totals.seriesFinished),
      delta: compare((p) => p.seriesFinished),
    },
  ]

  const timelineData = activity.timeline.map((p) => ({
    bucket: bucketLabel(p.bucket),
    Chapters: p.chaptersRead,
    Hours: Math.round((p.readingSeconds / 3600) * 10) / 10,
  }))

  const midway = standing?.midway ?? []
  const hasFeed =
    activity.finished.length + activity.added.length + activity.removed.length + activity.dropped.length > 0

  return (
    <>
      <ReadingInsight
        totals={activity.totals}
        prevTotals={comparing ? prevActivity!.totals : undefined}
        primeStartHour={primeStartHour}
      />
      <Stack gap="lg">
        <Panel p={0}>
          <div className="stats-figures">
            {figures.map((f) => (
              <div className="stats-figure" key={f.key}>
                <div className="stats-figure-value">
                  <span className="stats-figure-n tnum">{f.value}</span>
                  {f.delta !== undefined && (
                    <span
                      className="stats-figure-delta tnum"
                      data-tone={f.delta === null || f.delta === 0 ? 'flat' : f.delta > 0 ? 'up' : 'down'}
                      title={deltaLabel}
                    >
                      {f.delta === null
                        ? t`no baseline`
                        : `${f.delta > 0 ? '+' : ''}${Math.round(f.delta * 100)}%`}
                    </span>
                  )}
                </div>
                <span className="stats-figure-l">{f.label}</span>
              </div>
            ))}
          </div>
        </Panel>

        <div className="stats-grid-main">
          <Panel p="md">
            <Group justify="space-between" align="flex-start" mb="sm">
              <p className="stats-panel-title">
                <IconCalendarStats size={16} style={{ color: 'var(--brand)' }} />
                {t`Day by day`}
              </p>
              <SegmentedControl
                size="xs"
                value={metric}
                onChange={(v) => setMetric(v as 'chapters' | 'time')}
                data={[
                  { value: 'chapters', label: t`Chapters` },
                  { value: 'time', label: t`Time` },
                ]}
              />
            </Group>
            {timelineData.length === 0 ? (
              <p className="stats-row-sub" style={{ margin: 0 }}>
                {t`No activity in ${windowLabel}.`}
              </p>
            ) : (
              <BarChart
                h={240}
                data={timelineData}
                dataKey="bucket"
                tickLine="none"
                gridAxis="y"
                unit={metric === 'time' ? t`h` : undefined}
                series={
                  metric === 'chapters'
                    ? [{ name: 'Chapters', label: t`Chapters`, color: 'var(--brand)' }]
                    : [{ name: 'Hours', label: t`Hours`, color: 'var(--brand)' }]
                }
              />
            )}
          </Panel>

          <Panel p="md">
            <p className="stats-panel-title">
              <IconBookmark size={16} style={{ color: 'var(--brand)' }} />
              {t`Mid-way`}
            </p>
            <MidwayList items={midway} emptyText={t`Nothing sits half read right now.`} />
          </Panel>
        </div>

        <div>
          <SectionHeader icon={IconTrophy} title={t`What you read`} />
          <SimpleGrid cols={{ base: 1, lg: activity.topByTime.length > 0 ? 3 : 2 }} spacing="lg">
            <RankList
              icon={IconBook2}
              title={t`Most read`}
              items={activity.topRead.map((s) => {
                const { count } = s
                return { ...s, value: plural(count, { one: '# ch', other: '# ch' }) }
              })}
              emptyText={t`No chapters read in this period.`}
            />
            {activity.topByTime.length > 0 && (
              <RankList
                icon={IconClock}
                title={t`Where the time went`}
                items={activity.topByTime.map((s) => ({ ...s, value: formatReadingTime(s.seconds) }))}
                emptyText={t`No reading time recorded.`}
              />
            )}
            <RankList
              icon={IconHourglassLow}
              title={t`Barely touched`}
              items={activity.leastRead.map((s) => {
                const { count } = s
                return { ...s, value: plural(count, { one: '# ch', other: '# ch' }) }
              })}
              emptyText={t`Everything you started, you kept reading.`}
            />
          </SimpleGrid>
        </div>

        {hasFeed && (
          <div>
            <SectionHeader icon={IconHistory} title={t`Activity feed`} />
            <ActivityFeed stats={activity} />
          </div>
        )}
      </Stack>
    </>
  )
}
