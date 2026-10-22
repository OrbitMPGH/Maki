import { useMemo, useState } from 'react'
import { Group, SegmentedControl, SimpleGrid, Skeleton, Stack, Text } from '@mantine/core'
import { AreaChart } from '@mantine/charts'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'
import { useLibraryComposition, useLibraryGrowth } from '../../../api/hooks'
import type { LibraryCompositionTotals } from '../../../api/hooks'
import { EmptyState } from '../../../components/ui/EmptyState'
import { Panel } from '../../../components/ui/Panel'
import { StatTile } from '../../../components/ui/StatTile'
import { TagChip, TagChips } from '../../../components/ui/TagChip'
import { formatBytes, formatNumber, formatPercent, formatStatsBucket } from '../../../format'
import { ChartSkeleton } from '../ChartSkeleton'
import { SplitBar } from '../charts/SplitBar'
import type { SplitBarItem } from '../charts/SplitBar'
import { CONTENT_RATING_LABELS, PROTOCOL_LABELS, STATUS_LABELS, TYPE_LABELS, useNameLabel } from '../labels'
import { SeriesLink, SeriesThumb } from '../SeriesLink'
import { SourceReliability } from '../SourceReliability'
import { StatsInsight } from '../StatsSection'
import type { StatsSectionProps } from './types'

function fileCount(n: number): string {
  return plural(n, { one: '# file', other: '# files' })
}

function TotalsTiles({ totals }: { totals?: LibraryCompositionTotals }) {
  const { t } = useLingui()
  const loading = !totals
  const count = (n: number | undefined) => formatNumber(n ?? 0)
  return (
    <SimpleGrid cols={{ base: 2, sm: 3, lg: 6 }} spacing="sm">
      <StatTile label={t`Series`} value={count(totals?.seriesCount)} loading={loading} />
      <StatTile
        label={t`Chapters`}
        value={count(totals?.chapterCount)}
        loading={loading}
      />
      <StatTile
        label={t`Downloaded`}
        value={count(totals?.downloadedChapterCount)}
        loading={loading}
      />
      <StatTile
        label={t`Disk used`}
        value={formatBytes(totals?.totalBytes ?? 0)}
        loading={loading}
      />
      <StatTile label={t`Monitored`} value={count(totals?.monitoredCount)} loading={loading} />
      <StatTile
        label={t`Completed`}
        value={count(totals?.completedCount)}
        loading={loading}
      />
    </SimpleGrid>
  )
}

/**
 * What the collection is made of, as opposed to what anyone read. Not per-user, so this section
 * ignores the reader picker — root-folder visibility is applied server-side. Only Growth follows
 * the window; the rest describes the library as it is now.
 */
