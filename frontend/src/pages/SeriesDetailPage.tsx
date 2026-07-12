import { useState } from 'react'
import {
  ActionIcon,
  Alert,
  Badge,
  Button,
  Center,
  Group,
  Image,
  Loader,
  SegmentedControl,
  Select,
  Stack,
  Switch,
  Table,
  Text,
  Title,
  Tooltip,
} from '@mantine/core'
import { notifications } from '@mantine/notifications'
import { useNavigate, useParams } from 'react-router-dom'
import {
  useChapters,
  useDeleteSeries,
  useRefreshMetadata,
  useRefreshSeries,
  useRescanSeries,
  useSearchChapter,
  useSearchMissing,
  useSeriesDetail,
  useSetMonitorMode,
  useToggleChapterMonitor,
} from '../api/hooks'
import type { ChapterDto } from '../api/types'
import { MetadataLinks } from '../components/MetadataLinks'
import { ReleaseSearchModal } from '../components/ReleaseSearchModal'
import { SourceMappingsSection } from '../components/SourceMappingsSection'

function chapterLabel(c: ChapterDto): string {
  if (c.isOneShot || c.number === null) return c.title ?? 'One-shot'
  const vol = c.volume !== null ? `Vol.${c.volume} ` : ''
  return `${vol}Ch.${c.number}`
}

/** A special is a decimal-numbered chapter (10.5 omake etc.). */
const isSpecial = (c: ChapterDto) => c.number !== null && c.number % 1 !== 0

const chapterFilters: Record<string, (c: ChapterDto) => boolean> = {
  all: () => true,
  missing: (c) => !c.hasFile,
  downloaded: (c) => c.hasFile,
  specials: isSpecial,
}

