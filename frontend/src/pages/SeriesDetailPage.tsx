import {
  ActionIcon,
  Badge,
  Button,
  Center,
  Group,
  Image,
  Loader,
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
  useRefreshSeries,
  useSearchChapter,
  useSearchMissing,
  useSeriesDetail,
  useToggleChapterMonitor,
} from '../api/hooks'
import type { ChapterDto } from '../api/types'
import { SourceMappingsSection } from '../components/SourceMappingsSection'

function chapterLabel(c: ChapterDto): string {
  if (c.isOneShot || c.number === null) return c.title ?? 'One-shot'
  const vol = c.volume !== null ? `Vol.${c.volume} ` : ''
  return `${vol}Ch.${c.number}`
}

export default function SeriesDetailPage() {
  const { id } = useParams()
  const seriesId = Number(id)
  const navigate = useNavigate()
  const { data: series, isLoading } = useSeriesDetail(seriesId)
  const { data: chapters } = useChapters(seriesId)
  const deleteSeries = useDeleteSeries()
  const refresh = useRefreshSeries()
  const search = useSearchChapter()
  const toggleMonitor = useToggleChapterMonitor()
  const searchMissing = useSearchMissing()

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
          </Group>
        </Stack>
      </Group>

      <SourceMappingsSection seriesId={seriesId} seriesTitle={series.title} />

      <Title order={4}>
        Chapters{' '}
        {chapters && (
          <Text span size="sm" c="dimmed">
            ({chapters.filter((c) => c.hasFile).length}/{chapters.length})
          </Text>
        )}
      </Title>
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
            {chapters.map((c) => (
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
