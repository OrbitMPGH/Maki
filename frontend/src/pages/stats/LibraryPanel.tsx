import { useMemo } from 'react'
import {
  Alert,
  Badge,
  Card,
  Group,
  Loader,
  Progress,
  SimpleGrid,
  Stack,
  Table,
  Text,
} from '@mantine/core'
import { AreaChart, DonutChart } from '@mantine/charts'
import {
  IconAlertTriangle,
  IconBooks,
  IconChartPie,
  IconChecks,
  IconDatabase,
  IconDownload,
  IconEye,
  IconFileZip,
  IconServer,
  IconTrendingUp,
} from '@tabler/icons-react'
import { useLibraryComposition } from '../../api/hooks'
import type { NamedCount } from '../../api/hooks'
import { SectionHeader } from '../../components/ui/SectionHeader'
import { StatTile } from '../../components/ui/StatTile'
import { SeriesLink, SeriesThumb } from './SeriesLink'
import { MONTHS } from './StatsRange'

const SLICE_COLORS = [
  'var(--brand)',
  'var(--info)',
  'var(--ok)',
  'var(--warn)',
  'var(--danger)',
  'var(--mantine-color-dark-3)',
]

/** Binary units, matching what a file manager reports for the same folder. */
function formatBytes(bytes: number): string {
  if (bytes <= 0) return '0 B'
  const units = ['B', 'KiB', 'MiB', 'GiB', 'TiB']
  const i = Math.min(units.length - 1, Math.floor(Math.log(bytes) / Math.log(1024)))
  const value = bytes / 1024 ** i
  return `${value >= 100 || i === 0 ? Math.round(value) : value.toFixed(1)} ${units[i]}`
}

function fileCount(n: number): string {
  return `${n.toLocaleString()} file${n === 1 ? '' : 's'}`
}

/** "2026-03" → "Mar 26". */
function monthLabel(bucket: string): string {
  const [y, m] = bucket.split('-')
  return `${MONTHS[Number(m) - 1]?.slice(0, 3) ?? bucket} ${y.slice(2)}`
}

function CompositionCard({ title, items }: { title: string; items: NamedCount[] }) {
  const total = items.reduce((sum, i) => sum + i.count, 0)
  const data = items.slice(0, 6).map((item, i) => ({
    name: item.name,
    value: item.count,
    color: SLICE_COLORS[i % SLICE_COLORS.length],
  }))

  return (
    <Card padding="md" radius="lg" withBorder>
      <Text fw={650} mb="md">
        {title}
      </Text>
      {data.length === 0 ? (
        <Text c="dimmed" size="sm">
          Nothing to show yet.
        </Text>
      ) : (
        <Group align="center" gap="xl" wrap="nowrap">
          <DonutChart data={data} size={160} thickness={22} withTooltip />
          <Stack gap={6} style={{ minWidth: 0, flex: 1 }}>
            {data.map((d) => (
              <Group key={d.name} gap={8} wrap="nowrap">
                <span
                  style={{
                    width: 10,
                    height: 10,
                    borderRadius: 3,
                    background: d.color,
                    flexShrink: 0,
                  }}
                />
                <Text size="sm" truncate style={{ flex: 1, minWidth: 0 }}>
                  {d.name}
                </Text>
                <Text size="xs" c="dimmed" className="tnum" style={{ flexShrink: 0 }}>
                  {total > 0 ? Math.round((d.value / total) * 100) : 0}%
                </Text>
              </Group>
            ))}
          </Stack>
        </Group>
      )}
    </Card>
  )
}

/**
 * What the collection is made of, as opposed to what anyone read. Not per-user, so this panel
 * ignores the reader picker — root-folder visibility is applied server-side.
 */
