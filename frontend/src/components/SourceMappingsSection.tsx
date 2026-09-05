import { useState } from 'react'
import {
  ActionIcon,
  Alert,
  Anchor,
  Badge,
  Box,
  Button,
  Card,
  Checkbox,
  Group,
  Image,
  Loader,
  Modal,
  NumberInput,
  Select,
  Stack,
  Switch,
  Table,
  Text,
  TextInput,
  Title,
  Tooltip,
} from '@mantine/core'
import {
  IconColumns,
  IconExternalLink,
  IconLink,
  IconPlugConnected,
  IconRefresh,
  IconTrash,
  IconWand,
} from '@tabler/icons-react'
import { useDebouncedValue } from '@mantine/hooks'
import { notifications } from '@mantine/notifications'
import {
  useAutoMatchSources,
  useCreateMapping,
  useDeleteMapping,
  useRefreshSourceSnapshots,
  useRemoveMapping,
  useResolveSourceUrl,
  useSourceMappings,
  useSources,
  useSourceSearch,
  useUpdateMapping,
} from '../api/hooks'
import type { SourceMappingDto } from '../api/types'
import { useAuth } from '../auth/AuthProvider'
import { SourceCompareModal } from './SourceCompareModal'

const ORIGIN_LABELS: Record<string, string> = {
  TitleSearch: 'Title search',
  CrossId: 'Cross-id',
  Manual: 'Manual'
}

const ORIGIN_COLORS: Record<string, string> = {
  TitleSearch: 'blue',
  CrossId: 'grape',
  Manual: 'teal',
  Unknown: 'gray'
}

const SOURCE_ICONS: Record<string, string> = {
  asura: '/source-icons/asura.webp',
  atsumaru: '/source-icons/atsumaru.ico',
  flamecomics: '/source-icons/flamecomics.png',
  mangadex: '/source-icons/mangadex.ico',
  mangafire: '/source-icons/mangafire.svg',
  mangakakalot: '/source-icons/mangakakalot.ico',
  mangakatana: '/source-icons/mangakatana.png',
  mangapill: '/source-icons/mangapill.png',
  mangaplus: '/source-icons/mangaplus.ico',
  tcbscans: '/source-icons/tcbscans.png',
  topmanhua: '/source-icons/topmanhua.png',
  webtoons: '/source-icons/webtoons.ico',
  weebcentral: '/source-icons/weebcentral.ico',
}