export default function LibrarySection({ range }: StatsSectionProps) {
  const { t, i18n } = useLingui()
  const nameLabel = useNameLabel()
  const { data: stats, isLoading, isError, refetch } = useLibraryComposition()
  const { data: growth, isLoading: growthLoading } = useLibraryGrowth(range.from, range.to)
  const [growthMetric, setGrowthMetric] = useState<'series' | 'size'>('series')

  const growthData = useMemo(
    () =>
      (growth ?? []).map((g) => ({
        bucket: formatStatsBucket(g.bucket),
        Added: growthMetric === 'size' ? g.bytesAdded : g.seriesAdded,
        Total: growthMetric === 'size' ? g.cumulativeBytes : g.cumulative,
      })),
    // The bucket labels are locale-bound: without i18n.locale here, a language switch would leave
    // the previous language's month names cached until the data changed too.
    [growth, growthMetric, i18n.locale],
  )

  const topSource = useMemo(() => {
    if (!stats?.sourceReliability.length) return null
    return stats.sourceReliability.reduce((best, s) =>
      s.completed + s.failed > best.completed + best.failed ? s : best,
    )
  }, [stats])

  const seriesCount = stats?.totals.seriesCount ?? 0
  const diskSize = formatBytes(stats?.totals.totalBytes ?? 0)
  const topSourceName = topSource ? nameLabel(PROTOCOL_LABELS, topSource.name) : ''
  const topSourceAttempts = topSource ? topSource.completed + topSource.failed : 0
  const topSourceRate = topSource && topSourceAttempts > 0 ? formatPercent(topSource.completed / topSourceAttempts) : ''

  const biggestBytes = stats?.largest[0]?.bytes ?? 0
  const requestsLabel = stats?.requests.allUsers ? t`All requests` : t`Your requests`
  const requestsOpen = stats?.requests.open ?? 0
  const requestsMedianHours = stats?.requests.medianResolveHours ?? null
  const resolved = stats?.requests.resolved90d ?? 0
  const resolvedHours = requestsMedianHours !== null ? Math.round(requestsMedianHours) : null

  if (isLoading && !stats) {
    return (
      <>
        <StatsInsight>
          <Skeleton h={16} w={320} />
        </StatsInsight>
        <Stack gap="lg" aria-hidden>
          <TotalsTiles />
          <div className="stats-grid-main">
            <Panel p="md">
              <ChartSkeleton h={220} />
            </Panel>
            <Panel p="md">
              <ChartSkeleton h={220} />
            </Panel>
          </div>
          <SimpleGrid cols={{ base: 1, md: 2 }} spacing="lg">
            <Panel p="md">
              <ChartSkeleton h={180} />
            </Panel>
            <Panel p="md">
              <ChartSkeleton h={180} />
            </Panel>
          </SimpleGrid>
        </Stack>
      </>
    )
  }

  if (isError || !stats) {
    return (
      <EmptyState
        title={t`Could not load library stats`}
        description={t`The server logs will say why.`}
        actionLabel={t`Try again`}
        onAction={() => void refetch()}
      />
    )
  }

  const statusItems: SplitBarItem[] = stats.byStatus.map((s) => ({
    key: s.name,
    label: nameLabel(STATUS_LABELS, s.name),
    value: s.count,
  }))
  const typeItems: SplitBarItem[] = stats.byType.map((s) => ({
    key: s.name,
    label: nameLabel(TYPE_LABELS, s.name),
    value: s.count,
  }))
  const ratingItems: SplitBarItem[] = stats.byContentRating.map((s) => ({
    key: s.name,
    label: nameLabel(CONTENT_RATING_LABELS, s.name),
    value: s.count,
  }))

  if (seriesCount === 0) {
    return (
      <>
        <StatsInsight>
          <Trans>Nothing in the library yet.</Trans>{' '}
          <span className="stats-insight-dim">
            <Trans>Growth, composition and sources fill in once you add a series.</Trans>
          </span>
        </StatsInsight>
        <TotalsTiles totals={stats.totals} />
      </>
    )
  }

  return (
    <>
      <StatsInsight>
        <Trans>
          <em>
            <Plural value={seriesCount} one="# series" other="# series" />
          </em>{' '}
          and <em>{diskSize}</em> on disk.
        </Trans>
        {topSource && topSourceAttempts > 0 && (
          <span className="stats-insight-dim">
            {' '}
            <Trans>
              {topSourceName} delivered {topSourceRate} of its downloads in the last 30 days.
            </Trans>
          </span>
        )}
      </StatsInsight>

      <Stack gap="lg">
        <TotalsTiles totals={stats.totals} />

        <div className="stats-grid-main">
          <Panel p="md">
            <Group justify="space-between" align="flex-start" mb="sm">
              <p className="stats-panel-title">{t`Growth`}</p>
              <SegmentedControl
                aria-label={t`Growth`}
                size="xs"
                value={growthMetric}
                onChange={(v) => setGrowthMetric(v as 'series' | 'size')}
                data={[
                  { value: 'series', label: t`Series` },
                  { value: 'size', label: t`Disk size` },
                ]}
              />
            </Group>
            {growthLoading && !growth ? (
              <ChartSkeleton h={240} />
            ) : growthData.length > 0 ? (
              <AreaChart
                h={240}
                data={growthData}
                dataKey="bucket"
                curveType="monotone"
                withGradient
                withLegend
                tickLine="none"
                gridAxis="y"
                valueFormatter={growthMetric === 'size' ? formatBytes : undefined}
                series={[
                  { name: 'Total', label: t`Total`, color: 'var(--brand)' },
                  { name: 'Added', label: t`Added`, color: 'var(--ok)' },
                ]}
              />
            ) : (
              <Text c="var(--ink-3)" size="sm">
                {t`Nothing to show yet.`}
              </Text>
            )}
          </Panel>

          <Panel p="md">
            <p className="stats-panel-title">{t`Composition`}</p>
            <Stack gap="md" mt="xs">
              <div>
                <Text size="xs" fw={600} c="var(--ink-3)" mb={6}>
                  {t`By status`}
                </Text>
                <SplitBar items={statusItems} />
              </div>
              <div>
                <Text size="xs" fw={600} c="var(--ink-3)" mb={6}>
                  {t`By type`}
                </Text>
                <SplitBar items={typeItems} />
              </div>
              <div>
                <Text size="xs" fw={600} c="var(--ink-3)" mb={6}>
                  {t`Content rating`}
                </Text>
                <SplitBar items={ratingItems} />
              </div>
            </Stack>
          </Panel>
        </div>

        <SimpleGrid cols={{ base: 1, md: 2 }} spacing="lg">
          <Panel p="md">
            <p className="stats-panel-title">{t`Sources`}</p>
            <p className="stats-panel-sub">{t`Success and wait cover the last 30 days.`}</p>
            <SourceReliability sources={stats.bySource} reliability={stats.sourceReliability} />
          </Panel>

          <Panel p="md">
            <p className="stats-panel-title">{t`Biggest on disk`}</p>
            {stats.largest.length === 0 ? (
              <Text c="var(--ink-3)" size="sm">
                {t`Nothing downloaded yet.`}
              </Text>
            ) : (
              <ul className="stats-rows">
                {stats.largest.map((s) => {
                  const share = biggestBytes > 0 ? s.bytes / biggestBytes : 0
                  return (
                    <li className="stats-row" key={s.seriesId}>
                      <SeriesThumb url={s.coverUrl} alt={s.title} />
                      <div style={{ minWidth: 0 }}>
                        <div className="stats-row-title">
                          <SeriesLink id={s.seriesId} title={s.title} />
                        </div>
                        <div className="stats-row-sub">{fileCount(s.files)}</div>
                        <div className="stats-row-meter">
                          <span style={{ width: `${share * 100}%` }} />
                        </div>
                      </div>
                      <div className="stats-row-value">{formatBytes(s.bytes)}</div>
                    </li>
                  )
                })}
              </ul>
            )}

            <div className="stats-facts" style={{ marginTop: 16 }}>
              <div className="stats-fact">
                <span className="stats-fact-label">{requestsLabel}</span>
                <span className="stats-fact-value">
                  <Plural value={resolved} one="# resolved" other="# resolved" />
                </span>
                <span className="stats-fact-hint">
                  {resolvedHours === null ? (
                    <Trans>{requestsOpen} open</Trans>
                  ) : (
                    <Trans>
                      {requestsOpen} open, median {resolvedHours}h
                    </Trans>
                  )}
                </span>
              </div>
              <div className="stats-fact">
                <span className="stats-fact-label">{t`Monitor catches`}</span>
                <span className="stats-fact-value">{formatNumber(stats.monitorCatches)}</span>
                <span className="stats-fact-hint">{t`New chapters fetched automatically, last 30 days.`}</span>
              </div>
            </div>
          </Panel>
        </SimpleGrid>

        {stats.topGenres.length > 0 && (
          <Panel p="md">
            <p className="stats-panel-title">{t`Genres in the library`}</p>
            <TagChips>
              {stats.topGenres.map((g) => (
                <TagChip key={g.name}>
                  {g.name} <span className="tnum">{formatNumber(g.count)}</span>
                </TagChip>
              ))}
            </TagChips>
          </Panel>
        )}
      </Stack>
    </>
  )
}