export function LibraryPanel() {
  const { data: stats, isLoading, isError } = useLibraryComposition()

  const growthData = useMemo(
    () =>
      (stats?.growth ?? []).map((g) => ({
        bucket: monthLabel(g.bucket),
        Added: g.seriesAdded,
        Total: g.cumulative,
      })),
    [stats],
  )

  const biggestSource = stats?.bySource[0]?.bytes ?? 0

  if (isLoading && !stats) {
    return (
      <Group justify="center" py={64}>
        <Loader />
      </Group>
    )
  }

  if (isError || !stats) {
    return (
      <Alert icon={<IconAlertTriangle size={16} />} color="red" variant="light">
        Could not load library stats. The server logs will say why.
      </Alert>
    )
  }

  const { totals } = stats

  return (
    <Stack gap="lg">
      <SimpleGrid cols={{ base: 2, sm: 3, lg: 6 }} spacing="sm">
        <StatTile label="Series" value={totals.seriesCount.toLocaleString()} icon={IconBooks} />
        <StatTile
          label="Chapters"
          value={totals.chapterCount.toLocaleString()}
          icon={IconFileZip}
          accent="info"
        />
        <StatTile
          label="Downloaded"
          value={totals.downloadedChapterCount.toLocaleString()}
          icon={IconDownload}
          accent="info"
        />
        <StatTile label="Disk used" value={formatBytes(totals.totalBytes)} icon={IconDatabase} accent="warn" />
        <StatTile label="Monitored" value={totals.monitoredCount.toLocaleString()} icon={IconEye} accent="ok" />
        <StatTile
          label="Completed"
          value={totals.completedCount.toLocaleString()}
          icon={IconChecks}
          accent="ok"
        />
      </SimpleGrid>

      {growthData.length > 0 && (
        <div>
          <SectionHeader icon={IconTrendingUp} title="Growth" />
          <Card padding="md" radius="lg" withBorder>
            <AreaChart
              h={240}
              data={growthData}
              dataKey="bucket"
              curveType="monotone"
              withGradient
              withLegend
              tickLine="none"
              gridAxis="y"
              series={[
                { name: 'Total', color: 'var(--brand)' },
                { name: 'Added', color: 'var(--ok)' },
              ]}
            />
          </Card>
        </div>
      )}

      <div>
        <SectionHeader icon={IconChartPie} title="Composition" />
        <SimpleGrid cols={{ base: 1, lg: 2 }} spacing="lg">
          <CompositionCard title="By type" items={stats.byType} />
          <CompositionCard title="By status" items={stats.byStatus} />
        </SimpleGrid>
      </div>

      <div>
        <SectionHeader icon={IconServer} title="Where it came from" />
        <SimpleGrid cols={{ base: 1, lg: 2 }} spacing="lg">
          <Card padding="md" radius="lg" withBorder>
            <Text fw={650} mb="xs">
              Sources
            </Text>
            {stats.bySource.length === 0 ? (
              <Text c="dimmed" size="sm">
                Nothing downloaded yet.
              </Text>
            ) : (
              <Table verticalSpacing={6} withRowBorders={false}>
                <Table.Tbody>
                  {stats.bySource.map((s) => (
                    <Table.Tr key={s.name}>
                      <Table.Td>
                        <Text size="sm">{s.name}</Text>
                        <Progress
                          value={biggestSource > 0 ? (s.bytes / biggestSource) * 100 : 0}
                          size="xs"
                          radius="xl"
                          mt={4}
                        />
                      </Table.Td>
                      <Table.Td w={90} align="right">
                        <Text size="sm" c="dimmed" className="tnum">
                          {fileCount(s.files)}
                        </Text>
                      </Table.Td>
                      <Table.Td w={90} align="right">
                        <Text size="sm" fw={600} className="tnum">
                          {formatBytes(s.bytes)}
                        </Text>
                      </Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            )}
          </Card>

          <Card padding="md" radius="lg" withBorder>
            <Text fw={650} mb="xs">
              Biggest series
            </Text>
            {stats.largest.length === 0 ? (
              <Text c="dimmed" size="sm">
                Nothing downloaded yet.
              </Text>
            ) : (
              <Stack gap={0}>
                {stats.largest.map((s, i) => (
                  <div className="stats-rank-row" key={s.seriesId}>
                    <Text c="dimmed" fw={700} size="sm" className="tnum stats-rank-num">
                      {i + 1}
                    </Text>
                    <SeriesThumb url={s.coverUrl} alt={s.title} />
                    <div style={{ flex: 1, minWidth: 0 }}>
                      <Text size="sm" truncate>
                        <SeriesLink id={s.seriesId} title={s.title} />
                      </Text>
                      <Text size="xs" c="dimmed" className="tnum">
                        {fileCount(s.files)}
                      </Text>
                    </div>
                    <Text size="sm" fw={600} className="tnum" style={{ flexShrink: 0 }}>
                      {formatBytes(s.bytes)}
                    </Text>
                  </div>
                ))}
              </Stack>
            )}
          </Card>
        </SimpleGrid>
      </div>

      {stats.topGenres.length > 0 && (
        <div>
          <SectionHeader icon={IconBooks} title="Genres in the library" />
          <Card padding="md" radius="lg" withBorder>
            <Group gap={6}>
              {stats.topGenres.map((g) => (
                <Badge key={g.name} variant="default" color="gray" fw={500}>
                  {g.name} <span className="tnum">{g.count}</span>
                </Badge>
              ))}
            </Group>
          </Card>
        </div>
      )}
    </Stack>
  )
}
