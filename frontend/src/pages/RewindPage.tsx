import { useMemo, useState } from 'react'
import {
  Alert,
  Badge,
  Button,
  Card,
  Group,
  Loader,
  Select,
  SimpleGrid,
  Stack,
  Table,
  Text,
  Title,
} from '@mantine/core'
import { BarChart, DonutChart } from '@mantine/charts'
import {
  IconBook2,
  IconChecks,
  IconClockPause,
  IconDownload,
  IconHistory,
  IconInfoCircle,
  IconPlayerPlay,
  IconPlus,
  IconTrash,
} from '@tabler/icons-react'
import { Link } from 'react-router-dom'
import { useRewindStats, useRewindYears } from '../api/hooks'
import type { RewindSeriesEvent, RewindSeriesStat } from '../api/hooks'
import { PageHeader } from '../components/ui/PageHeader'
import { StatTile } from '../components/ui/StatTile'
import { EmptyState } from '../components/ui/EmptyState'
import { RewindIntro } from './rewind/RewindIntro'

const MONTHS = [
  'January',
  'February',
  'March',
  'April',
  'May',
  'June',
  'July',
  'August',
  'September',
  'October',
  'November',
  'December',
]

const GENRE_COLORS = ['var(--brand)', 'var(--info)', 'var(--ok)', 'var(--warn)', 'var(--danger)']

function rangeFor(year: number, month: number | null): { from: string; to: string } {
  if (month === null) {
    return { from: `${year}-01-01`, to: `${year}-12-31` }
  }
  const lastDay = new Date(year, month, 0).getDate()
  const mm = String(month).padStart(2, '0')
  return { from: `${year}-${mm}-01`, to: `${year}-${mm}-${String(lastDay).padStart(2, '0')}` }
}

/** "2026-03" → "Mar", "2026-03-14" → "14 Mar". */
function bucketLabel(bucket: string): string {
  const parts = bucket.split('-')
  const monthName = MONTHS[Number(parts[1]) - 1]?.slice(0, 3) ?? bucket
  return parts.length === 3 ? `${Number(parts[2])} ${monthName}` : monthName
}

function SeriesLink({ id, title }: { id: number | null; title: string }) {
  if (id === null) {
    return <Text span>{title}</Text>
  }
  return (
    <Text span component={Link} to={`/series/${id}`} className="rewind-series-link">
      {title}
    </Text>
  )
}

function ReadRankTable({ items }: { items: RewindSeriesStat[] }) {
  return (
    <Table verticalSpacing={6} withRowBorders={false}>
      <Table.Tbody>
        {items.map((s, i) => (
          <Table.Tr key={`${s.seriesId ?? s.title}-${i}`}>
            <Table.Td w={34}>
              <Text c="dimmed" fw={700} className="tnum">
                {i + 1}
              </Text>
            </Table.Td>
            <Table.Td>
              <SeriesLink id={s.seriesId} title={s.title} />
            </Table.Td>
            <Table.Td w={110} align="right">
              <Text className="tnum" fw={600}>
                {s.count} ch
              </Text>
            </Table.Td>
          </Table.Tr>
        ))}
      </Table.Tbody>
    </Table>
  )
}

function EventListCard({
  title,
  items,
  emptyText,
}: {
  title: string
  items: RewindSeriesEvent[]
  emptyText: string
}) {
  return (
    <Card padding="md" radius="lg">
      <Title order={4} mb="xs">
        {title}
      </Title>
      {items.length === 0 ? (
        <Text c="dimmed" size="sm">
          {emptyText}
        </Text>
      ) : (
        <Stack gap={4}>
          {items.slice(0, 12).map((e, i) => (
            <Group key={`${e.title}-${i}`} justify="space-between" wrap="nowrap" gap="xs">
              <SeriesLink id={e.seriesId} title={e.title} />
              <Text c="dimmed" size="xs" className="tnum" style={{ flexShrink: 0 }}>
                {new Date(e.at).toLocaleDateString()}
              </Text>
            </Group>
          ))}
          {items.length > 12 && (
            <Text c="dimmed" size="xs">
              …and {items.length - 12} more
            </Text>
          )}
        </Stack>
      )}
    </Card>
  )
}

