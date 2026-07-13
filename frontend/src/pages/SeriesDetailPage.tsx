import { useMemo, useState } from 'react'
import {
  ActionIcon,
  Alert,
  Anchor,
  Badge,
  Box,
  Button,
  Center,
  Group,
  Loader,
  Progress,
  SegmentedControl,
  Select,
  Stack,
  Switch,
  Table,
  Text,
  Title,
  Tooltip,
} from '@mantine/core'
import {
  IconAlertTriangle,
  IconArrowLeft,
  IconCircleCheck,
  IconDownload,
  IconEye,
  IconPhoto,
  IconRefresh,
  IconScan,
  IconSearch,
  IconTrash,
} from '@tabler/icons-react'
import { notifications } from '@mantine/notifications'
import { Link, useNavigate, useParams } from 'react-router-dom'
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
import { SeriesFilesSection } from '../components/SeriesFilesSection'
import { SeriesScrobbleSection } from '../components/SeriesScrobbleSection'
import { SourceMappingsSection } from '../components/SourceMappingsSection'
import { seriesStatusVisual } from '../components/ui/status'

function chapterLabel(c: ChapterDto): string {
  if (c.isOneShot || c.number === null) return c.title ?? 'One-shot'
  // Prefer the volume the backing file actually is; fall back to metadata volume.
  const volNum = c.fileVolume ?? (c.volume !== null ? String(c.volume) : null)
  const vol = volNum !== null ? `Vol.${volNum} ` : ''
  return `${vol}Ch.${c.number}`
}

/** A special is a decimal-numbered chapter (10.5 omake etc.). */
const isSpecial = (c: ChapterDto) => c.number !== null && c.number % 1 !== 0

