import { useMemo, useState } from 'react'
import {
  ActionIcon,
  Alert,
  Anchor,
  Badge,
  Box,
  Button,
  Center,
  Checkbox,
  Group,
  Loader,
  Modal,
  Paper,
  Progress,
  Radio,
  Rating,
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
  IconFolderSymlink,
  IconLink,
  IconLinkOff,
  IconListCheck,
  IconPhoto,
  IconRefresh,
  IconScan,
  IconSearch,
  IconTrash,
  IconX,
} from '@tabler/icons-react'
import { notifications } from '@mantine/notifications'
import { Link, useNavigate, useParams } from 'react-router-dom'
import {
  useChapters,
  useDeleteSeries,
  useMoveSeries,
  useRefreshMetadata,
  useRefreshSeries,
  useRescanSeries,
  useRootFolders,
  useSearchChapter,
  useSearchMissing,
  useSeriesDetail,
  useSetMonitorMode,
  useSetRating,
  useToggleChapterMonitor,
  useUnlinkChapters,
} from '../api/hooks'
import type { ChapterDto } from '../api/types'
import { LinkChaptersModal } from '../components/LinkChaptersModal'
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
  const moveSeries = useMoveSeries()
  const { data: rootFolders } = useRootFolders()
  const [moveModalOpen, setMoveModalOpen] = useState(false)
  const [moveTarget, setMoveTarget] = useState<string | null>(null)
  const [moveFiles, setMoveFiles] = useState(true)
  const search = useSearchChapter()
  const toggleMonitor = useToggleChapterMonitor()
  const searchMissing = useSearchMissing()
  const setMonitorMode = useSetMonitorMode()
  const setRating = useSetRating()
  const unlinkChapters = useUnlinkChapters()
  const [releaseModalOpen, setReleaseModalOpen] = useState(false)
  const [chapterFilter, setChapterFilter] = useState('all')
  const [selectMode, setSelectMode] = useState(false)
  const [selected, setSelected] = useState<Set<number>>(new Set())
  const [linkModalOpen, setLinkModalOpen] = useState(false)

  const toggleChapterSelected = (id: number) =>
    setSelected((s) => {
      const next = new Set(s)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })

  const exitSelectMode = () => {
    setSelectMode(false)
    setSelected(new Set())
  }

  const progress = useMemo(() => {
    const list = chapters ?? []
    const have = list.filter((c) => c.hasFile).length
    const monitored = list.filter((c) => c.monitored || c.hasFile).length
    // With nothing monitored and nothing downloaded this would read "0 / 0" while the Chapters
    // tab below lists every known chapter as missing. Show what's known instead, and don't let
    // the bar imply progress against a total the user isn't actually tracking.
    const unmonitored = monitored === 0 && list.length > 0
    const tracked = monitored || list.length
    return {
      have,
      tracked,
      unmonitored,
      pct: !unmonitored && tracked > 0 ? (have / tracked) * 100 : 0,
    }
  }, [chapters])

  /**
   * How far the linked sources fall short of the chapter count MangaBaka reports.
   *
   * Without this a series reads "41 / 41" once every chapter the sources carry is downloaded,
   * which looks finished — so it's easy to unmonitor a series that's actually missing its tail.
   * The gap is deliberately kept out of the progress fraction: those chapters can't be fetched
   * from the linked sources, so counting them would just make the bar unreachable instead.
   *
   * Compared by highest chapter NUMBER, never the row count — sources list specials and one-shots
   * MangaBaka doesn't count, so a count reads "ahead" (365 rows against a reported 119) on a
   * series that is really three chapters short.
   */
  const sourceGap = useMemo(() => {
    const total = series?.totalChapters
    const numbered = (chapters ?? []).map((c) => c.number).filter((n): n is number => n !== null)
    if (!total || numbered.length === 0) return null

    const highest = Math.max(...numbered)
    if (highest >= total) return null

    return { highest, total, missing: Math.floor(total - highest) }
  }, [series, chapters])

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
  // Errors are reported globally (see main.tsx); only success needs saying here.
  const notify = {
    ok: (message: string) => notifications.show({ message, color: 'green' }),
  }

  const submitRating = (rating: number | null) =>
    setRating.mutate(
      { seriesId, rating },
      {
        onSuccess: () =>
          notify.ok(rating === null ? 'Rating cleared' : `Rated ${rating}/10`),
      },
    )

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

            <Group gap="xs" align="center">
              <Text size="xs" c="dimmed" fw={600} tt="uppercase" style={{ letterSpacing: '0.05em' }}>
                Your rating
              </Text>
              <Rating
                count={5}
                fractions={2}
                value={series.rating ? series.rating / 2 : 0}
                onChange={(v) => submitRating(Math.round(v * 2) || null)}
              />
              {series.rating && (
                <>
                  <Text size="xs" c="dimmed" className="tnum">
                    {series.rating}/10
                  </Text>
                  <Tooltip label="Clear rating" withArrow>
                    <ActionIcon
                      size="sm"
                      variant="subtle"
                      color="gray"
                      onClick={() => submitRating(null)}
                      aria-label="Clear rating"
                    >
                      <IconX size={14} />
                    </ActionIcon>
                  </Tooltip>
                </>
              )}
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
                  {progress.unmonitored && ' known — none monitored'}
                  {/* Spelled out as chapter numbers, not folded into the fraction above it: that
                      fraction counts rows (which include specials), so "80 / 80 of 136" would be
                      comparing two different things. */}
                  {sourceGap && (
                    <Text span c="yellow.5">
                      {' '}
                      · up to ch. {sourceGap.highest} of {sourceGap.total}
                    </Text>
                  )}
                </Text>
              </Group>
              <Progress
                value={progress.pct}
                // Never green while the sources are short of the full run: "all downloaded" and
                // "you have the whole series" are different claims, and the green tick is exactly
                // what makes someone unmonitor a series that's still missing its tail.
                color={
                  sourceGap
                    ? 'yellow'
                    : !progress.unmonitored && progress.have >= progress.tracked && progress.tracked > 0
                      ? 'teal'
                      : 'brand'
                }
                radius="xl"
              />
              {sourceGap && (
                <Group gap={6} mt={8} wrap="nowrap" align="flex-start">
                  <IconAlertTriangle
                    size={14}
                    style={{ color: 'var(--warn)', flexShrink: 0, marginTop: 2 }}
                  />
                  <Text size="xs" c="dimmed">
                    Your sources only reach chapter{' '}
                    <Text span fw={600} c="gray.3" className="tnum">
                      {sourceGap.highest}
                    </Text>
                    , but MangaBaka lists{' '}
                    <Text span fw={600} c="gray.3" className="tnum">
                      {sourceGap.total}
                    </Text>
                    . Roughly {sourceGap.missing} chapter{sourceGap.missing === 1 ? '' : 's'} can't be
                    downloaded from the sources linked here — link another source to close the gap.
                  </Text>
                </Group>
              )}
            </Box>

            {series.readChapterCount != null && progress.have > 0 && (
              <Box maw={420}>
                <Group justify="space-between" mb={4}>
                  <Text size="xs" c="dimmed" fw={600} tt="uppercase" style={{ letterSpacing: '0.05em' }}>
                    Read
                  </Text>
                  <Text size="xs" c="dimmed" className="tnum">
                    {series.readChapterCount} / {progress.have}
                  </Text>
                </Group>
                <Progress
                  value={Math.min(100, (series.readChapterCount / progress.have) * 100)}
                  color="var(--info)"
                  radius="xl"
                />
              </Box>
            )}
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
            })
          }
        >
          Rescan files
        </Button>
        <Button
          variant="default"
          leftSection={<IconFolderSymlink size={16} />}
          onClick={() => {
            setMoveTarget(null)
            setMoveFiles(true)
            setMoveModalOpen(true)
          }}
        >
          Move
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

      <Modal opened={moveModalOpen} onClose={() => setMoveModalOpen(false)} title="Move series" centered>
        <Stack gap="md">
          <Text size="sm" c="dimmed">
            Re-triggers a Kavita scan of both locations either way. Blocked while a download for
            this series is in flight, unless Maki isn't touching the files itself.
          </Text>
          <Select
            label="Destination root folder"
            placeholder="Pick a root folder"
            data={(rootFolders ?? [])
              .filter((f) => f.id !== series.rootFolderId)
              .map((f) => ({ value: String(f.id), label: f.path }))}
            value={moveTarget}
            onChange={setMoveTarget}
            comboboxProps={{ withinPortal: true }}
          />
          <Radio.Group
            label="Files"
            value={moveFiles ? 'move' : 'already-moved'}
            onChange={(v) => setMoveFiles(v === 'move')}
          >
            <Stack gap={6} mt={6}>
              <Radio value="move" label="Move the files on disk to the new root folder" />
              <Radio
                value="already-moved"
                label="Just point the series at the new root folder — I already moved the files"
              />
            </Stack>
          </Radio.Group>
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setMoveModalOpen(false)}>
              Cancel
            </Button>
            <Button
              loading={moveSeries.isPending}
              disabled={!moveTarget}
              onClick={() =>
                moveTarget &&
                moveSeries.mutate(
                  { seriesId, rootFolderId: Number(moveTarget), moveFiles },
                  {
                    onSuccess: () => {
                      notify.ok('Series moved')
                      setMoveModalOpen(false)
                    },
                  },
                )
              }
            >
              Move
            </Button>
          </Group>
        </Stack>
      </Modal>

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
          <Group gap="xs" wrap="wrap">
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
            {!selectMode && (
              <Button
                size="xs"
                variant="default"
                leftSection={<IconListCheck size={14} />}
                onClick={() => setSelectMode(true)}
              >
                Select
              </Button>
            )}
          </Group>
        )}
      </Group>

      {selectMode && (
        <Paper withBorder p="xs" radius="lg">
          <Group justify="space-between" wrap="wrap" gap="xs">
            <Group gap="xs">
              <Text size="sm" c="dimmed" className="tnum">
                {selected.size} selected
              </Text>
              <Button
                size="xs"
                variant="subtle"
                onClick={() =>
                  setSelected(
                    selected.size === (chapters?.length ?? 0)
                      ? new Set()
                      : new Set((chapters ?? []).map((c) => c.id)),
                  )
                }
              >
                {selected.size === (chapters?.length ?? 0) ? 'Clear all' : 'Select all'}
              </Button>
            </Group>
            <Group gap="xs">
              <Button
                size="xs"
                variant="light"
                leftSection={<IconLink size={15} />}
                disabled={selected.size === 0}
                onClick={() => setLinkModalOpen(true)}
              >
                Link to file
              </Button>
              <Button
                size="xs"
                variant="light"
                color="orange"
                leftSection={<IconLinkOff size={15} />}
                disabled={selected.size === 0}
                loading={unlinkChapters.isPending}
                onClick={() =>
                  unlinkChapters.mutate([...selected], {
                    onSuccess: (r) => {
                      notify.ok(`Unlinked ${r.unlinked} chapter(s)`)
                      exitSelectMode()
                    },
                  })
                }
              >
                Unlink
              </Button>
              <Button
                size="xs"
                variant="default"
                leftSection={<IconX size={15} />}
                onClick={exitSelectMode}
              >
                Done
              </Button>
            </Group>
          </Group>
        </Paper>
      )}

      <LinkChaptersModal
        seriesId={seriesId}
        chapterIds={[...selected]}
        opened={linkModalOpen}
        onClose={() => {
          setLinkModalOpen(false)
          exitSelectMode()
        }}
      />

      {!chapters || chapters.length === 0 ? (
        <Text c="dimmed" size="sm">
          No chapters known. Link a source and refresh.
        </Text>
      ) : (
        <Table.ScrollContainer minWidth={560}>
          <Table highlightOnHover verticalSpacing="xs">
            <Table.Thead>
              <Table.Tr>
                {selectMode && <Table.Th w={40} />}
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
                  {selectMode && (
                    <Table.Td>
                      <Checkbox
                        size="xs"
                        checked={selected.has(c.id)}
                        onChange={() => toggleChapterSelected(c.id)}
                      />
                    </Table.Td>
                  )}
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