export default function RewindPage() {
  const currentYear = new Date().getFullYear()
  const { data: years } = useRewindYears()
  const [year, setYear] = useState(currentYear)
  const [month, setMonth] = useState<number | null>(null)
  const [introOpen, setIntroOpen] = useState(false)

  const { from, to } = useMemo(() => rangeFor(year, month), [year, month])
  const { data: stats, isLoading } = useRewindStats(from, to)

  const yearOptions = (years?.length ? years : [currentYear]).map(String)
  const hasAnything =
    stats &&
    (stats.totals.chaptersRead > 0 ||
      stats.totals.volumesRead > 0 ||
      stats.totals.chaptersDownloaded > 0 ||
      stats.totals.seriesAdded > 0 ||
      stats.totals.seriesRemoved > 0)

  const timelineData = useMemo(
    () =>
      (stats?.timeline ?? []).map((p) => ({
        bucket: bucketLabel(p.bucket),
        Read: p.chaptersRead,
        Downloaded: p.chaptersDownloaded,
      })),
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

  return (
    <>
      {introOpen && stats && (
        <RewindIntro
          stats={stats}
          label={month === null ? String(year) : `${MONTHS[month - 1]} ${year}`}
          onClose={() => setIntroOpen(false)}
        />
      )}

      <PageHeader
        title="Rewind"
        description="Your reading year, wrapped: what you read, added, finished and dropped."
        actions={
          <>
            <Select
              data={yearOptions}
              value={String(year)}
              onChange={(v) => v && setYear(Number(v))}
              w={100}
              aria-label="Year"
            />
            <Select
              data={[
                { value: 'all', label: 'Whole year' },
                ...MONTHS.map((m, i) => ({ value: String(i + 1), label: m })),
              ]}
              value={month === null ? 'all' : String(month)}
              onChange={(v) => setMonth(v === null || v === 'all' ? null : Number(v))}
              w={140}
              aria-label="Month"
            />
            <Button
              leftSection={<IconPlayerPlay size={16} />}
              onClick={() => setIntroOpen(true)}
              disabled={!hasAnything}
            >
              Play Rewind
            </Button>
          </>
        }
      />

      {isLoading && !stats && (
        <Group justify="center" py={64}>
          <Loader />
        </Group>
      )}

      {stats && !hasAnything && (
        <EmptyState
          icon={IconHistory}
          title="Nothing recorded for this period"
          description="Rewind starts collecting activity from the moment this version is installed — add, download and read some manga, then come back."
        />
      )}

      {stats && hasAnything && (
        <Stack gap="lg">
          {!stats.readTrackingAvailable && (
            <Alert icon={<IconInfoCircle size={16} />} color="gray" variant="light">
              Reading stats need Kavita: connect it in Settings and Mangarr will start tracking
              chapters you read. Downloads and library changes are tracked either way.
            </Alert>
          )}

          <SimpleGrid cols={{ base: 2, sm: 3, lg: 6 }} spacing="sm">
            <StatTile label="Chapters read" value={stats.totals.chaptersRead} icon={IconBook2} />
            <StatTile
              label="Downloaded"
              value={stats.totals.chaptersDownloaded}
              icon={IconDownload}
              accent="info"
            />
            <StatTile label="Series added" value={stats.totals.seriesAdded} icon={IconPlus} accent="ok" />
            <StatTile
              label="Finished"
              value={stats.totals.seriesFinished}
              icon={IconChecks}
              accent="ok"
            />
            <StatTile
              label="Dropped"
              value={stats.totals.seriesDropped}
              icon={IconClockPause}
              accent="warn"
            />
            <StatTile
              label="Removed"
              value={stats.totals.seriesRemoved}
              icon={IconTrash}
              accent="danger"
            />
          </SimpleGrid>

          <SimpleGrid cols={{ base: 1, lg: 2 }} spacing="lg">
            <Card padding="md" radius="lg">
              <Title order={4} mb="md">
                Activity
              </Title>
              {timelineData.length === 0 ? (
                <Text c="dimmed" size="sm">
                  No activity in this period.
                </Text>
              ) : (
                <BarChart
                  h={260}
                  data={timelineData}
                  dataKey="bucket"
                  series={[
                    { name: 'Read', color: 'var(--brand)' },
                    { name: 'Downloaded', color: 'var(--info)' },
                  ]}
                  withLegend
                  tickLine="none"
                  gridAxis="y"
                />
              )}
            </Card>
            <Card padding="md" radius="lg">
              <Title order={4} mb="md">
                Top genres
              </Title>
              {genreData.length === 0 ? (
                <Text c="dimmed" size="sm">
                  No genre data yet.
                </Text>
              ) : (
                <Group align="center" gap="xl" wrap="nowrap">
                  <DonutChart data={genreData} size={200} thickness={26} withTooltip />
                  <Stack gap={6} style={{ minWidth: 0 }}>
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
                        <Text size="sm" truncate>
                          {g.name}
                        </Text>
                      </Group>
                    ))}
                  </Stack>
                </Group>
              )}
            </Card>
          </SimpleGrid>

          {stats.topTags.length > 0 && (
            <Card padding="md" radius="lg">
              <Title order={4} mb="xs">
                Favorite tags
              </Title>
              <Group gap={6}>
                {stats.topTags.map((t) => (
                  <Badge key={t.name} variant="default" color="gray" fw={500}>
                    {t.name}
                  </Badge>
                ))}
              </Group>
            </Card>
          )}

          {(stats.topRead.length > 0 || stats.leastRead.length > 0) && (
            <SimpleGrid cols={{ base: 1, lg: 2 }} spacing="lg">
              <Card padding="md" radius="lg">
                <Title order={4} mb="xs">
                  Most read
                </Title>
                {stats.topRead.length === 0 ? (
                  <Text c="dimmed" size="sm">
                    No chapters read in this period.
                  </Text>
                ) : (
                  <ReadRankTable items={stats.topRead} />
                )}
              </Card>
              <Card padding="md" radius="lg">
                <Title order={4} mb="xs">
                  Barely touched
                </Title>
                {stats.leastRead.length === 0 ? (
                  <Text c="dimmed" size="sm">
                    Nothing here — everything you started, you kept reading.
                  </Text>
                ) : (
                  <ReadRankTable items={stats.leastRead} />
                )}
              </Card>
            </SimpleGrid>
          )}

          <SimpleGrid cols={{ base: 1, sm: 2, lg: 4 }} spacing="lg">
            <EventListCard
              title="Finished"
              items={stats.finished}
              emptyText="No series finished in this period."
            />
            <EventListCard title="Added" items={stats.added} emptyText="No series added." />
            <EventListCard title="Removed" items={stats.removed} emptyText="No series removed." />
            <Card padding="md" radius="lg">
              <Title order={4} mb="xs">
                Dropped
              </Title>
              {stats.dropped.length === 0 ? (
                <Text c="dimmed" size="sm">
                  No stalled series — nice.
                </Text>
              ) : (
                <Stack gap={4}>
                  {stats.dropped.slice(0, 12).map((d, i) => (
                    <Group key={`${d.title}-${i}`} justify="space-between" wrap="nowrap" gap="xs">
                      <SeriesLink id={d.seriesId} title={d.title} />
                      <Text c="dimmed" size="xs" className="tnum" style={{ flexShrink: 0 }}>
                        ch {d.maxChapter}
                      </Text>
                    </Group>
                  ))}
                </Stack>
              )}
            </Card>
          </SimpleGrid>
        </Stack>
      )}
    </>
  )
}
