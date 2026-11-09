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
} from '../../../lib/icons'
import { Trans, Plural, Select, useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'
import { useState } from 'react'
import { useActivityStats, type ActivityTotals } from '../../../api/hooks'
import { useStatsInsights, useStatsStanding } from '../../../api/stats'
import { EmptyState } from '../../../components/ui/EmptyState'
import { FigureStrip, type Figure } from '../../../components/ui/FigureStrip'
import { Panel } from '../../../components/ui/Panel'
import { SectionHeader } from '../../../components/ui/SectionHeader'
import {
  formatCalendarDate,
  formatHour,
  formatNumber,
  formatPercent,
  formatTimeSpent,
  formatStatsBucket,
} from '../../../format'
import { ActivityFeed } from '../ActivityFeed'
import { ReadTrackingEmpty } from '../ReadTrackingEmpty'
import { ChartSkeleton } from '../ChartSkeleton'
import { MidwayList } from '../MidwayList'
import { RankList } from '../RankList'
import { delta, type DateRange } from '../StatsRange'
import { deltaHasBase } from '../sampleSize'
import { StatsInsight } from '../StatsSection'
import type { StatsSectionProps } from './types'

/** Inclusive day count of a local-date range, for "12 of 30 days active". */
function daysInRange({ from, to }: DateRange): number {
  const f = new Date(`${from}T00:00:00`)
  const d = new Date(`${to}T00:00:00`)
  return Math.round((d.getTime() - f.getTime()) / 86_400_000) + 1
}

const pad = (n: number) => String(n).padStart(2, '0')
const dayKey = (d: Date) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`

/**
 * Every bucket from the window's start to today (or its end), filled with zeros where nothing
 * happened. The server only returns buckets with events, and a chart of those spaced Oct 13, 19, 23
 * and 24 evenly along its axis. Day buckets mirror ActivityStatsService.TimelineDayBucketMaxDays.
 */
function bucketsFor(range: DateRange, sample: string | undefined): string[] {
  const months = sample ? sample.split('-').length === 2 : daysInRange(range) > 62
  const start = new Date(`${range.from}T00:00:00`)
  const today = new Date()
  const endDate = new Date(`${range.to}T00:00:00`)
  const end = endDate > today ? today : endDate
  const keys: string[] = []
  if (months) {
    for (let d = new Date(start.getFullYear(), start.getMonth(), 1); d <= end; d.setMonth(d.getMonth() + 1)) {
      keys.push(`${d.getFullYear()}-${pad(d.getMonth() + 1)}`)
    }
  } else {
    for (let d = new Date(start); d <= end; d.setDate(d.getDate() + 1)) keys.push(dayKey(d))
  }
  return keys
}

/**
 * Close to the loaded section's height down to "What you read", the part on the first screen: a
 * short placeholder pushed every section below it 600px down as the data arrived.
 */
function ReadingSkeleton() {
  const { t } = useLingui()
  return (
    <>
      <div className="stats-insight" aria-hidden>
        <Skeleton h="0.8em" w="75%" my="0.2em" />
        <Skeleton h="0.8em" w="40%" my="0.2em" />
      </div>
      <Stack gap="lg" aria-hidden>
        <FigureStrip
          loading
          className="stats-figures"
          figures={[t`Chapters`, t`Time read`, t`Pages`, t`Days active`, t`Started`, t`Finished`].map(
            (label) => ({ label, value: 0 }),
          )}
        />
        <div className="stats-grid-main">
          {[0, 1].map((i) => (
            <Panel p="md" key={i}>
              <Skeleton h={18} w={120} mb="md" />
              <ChartSkeleton h={290} />
            </Panel>
          ))}
        </div>
        <div>
          <SectionHeader icon={IconTrophy} title={t`What you read`} />
          <SimpleGrid cols={{ base: 1, lg: 3 }} spacing="lg">
            {[0, 1, 2].map((i) => (
              <Panel p="md" key={i}>
                <Skeleton h={300} />
              </Panel>
            ))}
          </SimpleGrid>
        </div>
      </Stack>
    </>
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
  // Time without a finished chapter: the full sentence came out as "0 chapters across 0 reading
  // days, most of it after 9:00 AM. Down 100% on the period before."
  if (totals.chaptersRead === 0) {
    const time = formatTimeSpent(totals.readingSeconds)
    return (
      <StatsInsight>
        <Trans>
          <em>{time}</em> of reading<span className="stats-insight-dim">, no chapter finished yet.</span>
        </Trans>
      </StatsInsight>
    )
  }

  const chapters = totals.chaptersRead
  const days = totals.daysActive
  const hour = primeStartHour !== null ? formatHour(primeStartHour) : null

  let kind: 'up' | 'down' | 'flat' | 'none' = 'none'
  let pct = ''
  if (prevTotals && deltaHasBase('chaptersRead', prevTotals.chaptersRead)) {
    const d = delta(chapters, prevTotals.chaptersRead)
    if (d === null) {
      kind = 'none'
    } else {
      const whole = Math.round(Math.abs(d) * 100)
      if (whole === 0) {
        kind = 'flat'
      } else {
        kind = d > 0 ? 'up' : 'down'
        pct = formatPercent(whole / 100)
      }
    }
  }

  const trend = (
    <span className="stats-insight-dim">
      <Select
        value={kind}
        _up={`Up ${pct} on the period before.`}
        _down={`Down ${pct} on the period before.`}
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
      <ReadTrackingEmpty />
    )
  }

  const comparing = previous !== null && prevActivity !== undefined
  let deltaLabel: string | undefined
  if (previous) {
    const fromLabel = formatCalendarDate(previous.from)
    const toLabel = formatCalendarDate(previous.to)
    deltaLabel = t`vs ${fromLabel} to ${toLabel}`
  }
  const compare = (metric: keyof ActivityTotals) =>
    comparing && deltaHasBase(metric, prevActivity!.totals[metric])
      ? (delta(activity.totals[metric], prevActivity!.totals[metric]) ?? undefined)
      : undefined

  const windowDays = daysInRange(range)
  const primeStartHour =
    insights && insights.rhythm.totalSeconds > 0 ? insights.rhythm.primeStartHour : null

  const figures: Figure[] = [
    {
      zero: activity.totals.chaptersRead === 0,
      label: t`Chapters`,
      value: formatNumber(activity.totals.chaptersRead),
      delta: compare('chaptersRead'),
    },
    {
      zero: activity.totals.readingSeconds === 0,
      label: t`Time read`,
      value: formatTimeSpent(activity.totals.readingSeconds),
      delta: compare('readingSeconds'),
    },
    {
      zero: activity.totals.pagesRead === 0,
      label: t`Pages`,
      value: formatNumber(activity.totals.pagesRead),
      delta: compare('pagesRead'),
    },
    {
      zero: activity.totals.daysActive === 0,
      label: t`Days active`,
      value: `${formatNumber(activity.totals.daysActive)}/${formatNumber(windowDays)}`,
      delta: compare('daysActive'),
    },
    {
      zero: activity.totals.seriesStarted === 0,
      label: t`Started`,
      value: formatNumber(activity.totals.seriesStarted),
      delta: compare('seriesStarted'),
    },
    {
      zero: activity.totals.seriesFinished === 0,
      label: t`Finished`,
      value: formatNumber(activity.totals.seriesFinished),
      delta: compare('seriesFinished'),
    },
  ]

  const byBucket = new Map(activity.timeline.map((p) => [p.bucket, p]))
  const timelineData = bucketsFor(range, activity.timeline[0]?.bucket).map((bucket) => {
    const p = byBucket.get(bucket)
    return {
      bucket: formatStatsBucket(bucket),
      Chapters: p?.chaptersRead ?? 0,
      Hours: p ? Math.round((p.readingSeconds / 3600) * 10) / 10 : 0,
    }
  })
  const timelineEmpty = timelineData.every((p) => (metric === 'time' ? p.Hours === 0 : p.Chapters === 0))

  const midway = standing?.midway ?? []
  const chapterCount = ({ count }: { count: number }) => plural(count, { one: '# ch.', other: '# ch.' })
  const ranks = [
    {
      key: 'most',
      icon: IconBook2,
      title: t`Most read`,
      items: activity.topRead.map((s) => ({ ...s, value: chapterCount(s) })),
      emptyText: t`No chapters finished in this period.`,
    },
    ...(activity.topByTime.length > 0
      ? [
          {
            key: 'time',
            icon: IconClock,
            title: t`Where the time went`,
            items: activity.topByTime.map((s) => ({ ...s, value: formatTimeSpent(s.seconds) })),
            emptyText: t`No reading time recorded.`,
          },
        ]
      : []),
    {
      key: 'least',
      icon: IconHourglassLow,
      title: t`Barely touched`,
      items: activity.leastRead.map((s) => ({ ...s, value: chapterCount(s) })),
      emptyText: t`Everything you started, you kept reading.`,
    },
  ]
  const filledRanks = ranks.filter((r) => r.items.length > 0)
  const emptyRanks = ranks.filter((r) => r.items.length === 0)
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
        <FigureStrip figures={figures} deltaLabel={deltaLabel} className="stats-figures" />

        {/* Empty, the chart is one line, so it takes the full width and Mid-way goes under it instead
            of leaving a 350px box (or a hole) beside the list. */}
        <div className="stats-grid-main" data-empty={timelineEmpty || undefined}>
          <Panel p="md">
            <Group justify="space-between" align="flex-start" mb={timelineEmpty ? 4 : 'sm'}>
              <p className="stats-panel-title">
                <IconCalendarStats size={16} style={{ color: 'var(--brand)' }} />
                {t`Day by day`}
              </p>
              <SegmentedControl
                aria-label={t`Day by day`}
                size="xs"
                value={metric}
                onChange={(v) => setMetric(v as 'chapters' | 'time')}
                data={[
                  { value: 'chapters', label: t`Chapters` },
                  { value: 'time', label: t`Time` },
                ]}
              />
            </Group>
            {timelineEmpty ? (
              <EmptyState
                compact
                mood="asleep"
                title={
                  activity.totals.chaptersRead === 0 && activity.totals.readingSeconds === 0
                    ? t`No activity in ${windowLabel}.`
                    : metric === 'time'
                      ? t`No reading time recorded.`
                      : t`No chapters finished in this period.`
                }
              />
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
          {filledRanks.length > 0 && (
            <SimpleGrid cols={{ base: 1, lg: filledRanks.length }} spacing="lg">
              {filledRanks.map(({ key, ...rank }) => (
                <RankList key={key} {...rank} />
              ))}
            </SimpleGrid>
          )}
          {emptyRanks.length > 0 && (
            <SimpleGrid cols={{ base: 1, lg: emptyRanks.length }} spacing="lg" mt={filledRanks.length > 0 ? 'lg' : 0}>
              {emptyRanks.map(({ key, ...rank }) => (
                <RankList key={key} {...rank} />
              ))}
            </SimpleGrid>
          )}
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