const chapterFilters: Record<string, (c: ChapterDto) => boolean> = {
  all: () => true,
  monitored: (c) => c.monitored,
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

  const progress = useMemo(() => {
    const list = chapters ?? []
    const have = list.filter((c) => c.hasFile).length
    const tracked = list.filter((c) => c.monitored || c.hasFile).length
    return { have, tracked, pct: tracked > 0 ? (have / tracked) * 100 : 0 }
  }, [chapters])

  if (isLoading) {
    return (
      <Center py={80}>
        <Loader />
      </Center>
    )
  }

  if (!series) {
    return <Text c="red">Series not found.</Text>
  }

  const status = seriesStatusVisual(series.status)
  const notify = {
    ok: (message: string) => notifications.show({ message, color: 'green' }),
    err: (err: unknown) => notifications.show({ message: String(err), color: 'red' }),
  }

  return (
    <Stack gap="lg">
      <Anchor component={Link} to="/" c="dimmed" size="sm" w="fit-content">
        <Group gap={4} wrap="nowrap">
          <IconArrowLeft size={15} />
          Library
        </Group>
      </Anchor>

      {/* Hero */}
      <Box className="detail-hero">
        {series.coverUrl && (
          <div
            className="detail-hero-backdrop"
            style={{ backgroundImage: `url(${series.coverUrl})` }}
          />
        )}
        <div className="detail-hero-veil" />
        <Group align="flex-start" wrap="nowrap" p={{ base: 'md', sm: 'xl' }} style={{ position: 'relative' }}>
          {series.coverUrl && (
            <Box
              visibleFrom="xs"
              style={{
                width: 190,
                flexShrink: 0,
                borderRadius: 12,
                overflow: 'hidden',
                boxShadow: '0 16px 40px -12px rgba(0,0,0,.7)',
                border: '1px solid var(--border)',
              }}
            >
              <img
                src={series.coverUrl}
                alt={series.title}
                style={{ width: '100%', aspectRatio: '2/3', objectFit: 'cover', display: 'block' }}
              />
            </Box>
          )}
          <Stack gap="sm" style={{ flex: 1, minWidth: 0 }}>
            <div>
              <Title order={1}>{series.title}</Title>
              {series.originalTitle && series.originalTitle !== series.title && (
                <Text c="dimmed" size="sm">
                  {series.originalTitle}
                </Text>
              )}
            </div>

            <Group gap="xs">
              <Badge color={status.color} variant="light" leftSection={<status.Icon size={12} />}>
                {status.label}
              </Badge>
              {series.year && <Badge variant="default">{series.year}</Badge>}
              {series.genres.slice(0, 6).map((g) => (
                <Badge key={g} variant="default" color="gray" fw={500}>
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

            {series.overview && (
              <Text size="sm" lineClamp={4} maw={720} c="gray.4">
                {series.overview}
              </Text>
            )}

            {/* Progress */}
            <Box maw={420} mt={4}>
              <Group justify="space-between" mb={4}>
                <Text size="xs" c="dimmed" fw={600} tt="uppercase" style={{ letterSpacing: '0.05em' }}>
                  Downloaded
                </Text>
                <Text size="xs" c="dimmed" className="tnum">
                  {progress.have} / {progress.tracked}
                </Text>
              </Group>
              <Progress
                value={progress.pct}
                color={progress.have >= progress.tracked && progress.tracked > 0 ? 'teal' : 'brand'}
                radius="xl"
              />
            </Box>
          </Stack>
        </Group>
      </Box>

      {/* Action toolbar */}
      <Group gap="xs" wrap="wrap">
        <Button
          variant="light"
          leftSection={<IconRefresh size={16} />}
          loading={refresh.isPending}
          onClick={() =>
            refresh.mutate(seriesId, {
              onSuccess: (r) => notify.ok(`Refreshed — ${r.newChapters} new chapter(s)`),
              onError: notify.err,
            })
          }
        >
          Refresh chapters
        </Button>
        <Button
          variant="light"
          color="grape"
          leftSection={<IconSearch size={16} />}
          loading={searchMissing.isPending}
          onClick={() =>
            searchMissing.mutate(seriesId, {
              onSuccess: (r) => notify.ok(`Queued ${r.queued} missing chapter(s)`),
              onError: notify.err,
            })
          }
        >
          Search missing
        </Button>
        <Button variant="light" color="cyan" leftSection={<IconDownload size={16} />} onClick={() => setReleaseModalOpen(true)}>
          Search releases
        </Button>
        <Button
          variant="default"
          leftSection={<IconPhoto size={16} />}
          loading={refreshMetadata.isPending}
          onClick={() =>
            refreshMetadata.mutate(seriesId, {
              onSuccess: () => notify.ok('Metadata and poster refreshed'),
              onError: notify.err,
            })
          }
        >
          Metadata
        </Button>
        <Button
          variant="default"
          leftSection={<IconScan size={16} />}
          loading={rescan.isPending}
          onClick={() =>
            rescan.mutate(seriesId, {
              onSuccess: (r) =>
                notify.ok(
                  `Rescanned — ${r.newFiles} new, ${r.relinked} relinked, ${r.removed} removed`,
                ),
              onError: notify.err,
            })
          }
        >
          Rescan files
        </Button>

        <Tooltip
          label="Which chapters are monitored — applies now and to chapters released later"
          withArrow
          multiline
          w={240}
        >
          <Select
            leftSection={<IconEye size={15} />}
            w={210}
            data={[
              { value: 'All', label: 'Monitor: all chapters' },
              { value: 'MainOnly', label: 'Monitor: main (no specials)' },
              { value: 'None', label: 'Monitor: none' },
            ]}
            value={series.monitorNewItems}
            disabled={setMonitorMode.isPending}
            comboboxProps={{ withinPortal: true }}
            onChange={(mode) =>
              mode &&
              setMonitorMode.mutate(
                { seriesId, mode },
                {
                  onSuccess: (r) => notify.ok(`Monitoring ${r.monitored}/${r.total} chapter(s)`),
                  onError: notify.err,
                },
              )
            }
          />
        </Tooltip>

        <Button
          variant="subtle"
          color="red"
          leftSection={<IconTrash size={16} />}
          loading={deleteSeries.isPending}
          ml="auto"
          onClick={() =>
            deleteSeries.mutate(
              { id: series.id, deleteFiles: false },
              {
                onSuccess: () => {
                  notify.ok('Series removed')
                  navigate('/')
                },
              },
            )
          }
        >
          Remove
        </Button>
      </Group>

      <ReleaseSearchModal
        seriesId={seriesId}
        opened={releaseModalOpen}
        onClose={() => setReleaseModalOpen(false)}
      />

      {series.numberingClash && (
        <Alert
          color="yellow"
          icon={<IconAlertTriangle size={18} />}
          title="Sources disagree on chapter numbering"
        >
          {(() => {
            const [sub, whole] = series.numberingClash.split('|')
            return (
              <>
                <Text span fw={600}>
                  {sub}
                </Text>{' '}
                lists sub-chapters (1.1, 1.2, …) where{' '}
                <Text span fw={600}>
                  {whole}
                </Text>{' '}
                lists whole chapters for the same content, so both appear as separate rows below.
                There is no safe automatic merge — consider disabling one of the two source
                mappings; the warning clears on the next refresh.
              </>
            )
          })()}
        </Alert>
      )}

      <SourceMappingsSection seriesId={seriesId} seriesTitle={series.title} />

      {/* Chapters */}
      <Group justify="space-between" wrap="wrap" gap="sm">
        <Group gap="xs" align="baseline">
          <Title order={3}>Chapters</Title>
          {chapters && (
            <Text size="sm" c="dimmed" className="tnum">
              {progress.have}/{progress.tracked}
            </Text>
          )}
        </Group>
        {chapters && chapters.length > 0 && (
          <SegmentedControl
            size="xs"
            value={chapterFilter}
            onChange={setChapterFilter}
            data={[
              { value: 'all', label: `All` },
              { value: 'monitored', label: `Monitored (${chapters.filter(chapterFilters.monitored).length})` },
              { value: 'missing', label: `Missing (${chapters.filter(chapterFilters.missing).length})` },
              { value: 'downloaded', label: `Have (${chapters.filter(chapterFilters.downloaded).length})` },
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
        <Table.ScrollContainer minWidth={560}>
          <Table highlightOnHover verticalSpacing="xs">
            <Table.Thead>
              <Table.Tr>
                <Table.Th w={52}>Watch</Table.Th>
                <Table.Th w={140}>Chapter</Table.Th>
                <Table.Th>Title</Table.Th>
                <Table.Th w={120}>Released</Table.Th>
                <Table.Th w={130}>Status</Table.Th>
                <Table.Th w={52} />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {chapters.filter(chapterFilters[chapterFilter] ?? chapterFilters.all).map((c) => (
                <Table.Tr key={c.id} opacity={c.monitored || c.hasFile ? 1 : 0.55}>
                  <Table.Td>
                    <Switch
                      size="xs"
                      checked={c.monitored}
                      onChange={(e) =>
                        toggleMonitor.mutate({ chapterId: c.id, monitored: e.currentTarget.checked })
                      }
                    />
                  </Table.Td>
                  <Table.Td>
                    <Group gap={6} wrap="nowrap">
                      {c.fileVolume !== null && !c.isOneShot && c.number !== null && (
                        <Tooltip label="Contained in a volume/compilation file" withArrow>
                          <Badge size="sm" color="indigo" variant="light" className="tnum">
                            Vol.{c.fileVolume}
                          </Badge>
                        </Tooltip>
                      )}
                      <Text size="sm" fw={550} className="tnum">
                        {c.isOneShot || c.number === null
                          ? chapterLabel(c)
                          : c.fileVolume !== null
                            ? `Ch.${c.number}`
                            : chapterLabel(c)}
                      </Text>
                    </Group>
                  </Table.Td>
                  <Table.Td>
                    <Text size="sm" c="dimmed" lineClamp={1}>
                      {c.title}
                    </Text>
                  </Table.Td>
                  <Table.Td>
                    <Text size="sm" c="dimmed" className="tnum">
                      {c.releaseDate ? new Date(c.releaseDate).toLocaleDateString() : '—'}
                    </Text>
                  </Table.Td>
                  <Table.Td>
                    {c.hasFile ? (
                      <Badge size="sm" color="teal" variant="light" leftSection={<IconCircleCheck size={12} />}>
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
                          color="brand"
                          onClick={() =>
                            search.mutate(c.id, {
                              onSuccess: () => notify.ok(`Queued ${chapterLabel(c)}`),
                              onError: notify.err,
                            })
                          }
                          aria-label={`Download ${chapterLabel(c)}`}
                        >
                          <IconDownload size={17} />
                        </ActionIcon>
                      </Tooltip>
                    )}
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      )}

      <SeriesScrobbleSection seriesId={seriesId} />

      <SeriesFilesSection seriesId={seriesId} />
    </Stack>
  )
}