export default function SeriesDetailPage() {
  const { id } = useParams()
  const seriesId = Number(id)
  const navigate = useNavigate()
  const { data: series, isLoading } = useSeriesDetail(seriesId)
  const { data: chapters } = useChapters(seriesId)
  const deleteSeries = useDeleteSeries()
  const refresh = useRefreshSeries()
  const refreshMetadata = useRefreshMetadata()
  const rescan = useRescanSeries()
  const search = useSearchChapter()
  const toggleMonitor = useToggleChapterMonitor()
  const searchMissing = useSearchMissing()
  const setMonitorMode = useSetMonitorMode()
  const [releaseModalOpen, setReleaseModalOpen] = useState(false)
  const [chapterFilter, setChapterFilter] = useState('all')

  if (isLoading) {
    return (
      <Center py="xl">
        <Loader />
      </Center>
    )
  }

  if (!series) {
    return <Text c="red">Series not found.</Text>
  }

  return (
    <Stack>
      <Group align="flex-start" wrap="nowrap">
        {series.coverUrl && (
          <Image src={series.coverUrl} w={180} radius="md" alt={series.title} />
        )}
        <Stack gap="xs" style={{ flex: 1 }}>
          <Title order={2}>{series.title}</Title>
          <Group gap="xs">
            <Badge variant="light">{series.status}</Badge>
            {series.year && <Badge variant="outline">{series.year}</Badge>}
            {series.genres.slice(0, 6).map((g) => (
              <Badge key={g} variant="default" size="sm">
                {g}
              </Badge>
            ))}
          </Group>
          {series.authorStory && (
            <Text size="sm" c="dimmed">
              Story: {series.authorStory}
              {series.authorArt && series.authorArt !== series.authorStory
                ? ` · Art: ${series.authorArt}`
                : ''}
            </Text>
          )}
          {series.links.length > 0 && <MetadataLinks links={series.links} />}
          <Text size="sm" lineClamp={5}>
            {series.overview}
          </Text>
          <Group mt="sm">
            <Button
              variant="light"
              size="xs"
              loading={refresh.isPending}
              onClick={() =>
                refresh.mutate(seriesId, {
                  onSuccess: (r) =>
                    notifications.show({
                      message: `Refreshed — ${r.newChapters} new chapter(s)`,
                      color: 'green',
                    }),
                  onError: (err) => notifications.show({ message: String(err), color: 'red' }),
                })
              }
            >
              Refresh chapters
            </Button>
            <Button
              variant="light"
              color="indigo"
              size="xs"
              loading={refreshMetadata.isPending}
              onClick={() =>
                refreshMetadata.mutate(seriesId, {
                  onSuccess: () =>
                    notifications.show({
                      message: 'Metadata and poster refreshed',
                      color: 'green',
                    }),
                  onError: (err) => notifications.show({ message: String(err), color: 'red' }),
                })
              }
            >
              Refresh metadata
            </Button>
            <Button
              variant="light"
              color="cyan"
              size="xs"
              loading={rescan.isPending}
              onClick={() =>
                rescan.mutate(seriesId, {
                  onSuccess: (r) =>
                    notifications.show({
                      message: `Rescanned — ${r.newFiles} new file(s), ${r.relinked} relinked, ${r.removed} removed`,
                      color: 'green',
                    }),
                  onError: (err) => notifications.show({ message: String(err), color: 'red' }),
                })
              }
            >
              Rescan files
            </Button>
            <Button
              variant="light"
              color="teal"
              size="xs"
              loading={searchMissing.isPending}
              onClick={() =>
                searchMissing.mutate(seriesId, {
                  onSuccess: (r) =>
                    notifications.show({
                      message: `Queued ${r.queued} missing chapter(s)`,
                      color: 'green',
                    }),
                  onError: (err) => notifications.show({ message: String(err), color: 'red' }),
                })
              }
            >
              Search all missing
            </Button>
            <Button variant="light" color="grape" size="xs" onClick={() => setReleaseModalOpen(true)}>
              Search releases
            </Button>
            <Button
              variant="light"
              color="red"
              size="xs"
              loading={deleteSeries.isPending}
              onClick={() =>
                deleteSeries.mutate(
                  { id: series.id, deleteFiles: false },
                  {
                    onSuccess: () => {
                      notifications.show({ message: 'Series removed', color: 'green' })
                      navigate('/')
                    },
                  },
                )
              }
            >
              Remove from library
            </Button>
            <Tooltip
              label="Which chapters are monitored — applies now and to chapters released later"
              withArrow
            >
              <Select
                size="xs"
                w={190}
                data={[
                  { value: 'All', label: 'Monitor: all chapters' },
                  { value: 'MainOnly', label: 'Monitor: main (no specials)' },
                  { value: 'None', label: 'Monitor: none' },
                ]}
                value={series.monitorNewItems}
                disabled={setMonitorMode.isPending}
                onChange={(mode) =>
                  mode &&
                  setMonitorMode.mutate(
                    { seriesId, mode },
                    {
                      onSuccess: (r) =>
                        notifications.show({
                          message: `Monitoring ${r.monitored}/${r.total} chapter(s)`,
                          color: 'green',
                        }),
                      onError: (err) => notifications.show({ message: String(err), color: 'red' }),
                    },
                  )
                }
              />
            </Tooltip>
          </Group>
        </Stack>
      </Group>

      <ReleaseSearchModal
        seriesId={seriesId}
        opened={releaseModalOpen}
        onClose={() => setReleaseModalOpen(false)}
      />

      {series.numberingClash && (
        <Alert color="yellow" title="Sources disagree on chapter numbering" mb="md">
          {(() => {
            const [sub, whole] = series.numberingClash.split('|')
            return (
              <>
                <Text span fw={600}>{sub}</Text> lists sub-chapters (1.1, 1.2, …) where{' '}
                <Text span fw={600}>{whole}</Text> lists whole chapters for the same content, so
                both appear as separate rows below. There is no safe automatic merge — consider
                disabling one of the two source mappings; the warning clears on the next refresh.
              </>
            )
          })()}
        </Alert>
      )}

      <SourceMappingsSection seriesId={seriesId} seriesTitle={series.title} />

      <Group justify="space-between">
        <Title order={4}>
          Chapters{' '}
          {chapters && (
            <Text span size="sm" c="dimmed">
              {/* Denominator excludes unmonitored, un-downloaded chapters (skipped specials). */}
              ({chapters.filter((c) => c.hasFile).length}/
              {chapters.filter((c) => c.monitored || c.hasFile).length})
            </Text>
          )}
        </Title>
        {chapters && chapters.length > 0 && (
          <SegmentedControl
            size="xs"
            value={chapterFilter}
            onChange={setChapterFilter}
            data={[
              { value: 'all', label: 'All' },
              { value: 'missing', label: `Missing (${chapters.filter(chapterFilters.missing).length})` },
              { value: 'downloaded', label: `Downloaded (${chapters.filter(chapterFilters.downloaded).length})` },
              { value: 'specials', label: `Specials (${chapters.filter(chapterFilters.specials).length})` },
            ]}
          />
        )}
      </Group>
      {!chapters || chapters.length === 0 ? (
        <Text c="dimmed" size="sm">
          No chapters known. Link a source and refresh.
        </Text>
      ) : (
        <Table striped highlightOnHover>
          <Table.Thead>
            <Table.Tr>
              <Table.Th w={40}></Table.Th>
              <Table.Th>Chapter</Table.Th>
              <Table.Th>Title</Table.Th>
              <Table.Th>Released</Table.Th>
              <Table.Th>Status</Table.Th>
              <Table.Th w={60}></Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {chapters.filter(chapterFilters[chapterFilter] ?? chapterFilters.all).map((c) => (
              <Table.Tr key={c.id}>
                <Table.Td>
                  <Switch
                    size="xs"
                    checked={c.monitored}
                    onChange={(e) =>
                      toggleMonitor.mutate({
                        chapterId: c.id,
                        monitored: e.currentTarget.checked,
                      })
                    }
                  />
                </Table.Td>
                <Table.Td>{chapterLabel(c)}</Table.Td>
                <Table.Td>
                  <Text size="sm" lineClamp={1}>
                    {c.title}
                  </Text>
                </Table.Td>
                <Table.Td>
                  <Text size="sm" c="dimmed">
                    {c.releaseDate ? new Date(c.releaseDate).toLocaleDateString() : '—'}
                  </Text>
                </Table.Td>
                <Table.Td>
                  {c.hasFile ? (
                    <Badge size="sm" color="green" variant="light">
                      Downloaded
                    </Badge>
                  ) : (
                    <Badge size="sm" color="gray" variant="light">
                      Missing
                    </Badge>
                  )}
                </Table.Td>
                <Table.Td>
                  {!c.hasFile && (
                    <Tooltip label="Download this chapter" withArrow>
                      <ActionIcon
                        variant="subtle"
                        onClick={() =>
                          search.mutate(c.id, {
                            onSuccess: () =>
                              notifications.show({
                                message: `Queued ${chapterLabel(c)}`,
                                color: 'green',
                              }),
                            onError: (err) =>
                              notifications.show({ message: String(err), color: 'red' }),
                          })
                        }
                        aria-label="Download chapter"
                      >
                        ⬇
                      </ActionIcon>
                    </Tooltip>
                  )}
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}
    </Stack>
  )
}
