import { useMemo, useState } from 'react'
import {
  Alert,
  Badge,
  Card,
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
import { useGamificationSummary, useReadingHeatmap, useActivityStats } from '../../api/hooks'
import { EmptyState } from '../../components/ui/EmptyState'
import { SectionHeader } from '../../components/ui/SectionHeader'
import { StatTile } from '../../components/ui/StatTile'
import { formatReadingTime } from './duration'
import { ActivityFeed } from './ActivityFeed'
import { ProgressStrip } from './ProgressStrip'
import { RankList } from './RankList'
import { ReadingHeatmap } from './ReadingHeatmap'
import {
  MONTHS,
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
  const monthName = MONTHS[Number(parts[1]) - 1]?.slice(0, 3) ?? bucket
  return parts.length === 3 ? `${Number(parts[2])} ${monthName}` : monthName
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

  const { data: summary } = useGamificationSummary(userId)
  const gamificationOn = summary?.enabled === true
  const { data: heatmap } = useReadingHeatmap(userId, gamificationOn)

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
    [stats],
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
  const deltaLabel = previous ? `vs ${previous.from} to ${previous.to}` : undefined
  const compare = (current: number, pick: (t: NonNullable<typeof prevStats>['totals']) => number) =>
    comparing ? delta(current, pick(prevStats!.totals)) : undefined

  const rangeControls = (
    <Group gap="sm" wrap="wrap" mb="lg">
      <SegmentedControl
        size="sm"
        value={preset}
        onChange={(v) => onPresetChange(v as RangePreset)}
        data={RANGE_OPTIONS}
      />
      {preset === 'year' && (
        <>
          <Select
            data={yearOptions}
            value={String(year)}
            onChange={(v) => v && onYearChange(Number(v))}
            w={100}
            size="sm"
            aria-label="Year"
          />
          <Select
            data={[
              { value: 'all', label: 'Whole year' },
              ...MONTHS.map((m, i) => ({ value: String(i + 1), label: m })),
            ]}
            value={month === null ? 'all' : String(month)}
            onChange={(v) => onMonthChange(v === null || v === 'all' ? null : Number(v))}
            w={140}
            size="sm"
            aria-label="Month"
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
    return (
      <>
        {rangeControls}
        <EmptyState
          icon={IconHistory}
          title={`Nothing recorded for ${rangeLabel(preset, year, month)}`}
          description="Activity is collected from the moment this version is installed. Add, download and read some manga, then come back."
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
          <Alert icon={<IconInfoCircle size={16} />} color="gray" variant="light">
            Reading stats need Kavita: connect it in Settings and Maki will start tracking chapters
            you read. Downloads and library changes are tracked either way.
          </Alert>
        )}

        <SimpleGrid cols={{ base: 2, sm: 3, lg: 6 }} spacing="sm">
          <StatTile
            label="Chapters read"
            value={stats.totals.chaptersRead}
            icon={IconBook2}
            delta={compare(stats.totals.chaptersRead, (t) => t.chaptersRead)}
            deltaLabel={deltaLabel}
          />
          <StatTile
            label="Time read"
            value={formatReadingTime(stats.totals.readingSeconds)}
            icon={IconClock}
            delta={compare(stats.totals.readingSeconds, (t) => t.readingSeconds)}
            deltaLabel={deltaLabel}
          />
          <StatTile
            label="Days active"
            value={stats.totals.daysActive}
            icon={IconCalendarStats}
            delta={compare(stats.totals.daysActive, (t) => t.daysActive)}
            deltaLabel={deltaLabel}
          />
          <StatTile
            label="Finished"
            value={stats.totals.seriesFinished}
            icon={IconChecks}
            accent="ok"
            delta={compare(stats.totals.seriesFinished, (t) => t.seriesFinished)}
            deltaLabel={deltaLabel}
          />
          <StatTile
            label="Downloaded"
            value={stats.totals.chaptersDownloaded}
            icon={IconDownload}
            accent="info"
            delta={compare(stats.totals.chaptersDownloaded, (t) => t.chaptersDownloaded)}
            deltaLabel={deltaLabel}
          />
          <StatTile
            label="Series added"
            value={stats.totals.seriesAdded}
            icon={IconPlus}
            accent="ok"
            delta={compare(stats.totals.seriesAdded, (t) => t.seriesAdded)}
            deltaLabel={deltaLabel}
          />
        </SimpleGrid>

        {gamificationOn && summary && (
          <ProgressStrip summary={summary} onOpenAchievements={onOpenAchievements} />
        )}

        <div>
          <SectionHeader icon={IconCalendarStats} title="Activity" />
          <Card padding="md" radius="lg" withBorder>
            <Group justify="flex-end" mb="sm">
              <SegmentedControl
                size="xs"
                value={metric}
                onChange={(v) => setMetric(v as 'chapters' | 'time')}
                data={[
                  { value: 'chapters', label: 'Chapters' },
                  { value: 'time', label: 'Time' },
                ]}
              />
            </Group>
            {timelineData.length === 0 ? (
              <Text c="dimmed" size="sm">
                No activity in this period.
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
                unit={metric === 'time' ? 'h' : undefined}
                series={
                  metric === 'chapters'
                    ? [
                        { name: 'Read', color: 'var(--brand)' },
                        { name: 'Downloaded', color: 'var(--info)' },
                        { name: 'Added', color: 'var(--ok)' },
                      ]
                    : [{ name: 'Hours', color: 'var(--brand)' }]
                }
              />
            )}
          </Card>
        </div>

        {gamificationOn && heatmap && heatmap.length > 0 && <ReadingHeatmap days={heatmap} />}

        <div>
          <SectionHeader icon={IconTrophy} title="What you read" />
          <SimpleGrid cols={{ base: 1, lg: stats.topByTime.length > 0 ? 3 : 2 }} spacing="lg">
            <RankList
              icon={IconBook2}
              title="Most read"
              items={stats.topRead.map((s) => ({ ...s, value: `${s.count} ch` }))}
              emptyText="No chapters read in this period."
            />
            {stats.topByTime.length > 0 && (
              <RankList
                icon={IconClock}
                title="Where the time went"
                items={stats.topByTime.map((s) => ({ ...s, value: formatReadingTime(s.seconds) }))}
                emptyText="No reading time recorded."
              />
            )}
            <RankList
              icon={IconHourglassLow}
              title="Barely touched"
              items={stats.leastRead.map((s) => ({ ...s, value: `${s.count} ch` }))}
              emptyText="Everything you started, you kept reading."
            />
          </SimpleGrid>
        </div>

        {(genreData.length > 0 || stats.topTags.length > 0) && (
          <div>
            <SectionHeader icon={IconChecks} title="Taste" />
            <SimpleGrid cols={{ base: 1, lg: 2 }} spacing="lg">
              <Card padding="md" radius="lg" withBorder>
                <Text fw={650} mb="md">
                  Top genres
                </Text>
                {genreData.length === 0 ? (
                  <Text c="dimmed" size="sm">
                    No genre data yet.
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
                          <Text size="xs" c="dimmed" className="tnum" style={{ flexShrink: 0 }}>
                            {genreTotal > 0 ? Math.round((g.value / genreTotal) * 100) : 0}%
                          </Text>
                        </Group>
                      ))}
                    </Stack>
                  </Group>
                )}
              </Card>
              <Card padding="md" radius="lg" withBorder>
                <Text fw={650} mb="md">
                  Favorite tags
                </Text>
                {stats.topTags.length === 0 ? (
                  <Text c="dimmed" size="sm">
                    No tag data yet.
                  </Text>
                ) : (
                  <Group gap={6}>
                    {stats.topTags.map((t) => (
                      <Badge key={t.name} variant="default" color="gray" fw={500}>
                        {t.name}
                      </Badge>
                    ))}
                  </Group>
                )}
              </Card>
            </SimpleGrid>
          </div>
        )}

        {stats.finished.length +
          stats.added.length +
          stats.removed.length +
          stats.dropped.length >
          0 && (
          <div>
            <SectionHeader icon={IconHistory} title="Activity feed" />
            <ActivityFeed stats={stats} />
          </div>
        )}
      </Stack>
    </>
  )
}
