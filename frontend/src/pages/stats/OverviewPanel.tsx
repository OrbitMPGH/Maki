import { useMemo, useState } from 'react'
import {
  Alert,
  Group,
  Loader,
  SegmentedControl,
  Select,
  SimpleGrid,
  Stack,
  Text,
} from '@mantine/core'
import { AreaChart, DonutChart } from '@mantine/charts'
import {
  IconBook2,
  IconCalendarStats,
  IconChecks,
  IconClock,
  IconDownload,
  IconHistory,
  IconHourglassLow,
  IconInfoCircle,
  IconPlus,
  IconTrophy,
} from '@tabler/icons-react'
import { Trans, useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'
import { useProgressSummary, useReadingHeatmap, useActivityStats } from '../../api/hooks'
import { EmptyState } from '../../components/ui/EmptyState'
import { useLabel } from '../../i18n-context'
import { Panel } from '../../components/ui/Panel'
import { SectionHeader } from '../../components/ui/SectionHeader'
import { StatTile } from '../../components/ui/StatTile'
import { TagChip, TagChips } from '../../components/ui/TagChip'
import { formatReadingTime, monthName } from '../../format'
import { ActivityFeed } from './ActivityFeed'
import { ProgressStrip } from './ProgressStrip'
import { RankList } from './RankList'
import { ReadingHeatmap } from './ReadingHeatmap'
import {
  RANGE_OPTIONS,
  delta,
  previousRange,
  rangeLabel,
  resolveRange,
  type RangePreset,
} from './StatsRange'

const GENRE_COLORS = ['var(--brand)', 'var(--info)', 'var(--ok)', 'var(--warn)', 'var(--danger)']

/** "2026-03" → "Mar", "2026-03-14" → "14 Mar". */
function bucketLabel(bucket: string): string {
  const parts = bucket.split('-')
  const month = monthName(Number(parts[1]), 'short') || bucket
  return parts.length === 3 ? `${Number(parts[2])} ${month}` : month
}

export function OverviewPanel({
  userId,
  preset,
  onPresetChange,
  year,
  onYearChange,
  month,
  onMonthChange,
  yearOptions,
  earliestYear,
  onOpenAchievements,
}: {
  userId?: number
  preset: RangePreset
  onPresetChange: (preset: RangePreset) => void
  year: number
  onYearChange: (year: number) => void
  month: number | null
  onMonthChange: (month: number | null) => void
  yearOptions: string[]
  earliestYear: number
  onOpenAchievements: () => void
}) {
  const [metric, setMetric] = useState<'chapters' | 'time'>('chapters')
  const { t, i18n } = useLingui()
  const label = useLabel()

  const range = useMemo(
    () => resolveRange(preset, year, month, earliestYear),
    [preset, year, month, earliestYear],
  )
  const previous = useMemo(() => previousRange(preset, range), [preset, range])

  const { data: stats, isLoading } = useActivityStats(range.from, range.to, userId)
  const { data: prevStats } = useActivityStats(
    previous?.from ?? range.from,
    previous?.to ?? range.to,
    userId,
    previous !== null,
  )

  const { data: summary } = useProgressSummary(userId)
  const progressOn = summary?.enabled === true
  const { data: heatmap } = useReadingHeatmap(userId, progressOn)

  const hasAnything =
    stats &&
    (stats.totals.chaptersRead > 0 ||
      stats.totals.volumesRead > 0 ||
      stats.totals.readingSeconds > 0 ||
      stats.totals.chaptersDownloaded > 0 ||
      stats.totals.seriesAdded > 0 ||
      stats.totals.seriesRemoved > 0)

  const timelineData = useMemo(
    () =>
      (stats?.timeline ?? []).map((p) => ({
        bucket: bucketLabel(p.bucket),
        Read: p.chaptersRead,
        Downloaded: p.chaptersDownloaded,
        Added: p.seriesAdded,
        // Hours to one decimal: a chart axis in seconds is unreadable at any realistic total.
        Hours: Math.round((p.readingSeconds / 3600) * 10) / 10,
      })),
    // bucketLabel calls monthName, which is locale-bound: without i18n.locale here, a language
    // switch would leave the previous language's month names cached until stats changed too.
    [stats, i18n.locale],
  )

  const genreTotal = useMemo(
    () => (stats?.topGenres ?? []).slice(0, 5).reduce((sum, g) => sum + g.weight, 0),
    [stats],
  )
  const genreData = useMemo(
    () =>
      (stats?.topGenres ?? []).slice(0, 5).map((g, i) => ({
        name: g.name,
        value: g.weight,
        color: GENRE_COLORS[i % GENRE_COLORS.length],
      })),
    [stats],
  )

  // Only compared when the previous window actually loaded — a tile that silently reads "vs the
  // period before" against a placeholder would be wrong rather than absent.
  const comparing = previous !== null && prevStats !== undefined
  let deltaLabel: string | undefined
  if (previous) {
    const { from, to } = previous
    deltaLabel = t`vs ${from} to ${to}`
  }
  const compare = (current: number, pick: (t: NonNullable<typeof prevStats>['totals']) => number) =>
    comparing ? delta(current, pick(prevStats!.totals)) : undefined

  const rangeControls = (
    <Group gap="sm" wrap="wrap" mb="lg">
      <SegmentedControl
        size="sm"
        value={preset}
        onChange={(v) => onPresetChange(v as RangePreset)}
        data={RANGE_OPTIONS.map((option) => ({ value: option.value, label: label(option.label) }))}
      />
      {preset === 'year' && (
        <>
          <Select
            data={yearOptions}
            value={String(year)}
            onChange={(v) => v && onYearChange(Number(v))}
            w={100}
            size="sm"
            aria-label={t`Year`}
          />
          <Select
            data={[
              { value: 'all', label: t`Whole year` },
              ...Array.from({ length: 12 }, (_, i) => ({ value: String(i + 1), label: monthName(i + 1) })),
            ]}
            value={month === null ? 'all' : String(month)}
            onChange={(v) => onMonthChange(v === null || v === 'all' ? null : Number(v))}
            w={140}
            size="sm"
            aria-label={t`Month`}
          />
        </>
      )}
    </Group>
  )

  if (isLoading && !stats) {
    return (
      <>
        {rangeControls}
        <Group justify="center" py={64}>
          <Loader />
        </Group>
      </>
    )
  }

  if (stats && !hasAnything) {
    const windowLabel = rangeLabel(preset, year, month)
    return (
      <>
        {rangeControls}
        <EmptyState
          icon={IconHistory}
          title={t`Nothing recorded for ${windowLabel}`}
          description={t`Activity is collected from the moment this version is installed. Add, download and read some manga, then come back.`}
        />
      </>
    )
  }

  if (!stats) {
    return rangeControls
  }

  return (
    <>
      {rangeControls}
      <Stack gap="lg">
        {!stats.readTrackingAvailable && (
          <Alert icon={<IconInfoCircle size={16} />} color="var(--neutral)" variant="light">
            <Trans>
              Reading stats need Kavita: connect it in Settings and Maki will start tracking
              chapters you read. Downloads and library changes are tracked either way.
            </Trans>
          </Alert>
        )}

        <SimpleGrid cols={{ base: 2, sm: 3, lg: 6 }} spacing="sm">
          <StatTile
            label={t`Chapters read`}
            value={stats.totals.chaptersRead}
            icon={IconBook2}
            delta={compare(stats.totals.chaptersRead, (t) => t.chaptersRead)}
            deltaLabel={deltaLabel}
          />
          <StatTile
            label={t`Time read`}
            value={formatReadingTime(stats.totals.readingSeconds)}
            icon={IconClock}
            delta={compare(stats.totals.readingSeconds, (t) => t.readingSeconds)}
            deltaLabel={deltaLabel}
          />
          <StatTile
            label={t`Days active`}
            value={stats.totals.daysActive}
            icon={IconCalendarStats}
            delta={compare(stats.totals.daysActive, (t) => t.daysActive)}
            deltaLabel={deltaLabel}
          />
          <StatTile
            label={t`Finished`}
            value={stats.totals.seriesFinished}
            icon={IconChecks}
            accent="ok"
            delta={compare(stats.totals.seriesFinished, (t) => t.seriesFinished)}
            deltaLabel={deltaLabel}
          />
          <StatTile
            label={t`Downloaded`}
            value={stats.totals.chaptersDownloaded}
            icon={IconDownload}
            accent="info"
            delta={compare(stats.totals.chaptersDownloaded, (t) => t.chaptersDownloaded)}
            deltaLabel={deltaLabel}
          />
          <StatTile
            label={t`Series added`}
            value={stats.totals.seriesAdded}
            icon={IconPlus}
            accent="ok"
            delta={compare(stats.totals.seriesAdded, (t) => t.seriesAdded)}
            deltaLabel={deltaLabel}
          />
        </SimpleGrid>

        {progressOn && summary && (
          <ProgressStrip summary={summary} onOpenAchievements={onOpenAchievements} />
        )}

        <div>
          <SectionHeader icon={IconCalendarStats} title={t`Activity`} />
          <Panel edge="brand" p="md">
            <Group justify="flex-end" mb="sm">
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
              <Text c="var(--ink-3)" size="sm">
                <Trans>No activity in this period.</Trans>
              </Text>
            ) : (
              <AreaChart
                h={260}
                data={timelineData}
                dataKey="bucket"
                curveType="monotone"
                withGradient
                withLegend
                tickLine="none"
                gridAxis="y"
                unit={metric === 'time' ? t`h` : undefined}
                series={
                  metric === 'chapters'
                    ? [
                        { name: 'Read', label: t`Read`, color: 'var(--brand)' },
                        { name: 'Downloaded', label: t`Downloaded`, color: 'var(--info)' },
                        { name: 'Added', label: t`Added`, color: 'var(--ok)' },
                      ]
                    : [{ name: 'Hours', label: t`Hours`, color: 'var(--brand)' }]
                }
              />
            )}
          </Panel>
        </div>

        {progressOn && heatmap && heatmap.length > 0 && <ReadingHeatmap days={heatmap} />}

        <div>
          <SectionHeader icon={IconTrophy} title={t`What you read`} />
          <SimpleGrid cols={{ base: 1, lg: stats.topByTime.length > 0 ? 3 : 2 }} spacing="lg">
            <RankList
              icon={IconBook2}
              title={t`Most read`}
              items={stats.topRead.map((s) => {
                const { count } = s
                return { ...s, value: plural(count, { one: '# ch', other: '# ch' }) }
              })}
              emptyText={t`No chapters read in this period.`}
            />
            {stats.topByTime.length > 0 && (
              <RankList
                icon={IconClock}
                title={t`Where the time went`}
                items={stats.topByTime.map((s) => ({ ...s, value: formatReadingTime(s.seconds) }))}
                emptyText={t`No reading time recorded.`}
              />
            )}
            <RankList
              icon={IconHourglassLow}
              title={t`Barely touched`}
              items={stats.leastRead.map((s) => {
                const { count } = s
                return { ...s, value: plural(count, { one: '# ch', other: '# ch' }) }
              })}
              emptyText={t`Everything you started, you kept reading.`}
            />
          </SimpleGrid>
        </div>

        {(genreData.length > 0 || stats.topTags.length > 0) && (
          <div>
            <SectionHeader icon={IconChecks} title={t`Taste`} />
            <SimpleGrid cols={{ base: 1, lg: 2 }} spacing="lg">
              <Panel p="md">
                <Text fw={650} mb="md">
                  <Trans>Top genres</Trans>
                </Text>
                {genreData.length === 0 ? (
                  <Text c="var(--ink-3)" size="sm">
                    <Trans>No genre data yet.</Trans>
                  </Text>
                ) : (
                  <Group align="center" gap="xl" wrap="nowrap">
                    <DonutChart data={genreData} size={180} thickness={24} withTooltip />
                    <Stack gap={6} style={{ minWidth: 0, flex: 1 }}>
                      {genreData.map((g) => (
                        <Group key={g.name} gap={8} wrap="nowrap">
                          <span
                            style={{
                              width: 10,
                              height: 10,
                              borderRadius: 3,
                              background: g.color,
                              flexShrink: 0,
                            }}
                          />
                          <Text size="sm" truncate style={{ flex: 1, minWidth: 0 }}>
                            {g.name}
                          </Text>
                          <Text size="xs" c="var(--ink-3)" className="tnum" style={{ flexShrink: 0 }}>
                            {genreTotal > 0 ? Math.round((g.value / genreTotal) * 100) : 0}%
                          </Text>
                        </Group>
                      ))}
                    </Stack>
                  </Group>
                )}
              </Panel>
              <Panel p="md">
                <Text fw={650} mb="md">
                  <Trans>Favorite tags</Trans>
                </Text>
                {stats.topTags.length === 0 ? (
                  <Text c="var(--ink-3)" size="sm">
                    <Trans>No tag data yet.</Trans>
                  </Text>
                ) : (
                  <TagChips>
                    {stats.topTags.map((t) => (
                      <TagChip key={t.name}>{t.name}</TagChip>
                    ))}
                  </TagChips>
                )}
              </Panel>
            </SimpleGrid>
          </div>
        )}

        {stats.finished.length +
          stats.added.length +
          stats.removed.length +
          stats.dropped.length >
          0 && (
          <div>
            <SectionHeader icon={IconHistory} title={t`Activity feed`} />
            <ActivityFeed stats={stats} />
          </div>
        )}
      </Stack>
    </>
  )
}