export function SourceMappingsSection({
  seriesId,
  seriesTitle,
  matching = false,
}: {
  seriesId: number
  seriesTitle: string
  /**
   * Auto-matching is still running in the background (a series added seconds ago). Without it a
   * fresh series reads "No sources linked. Chapters cannot be synced or downloaded." for the half
   * minute the source searches take, which is alarming and wrong.
   */
  matching?: boolean
}) {
  const { data: mappings } = useSourceMappings(seriesId)
  const { data: sources } = useSources()
  const updateMapping = useUpdateMapping()
  const deleteMapping = useDeleteMapping()
  const removeMapping = useRemoveMapping()
  const createMapping = useCreateMapping()
  const autoMatch = useAutoMatchSources()
  const refreshSnapshots = useRefreshSourceSnapshots()
  const { can } = useAuth()

  const [modalOpen, setModalOpen] = useState(false)
  const [compareOpen, setCompareOpen] = useState(false)
  const [sourceName, setSourceName] = useState<string | null>(null)
  const [query, setQuery] = useState(seriesTitle)
  const [removing, setRemoving] = useState<SourceMappingDto | null>(null)
  const [deleteFiles, setDeleteFiles] = useState(false)
  const [fallbackOpen, setFallbackOpen] = useState(false)
  const [debounced] = useDebouncedValue(query, 400)
  // A pasted URL bypasses search: the backend maps it to a source + series id.
  const pastedUrl = /^https?:\/\//i.test(debounced.trim()) ? debounced.trim() : ''
  const { data: results, isFetching } = useSourceSearch(
    sourceName ?? '',
    pastedUrl ? '' : debounced,
  )
  const {
    data: resolved,
    isFetching: resolving,
    error: resolveError,
  } = useResolveSourceUrl(pastedUrl)

  // A source switched off in Settings can't be linked, and its existing mappings here are
  // inert: their own on/off state is kept and shown read-only rather than being rewritten.
  const unmappedSources = sources?.filter(
    (s) => s.enabled && !mappings?.some((m) => m.sourceName === s.name),
  )
  const sourceDisabled = (name: string) =>
    sources?.some((s) => s.name === name && !s.enabled) ?? false
  const nothingLeftToMatch = !unmappedSources || unmappedSources.length === 0
  // Both switches, as everywhere else: a mapping is only live if its own toggle is on *and* the
  // source isn't switched off globally. Comparing one source against nothing proves nothing.
  const comparable =
    mappings?.filter((m) => m.enabled && !sourceDisabled(m.sourceName)).length ?? 0
  const missingSnapshots =
    mappings?.filter(
      (m) =>
        m.id !== removing?.id &&
        m.enabled &&
        !sourceDisabled(m.sourceName) &&
        !m.chapterSnapshotAt,
    ) ?? []

  const link = (name: string, sourceSeriesId: string, url: string) =>
    createMapping.mutate(
      { seriesId, sourceName: name, sourceSeriesId, url },
      {
        onSuccess: () => {
          notifications.show({ message: `Linked ${name}`, color: 'green' })
          setModalOpen(false)
        },
      },
    )

  return (
    <>
      <Group justify="space-between" mb="md">
        <Group gap={8}>
          <IconPlugConnected size={18} style={{ color: 'var(--ink-4)' }} />
          <Title order={3} fz={17}>
            Linked sources
          </Title>
        </Group>
        {/* Both held while auto-matching runs: (SeriesId, SourceName) is unique, so a hand-linked
            source that the matcher is about to add itself fails its whole batch of mappings. */}
        <Group gap="xs">
          <Tooltip
            label={
              matching
                ? "Auto-matching is already running."
                : nothingLeftToMatch
                  ? 'Every enabled source is already linked.'
                  : 'Search the remaining sources for this title again. Sources already linked are left alone.'
            }
            withArrow
            multiline
            w={240}
          >
            <Box component="span" display="inline-flex">
              <Button
                size="xs"
                variant="default"
                leftSection={<IconWand size={14} />}
                disabled={matching || nothingLeftToMatch}
                loading={autoMatch.isPending}
                onClick={() =>
                  autoMatch.mutate([seriesId], {
                    onSuccess: () =>
                      notifications.show({
                        message: 'Searching sources for a match…',
                      }),
                  })
                }
              >
                Auto-match
              </Button>
            </Box>
          </Tooltip>
          <Tooltip
            label={
              matching
                ? "Auto-matching is still running. It'll be free in a moment."
                : comparable < 2
                  ? 'Needs at least two enabled sources to compare.'
                  : 'Fetch a sample of the same chapter from each source and rank them by scan quality.'
            }
            withArrow
            multiline
            w={240}
          >
            <Box component="span" display="inline-flex">
              <Button
                size="xs"
                variant="default"
                leftSection={<IconColumns size={14} />}
                disabled={matching || comparable < 2}
                onClick={() => setCompareOpen(true)}
              >
                Compare
              </Button>
            </Box>
          </Tooltip>
          <Tooltip
            label="Auto-matching is still running. It'll be free in a moment."
            withArrow
            disabled={!matching}
          >
            <Box component="span" display="inline-flex">
              <Button
                size="xs"
                variant="light"
                leftSection={<IconLink size={14} />}
                disabled={matching || nothingLeftToMatch}
                onClick={() => {
                  setSourceName(unmappedSources?.[0]?.name ?? null)
                  setQuery(seriesTitle)
                  setModalOpen(true)
                }}
              >
                Link source
              </Button>
            </Box>
          </Tooltip>
        </Group>
      </Group>

      {matching && (
        <Group gap="xs">
          <Loader size="xs" />
          <Text c="dimmed" size="sm">
            {mappings && mappings.length > 0
              ? 'Matching the remaining sources…'
              : 'Searching sources for a match…'}
          </Text>
        </Group>
      )}

      {!mappings || mappings.length === 0 ? (
        !matching && (
          <Text c="dimmed" size="sm">
            No sources linked. Chapters cannot be synced or downloaded.
          </Text>
        )
      ) : (
        <Table.ScrollContainer minWidth={720}>
          <Table className="panel-table">
            <Table.Thead>
            <Table.Tr>
              <Table.Th>Source</Table.Th>
              <Table.Th>Series</Table.Th>
              <Table.Th>Priority</Table.Th>
              <Table.Th>Enabled</Table.Th>
              <Table.Th>Last refresh</Table.Th>
              <Table.Th />
            </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {mappings.map((m) => (
              <Table.Tr key={m.id}>
                <Table.Td>
                  <Group gap="xs" wrap="nowrap">
                    {SOURCE_ICONS[m.sourceName] && (
                      <Image
                        src={SOURCE_ICONS[m.sourceName]}
                        alt=""
                        w={20}
                        h={20}
                        fit="contain"
                        style={{
                          flex: '0 0 auto',
                          opacity: sourceDisabled(m.sourceName) ? 0.45 : 1,
                        }}
                      />
                    )}
                    <Text fw={600} size="sm" c={sourceDisabled(m.sourceName) ? 'dimmed' : undefined}>
                      {m.sourceName}
                    </Text>
                    {sourceDisabled(m.sourceName) && (
                      <Badge size="xs" color="gray" variant="light">
                        Source off
                      </Badge>
                    )}
                    {m.origin && m.origin != "Unknown" && <Tooltip label={m.origin == "CrossId" ? "Resolved using ID. High accuracy" : m.origin == "TitleSearch" ? "Resolved using fuzzy title search. Medium accuracy" : "Added manually"}>
                      <Badge size="xs" color={ORIGIN_COLORS[m.origin] ?? 'gray'} variant="light">
                        {ORIGIN_LABELS[m.origin] ?? m.origin}
                      </Badge>
                    </Tooltip>}
                  </Group>
                </Table.Td>
                <Table.Td>
                  <Anchor href={m.url} target="_blank" size="sm">
                    {m.sourceSeriesId}
                  </Anchor>
                </Table.Td>
                <Table.Td>
                  <Tooltip label="Lower number = tried first when downloading" withArrow>
                    <NumberInput
                      size="xs"
                      w={70}
                      min={1}
                      max={99}
                      value={m.priority}
                      onChange={(v) => {
                        const priority = typeof v === 'number' ? v : Number(v)
                        if (Number.isFinite(priority) && priority !== m.priority) {
                          updateMapping.mutate({ ...m, priority })
                        }
                      }}
                    />
                  </Tooltip>
                </Table.Td>
                <Table.Td>
                  <Tooltip
                    label={`${m.sourceName} is switched off in Settings → Source priority. This series' setting is kept and applies again once it's back on.`}
                    withArrow
                    multiline
                    w={260}
                    disabled={!sourceDisabled(m.sourceName)}
                  >
                    {/* A disabled input fires no pointer events, so the tooltip has to hang off
                        a wrapper. Greyed, not flipped: the stored flag is left exactly as-is. */}
                    <Box component="span" display="inline-flex">
                      <Switch
                        size="xs"
                        checked={m.enabled}
                        disabled={sourceDisabled(m.sourceName)}
                        onChange={(e) =>
                          updateMapping.mutate({ ...m, enabled: e.currentTarget.checked })
                        }
                      />
                    </Box>
                  </Tooltip>
                </Table.Td>
                <Table.Td>
                  {m.lastError ? (
                    <Tooltip label={m.lastError} withArrow>
                      <Badge size="sm" color="red" variant="light">
                        Error
                      </Badge>
                    </Tooltip>
                  ) : (
                    <Text size="xs" c="dimmed">
                      {m.lastRefresh ? new Date(m.lastRefresh).toLocaleString() : 'never'}
                    </Text>
                  )}
                </Table.Td>
                <Table.Td>
                  <ActionIcon
                    variant="subtle"
                    color="red"
                    onClick={() => {
                      setRemoving(m)
                      setDeleteFiles(false)
                    }}
                    aria-label="Remove mapping"
                  >
                    <IconTrash size={16} />
                  </ActionIcon>
                </Table.Td>
              </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      )}

      <Modal
        opened={removing !== null && !fallbackOpen}
        onClose={() => setRemoving(null)}
        title={removing ? `Remove ${removing.sourceName}?` : 'Remove source?'}
        centered
      >
        <Stack gap="md">
          <Text size="sm" c="dimmed">
            Chapters not listed by another enabled source will be removed. Files downloaded from
            this source will be detached so they cannot be read as the correct chapter.
          </Text>
          <Text size="sm" c="dimmed">
            Detached CBZs stay in the Files section unless you choose to delete them.
          </Text>

          {missingSnapshots.length > 0 && (
            <Alert color="yellow" title="One refresh required">
              <Stack gap="xs">
                <Text size="sm">
                  {missingSnapshots.map((m) => m.sourceName).join(', ')} must record a chapter
                  snapshot before Maki can safely clean the list. Later source removals use the
                  stored snapshots and make no source requests.
                </Text>
                <Button
                  size="xs"
                  variant="light"
                  leftSection={<IconRefresh size={14} />}
                  loading={refreshSnapshots.isPending}
                  onClick={() => {
                    if (!removing) return
                    refreshSnapshots.mutate({ seriesId, excludeMappingId: removing.id }, {
                      onSuccess: () =>
                        notifications.show({
                          message: 'Chapter snapshots refreshed',
                          color: 'green',
                        }),
                    })
                  }}
                >
                  Refresh chapters
                </Button>
              </Stack>
            </Alert>
          )}

          {can('DeleteSeries') && (
            <Checkbox
              label="Also delete detached files from disk"
              checked={deleteFiles}
              onChange={(event) => setDeleteFiles(event.currentTarget.checked)}
            />
          )}

          <Text size="sm" c="red">
            Reading progress and bookmarks for removed chapter rows will also be deleted.
          </Text>
          <Group justify="space-between">
            <Button
              variant="subtle"
              color="red"
              onClick={() => setFallbackOpen(true)}
            >
              Remove without cleanup
            </Button>
            <Group gap="xs">
              <Button variant="default" onClick={() => setRemoving(null)}>
                Cancel
              </Button>
              <Button
                color="red"
                leftSection={<IconTrash size={16} />}
                disabled={missingSnapshots.length > 0 || !removing}
                loading={removeMapping.isPending}
                onClick={() =>
                  removing &&
                  removeMapping.mutate(
                    { id: removing.id, seriesId, deleteFiles },
                    {
                      onSuccess: (result) => {
                        const kept = result.detachedFiles - result.deletedFiles
                        const failures = result.failedFileDeletions > 0
                          ? `; ${result.failedFileDeletions} could not be deleted`
                          : ''
                        notifications.show({
                          message: `Removed ${result.removedChapters} unsupported chapter(s); ${kept} file(s) left unlinked${failures}`,
                          color: result.failedFileDeletions > 0 ? 'orange' : 'green',
                        })
                        setRemoving(null)
                      },
                    },
                  )
                }
              >
                Remove and clean up
              </Button>
            </Group>
          </Group>
        </Stack>
      </Modal>

      <Modal
        opened={fallbackOpen}
        onClose={() => setFallbackOpen(false)}
        title="Remove source without cleanup?"
        centered
      >
        <Stack gap="md">
          <Text size="sm" c="dimmed">
            This removes only the source mapping. Existing chapter rows and files will stay exactly
            as they are and may need manual cleanup later.
          </Text>
          <Text size="sm" c="red">
            This action cannot be undone.
          </Text>
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setFallbackOpen(false)}>
              Cancel
            </Button>
            <Button
              color="red"
              loading={deleteMapping.isPending}
              onClick={() =>
                removing &&
                deleteMapping.mutate(
                  { id: removing.id, seriesId },
                  {
                    onSuccess: () => {
                      notifications.show({ message: 'Source removed without cleanup', color: 'orange' })
                      setFallbackOpen(false)
                      setRemoving(null)
                    },
                  },
                )
              }
            >
              Remove source only
            </Button>
          </Group>
        </Stack>
      </Modal>

      <Modal
        opened={modalOpen}
        onClose={() => setModalOpen(false)}
        title="Link a source"
        size="lg"
      >
        <Stack>
          <Group grow>
            <Select
              label="Source"
              data={
                unmappedSources?.map((s) => ({ value: s.name, label: s.displayName })) ?? []
              }
              value={sourceName}
              onChange={setSourceName}
            />
            <TextInput
              label="Search or paste a series URL"
              value={query}
              onChange={(e) => setQuery(e.currentTarget.value)}
              rightSection={isFetching || resolving ? <Loader size="xs" /> : null}
            />
          </Group>
          <Stack gap="xs">
            {pastedUrl && resolved && (
              <Card
                withBorder
                padding="xs"
                style={{ cursor: 'pointer' }}
                onClick={() => link(resolved.sourceName, resolved.sourceSeriesId, resolved.url)}
              >
                <Group wrap="nowrap">
                  {resolved.coverUrl && (
                    <Image src={resolved.coverUrl} w={40} h={60} radius="sm" fit="cover" alt="" />
                  )}
                  <div style={{ flex: 1, minWidth: 0 }}>
                    <Group gap="xs">
                      <Text fw={600} size="sm">
                        {resolved.title}
                      </Text>
                      <Badge size="xs" variant="light">
                        {resolved.displayName}
                      </Badge>
                    </Group>
                    <Text size="xs" c="dimmed" lineClamp={1}>
                      {resolved.url}
                    </Text>
                  </div>
                </Group>
              </Card>
            )}
            {pastedUrl && resolveError && (
              <Text c="red" size="sm">
                {String(resolveError)}
              </Text>
            )}
            {!pastedUrl && results?.map((r) => (
              <Card
                key={r.sourceSeriesId}
                withBorder
                padding="xs"
                style={{ cursor: 'pointer' }}
                onClick={() => sourceName && link(sourceName, r.sourceSeriesId, r.url)}
              >
                <Group wrap="nowrap">
                  {r.coverUrl && (
                    <Image src={r.coverUrl} w={40} h={60} radius="sm" fit="cover" alt="" />
                  )}
                  <div style={{ flex: 1, minWidth: 0 }}>
                    <Text fw={600} size="sm">
                      {r.title}
                    </Text>
                    <Text size="xs" c="dimmed" lineClamp={1}>
                      {r.url}
                    </Text>
                  </div>
                  <Tooltip label="Open the page to check it's the right series" withArrow>
                    <ActionIcon
                      component="a"
                      href={r.url}
                      target="_blank"
                      rel="noreferrer"
                      variant="subtle"
                      onClick={(e) => e.stopPropagation()}
                      aria-label="Open source page"
                    >
                      <IconExternalLink size={16} />
                    </ActionIcon>
                  </Tooltip>
                </Group>
              </Card>
            ))}
            {sourceName && debounced.trim().length > 1 && results?.length === 0 && !isFetching && (
              <Text c="dimmed" size="sm">
                No results.
              </Text>
            )}
          </Stack>
        </Stack>
      </Modal>

      <SourceCompareModal
        seriesId={seriesId}
        opened={compareOpen}
        onClose={() => setCompareOpen(false)}
      />
    </>
  )
}
