import { useEffect, useRef, useState } from 'react'
import {
  ActionIcon,
  Alert,
  Badge,
  Button,
  Card,
  Checkbox,
  Code,
  FileButton,
  Group,
  Modal,
  MultiSelect,
  NumberInput,
  Radio,
  Stack,
  Switch,
  Table,
  Text,
  TextInput,
  Title,
  UnstyledButton,
} from '@mantine/core'
import {
  IconAlertTriangle,
  IconCheck,
  IconDownload,
  IconGripVertical,
  IconTrash,
  IconUpload,
} from '@tabler/icons-react'
import { notifications } from '@mantine/notifications'
import { PageHeader } from '../components/ui/PageHeader'
import { RecommendationModelCards } from '../components/RecommendationModelCards'
import {
  useAddRootFolder,
  useBackups,
  useBackupSettings,
  useCreateBackup,
  useDeleteBackup,
  useRestoreBackup,
  useSaveBackupSettings,
  useUploadRestore,
  downloadBackup,
  useCompleteSetup,
  useConnectionSettings,
  useDeleteRootFolder,
  useDownloadSettings,
  useLibrarySettings,
  useSaveLibrarySettings,
  useFlareSolverrSettings,
  useGeneralSettings,
  useMetadataSettings,
  useMonitoringSettings,
  useProwlarrIndexers,
  useProwlarrOptions,
  useRecommendationIndex,
  useRefreshMetadataDump,
  useRootFolders,
  useSaveDownloadSettings,
  useSaveFlareSolverr,
  useSaveMetadataSettings,
  useSaveMonitoringSettings,
  useSaveProwlarrOptions,
  useSaveScrobbleSettings,
  useSaveSourcePriority,
  useSetEmbeddingModel,
  useScrobbleSettings,
  useScrobbleStatus,
  useSourcePriority,
  useSources,
  useTestFlareSolverr,
  useCheckForUpdatesNow,
  useSaveUpdateSettings,
  useUpdateSettings,
  useUpdateStatus,
  type FolderNamingMode,
  type ScrobbleSettings,
} from '../api/hooks'
import { ConnectionSettingsCard } from '../components/ConnectionSettingsCard'
import { NotificationsSection } from '../components/NotificationsSection'
import { TrackerSyncControls } from '../components/TrackerSyncControls'
import { useThemeChoice } from '../theme-context'

function formatBytes(bytes: number | null): string {
  if (bytes === null) return '—'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit++
  }
  return `${value.toFixed(1)} ${units[unit]}`
}

function RootFoldersSection() {
  const [newPath, setNewPath] = useState('')
  const { data: rootFolders } = useRootFolders()
  const addFolder = useAddRootFolder()
  const deleteFolder = useDeleteRootFolder()

  const add = () => {
    if (!newPath.trim()) return
    addFolder.mutate(newPath.trim(), {
      onSuccess: () => setNewPath(''),
    })
  }

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        Root Folders
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        Library folders where series are stored (point Kavita at the same location).
      </Text>
      <Stack>
        {rootFolders && rootFolders.length > 0 && (
          <Table>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Path</Table.Th>
                <Table.Th>Free space</Table.Th>
                <Table.Th />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {rootFolders.map((f) => (
                <Table.Tr key={f.id}>
                  <Table.Td>
                    {f.path}
                    {!f.accessible && (
                      <Text span c="red" size="xs" ml="xs">
                        (inaccessible)
                      </Text>
                    )}
                  </Table.Td>
                  <Table.Td>{formatBytes(f.freeSpace)}</Table.Td>
                  <Table.Td>
                    <ActionIcon
                      variant="subtle"
                      color="red"
                      onClick={() =>
                        deleteFolder.mutate(f.id, {
                        })
                      }
                      aria-label="Delete root folder"
                    >
                      <IconTrash size={16} />
                    </ActionIcon>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}
        <Group>
          <TextInput
            placeholder="C:\Manga or /library"
            value={newPath}
            onChange={(e) => setNewPath(e.currentTarget.value)}
            style={{ flex: 1 }}
          />
          <Button onClick={add} loading={addFolder.isPending}>
            Add
          </Button>
        </Group>
      </Stack>
    </Card>
  )
}

function SourcesSection() {
  const { data: sources } = useSources()

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        Sources
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        Built-in site scrapers available for linking series.
      </Text>
      <Table>
        <Table.Tbody>
          {sources?.map((s) => (
            <Table.Tr key={s.name}>
              <Table.Td>
                <Text fw={600}>{s.displayName}</Text>
              </Table.Td>
              <Table.Td>
                <Text size="sm" c="dimmed">
                  {s.baseUrl}
                </Text>
              </Table.Td>
              <Table.Td>
                {s.needsFlareSolverr && (
                  <Badge size="sm" color="orange" variant="light">
                    Needs FlareSolverr
                  </Badge>
                )}
              </Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </Card>
  )
}

function SourcePrioritySection() {
  const { data: sources } = useSources()
  const { data: priority } = useSourcePriority()
  const save = useSaveSourcePriority()
  const [order, setOrder] = useState<string[] | null>(null)
  const dragIndex = useRef<number | null>(null)
  const [overIndex, setOverIndex] = useState<number | null>(null)

  useEffect(() => {
    if (priority) setOrder(priority.order)
  }, [priority])

  const displayName = (name: string) => sources?.find((s) => s.name === name)?.displayName ?? name
  const dirty = order !== null && priority !== undefined && order.join(',') !== priority.order.join(',')

  function reorder(from: number, to: number) {
    if (!order || from === to) return
    const next = [...order]
    const [moved] = next.splice(from, 1)
    next.splice(to, 0, moved)
    setOrder(next)
  }

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        Source priority
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        When a series auto-matches multiple sources, chapters download from the highest-priority
        enabled source first. Applies to new auto-matches and manual "Auto-match" runs — existing
        series mappings keep their current priorities. Drag to reorder.
      </Text>
      <Stack gap={4} mb="md">
        {order?.map((name, i) => (
          <Group
            key={name}
            justify="space-between"
            wrap="nowrap"
            py={4}
            px={4}
            draggable
            onDragStart={() => {
              dragIndex.current = i
            }}
            onDragEnter={() => setOverIndex(i)}
            onDragOver={(e) => e.preventDefault()}
            onDrop={(e) => {
              e.preventDefault()
              if (dragIndex.current !== null) reorder(dragIndex.current, i)
              dragIndex.current = null
              setOverIndex(null)
            }}
            onDragEnd={() => {
              dragIndex.current = null
              setOverIndex(null)
            }}
            style={{
              cursor: 'grab',
              borderRadius: 4,
              outline: overIndex === i ? '2px solid var(--mantine-color-brand-5)' : undefined,
            }}
          >
            <Group gap="sm" wrap="nowrap">
              <IconGripVertical size={14} opacity={0.5} />
              <Text size="sm" c="dimmed" w={20}>
                {i + 1}
              </Text>
              <Text size="sm" fw={500}>
                {displayName(name)}
              </Text>
            </Group>
          </Group>
        ))}
      </Stack>
      <Button
        variant="default"
        disabled={!dirty}
        loading={save.isPending}
        onClick={() =>
          order &&
          save.mutate(order, {
            onSuccess: () => notifications.show({ message: 'Saved', color: 'green' }),
          })
        }
      >
        Save
      </Button>
    </Card>
  )
}

function MetadataSection() {
  const { data: settings } = useMetadataSettings()
  const save = useSaveMetadataSettings()
  const refresh = useRefreshMetadataDump()

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        Metadata
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        Series metadata comes from MangaBaka. With the local database enabled, Maki keeps a
        nightly snapshot on disk (~3 GB) so searches and library imports are instant instead of
        rate-limited. Until the first download finishes, the API is used automatically.
      </Text>
      <Stack gap="sm">
        <Switch
          label="Use local MangaBaka database"
          checked={settings?.useLocalDb ?? true}
          onChange={(e) =>
            save.mutate(e.currentTarget.checked, {
            })
          }
        />
        <Group justify="space-between">
          <Text size="sm" c="dimmed">
            {settings === undefined
              ? '...'
              : settings.dumpPresent
                ? `Snapshot on disk: ${formatBytes(settings.dumpSizeBytes)}, refreshed ${
                    settings.dumpRefreshedAt
                      ? new Date(settings.dumpRefreshedAt).toLocaleString()
                      : 'at an unknown time'
                  }`
              : 'No snapshot downloaded yet'}
          </Text>
          <Button
            variant="default"
            size="xs"
            loading={refresh.isPending}
            onClick={() =>
              refresh.mutate(undefined, {
                onSuccess: () =>
                  notifications.show({
                    message: 'Refresh started — downloading in the background if a new snapshot is available',
                    color: 'green',
                  }),
              })
            }
          >
            Refresh now
          </Button>
        </Group>
      </Stack>
    </Card>
  )
}

function RecommendationIndexSection() {
  const { data: status } = useRecommendationIndex()
  const setModel = useSetEmbeddingModel()  

  const selectModel = (kind: string) =>
    setModel.mutate(kind, {
      onSuccess: (r) =>
        notifications.show({
          message: r.switching
            ? kind === 'off'
              ? 'Turning embeddings off…'
              : `Switching to ${kind}: downloading the model and index…`
            : r.reason,
          color: r.switching ? 'blue' : 'gray',
        }),
      onError: (e) => notifications.show({ message: String(e), color: 'red' }),
    })

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        Recommendations
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        Discover recommends by semantic "feel" and searches by description, using a local embedding
        model. Pick how much muscle it gets — or turn it off. The vectors download prebuilt and
        refresh nightly, so this normally needs no attention; search falls back to titles and
        recommendations to genres whenever it's off or still downloading.
      </Text>

      <RecommendationModelCards status={status} busy={setModel.isPending} onSelect={selectModel} />
    </Card>
  )
}

function MonitoringSection() {
  const { data: settings } = useMonitoringSettings()
  const save = useSaveMonitoringSettings()

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        Monitoring
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        Specials are decimal chapters (10.5 omake, x.1/x.2 splits). When enabled, newly added
        or imported series monitor main chapters only — specials stay listed but are never
        auto-downloaded. Existing series are unaffected; change them per series or via the
        library bulk "Monitoring" action.
      </Text>
      <Switch
        label="Don't monitor specials on new series"
        checked={settings?.unmonitorSpecials ?? false}
        onChange={(e) =>
          save.mutate(e.currentTarget.checked, {
          })
        }
      />
    </Card>
  )
}

function LibrarySection() {
  const { data: settings } = useLibrarySettings()
  const save = useSaveLibrarySettings()

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        Library files
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        Maki writes a standardized <Code>ComicInfo.xml</Code> into each CBZ so Kavita groups and
        names chapters consistently. Turn this off to leave imported files (torrent grabs and
        manual imports) exactly as they came — chapters Maki downloads itself from a source still
        get a ComicInfo, since Maki builds those files. You can always standardize a single series
        later with the "Update ComicInfo" bulk action on its page.
      </Text>
      <Switch
        mb="lg"
        label="Write ComicInfo.xml into imported files"
        checked={settings?.writeComicInfo ?? true}
        onChange={(e) =>
          save.mutate(
            { writeComicInfo: e.currentTarget.checked, folderNamingMode: settings?.folderNamingMode ?? 'rename' },
            { onSuccess: () => notifications.show({ message: 'Saved', color: 'green' }) },
          )
        }
      />

      <Text fw={500} size="sm" mb={4}>
        Folder naming
      </Text>
      <Text size="sm" c="dimmed" mb="sm">
        Controls whether Maki renames an imported series' folder to its standard sanitized-title
        name, or leaves it as found.
      </Text>
      <Radio.Group
        value={settings?.folderNamingMode ?? 'rename'}
        onChange={(value) =>
          save.mutate(
            { writeComicInfo: settings?.writeComicInfo ?? true, folderNamingMode: value as FolderNamingMode },
            { onSuccess: () => notifications.show({ message: 'Saved', color: 'green' }) },
          )
        }
      >
        <Stack gap="xs" mt="xs">
          <Radio value="rename" label="Rename folder to Maki standard" />
          <Radio
            value="keep-new-standard"
            label="Keep folder name, but put new downloads in a Maki standard folder"
          />
          <Radio value="keep-original" label="Keep folder name, and put new downloads there too" />
        </Stack>
      </Radio.Group>
    </Card>
  )
}

function DownloadSection() {
  const { data: settings } = useDownloadSettings()
  const save = useSaveDownloadSettings()
  const [concurrentChapters, setConcurrentChapters] = useState<number | string>(2)
  const [retryEnabled, setRetryEnabled] = useState(true)
  const [retryMaxAttempts, setRetryMaxAttempts] = useState<number | string>(5)

  useEffect(() => {
    if (settings) {
      setConcurrentChapters(settings.concurrentChapters)
      setRetryEnabled(settings.retryEnabled)
      setRetryMaxAttempts(settings.retryMaxAttempts)
    }
  }, [settings])

  const dirty =
    settings !== undefined &&
    (Number(concurrentChapters) !== settings.concurrentChapters ||
      retryEnabled !== settings.retryEnabled ||
      Number(retryMaxAttempts) !== settings.retryMaxAttempts)

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        Downloads
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        How many chapters download at once from scraper sources. Higher isn't always faster —
        each worker is a live connection to the same site, and tripping its rate limit pauses
        every download. Torrent releases aren't affected. Takes effect after a restart.
      </Text>
      <NumberInput
        label="Concurrent chapter downloads"
        min={1}
        max={8}
        clampBehavior="strict"
        value={concurrentChapters}
        onChange={setConcurrentChapters}
        w={220}
        mb="md"
      />
      <Text size="sm" c="dimmed" mb="xs">
        Failed downloads are automatically retried on an escalating backoff (5m, 10m, 20m, ...) up
        to the attempt cap below. A manual retry from the Activity page doesn't count against it.
      </Text>
      <Group align="flex-end" mb="md">
        <Switch
          label="Automatically retry failed downloads"
          checked={retryEnabled}
          onChange={(e) => setRetryEnabled(e.currentTarget.checked)}
        />
        <NumberInput
          label="Max attempts"
          min={1}
          max={20}
          clampBehavior="strict"
          value={retryMaxAttempts}
          onChange={setRetryMaxAttempts}
          disabled={!retryEnabled}
          w={140}
        />
      </Group>
      <Button
        variant="default"
        disabled={!dirty}
        loading={save.isPending}
        onClick={() =>
          save.mutate(
            {
              concurrentChapters: Number(concurrentChapters),
              retryEnabled,
              retryMaxAttempts: Number(retryMaxAttempts),
            },
            {
              onSuccess: () =>
                notifications.show({ message: 'Saved — restart Maki to apply', color: 'green' }),
            },
          )
        }
      >
        Save
      </Button>
    </Card>
  )
}

type RestoreTarget = { kind: 'existing'; name: string } | { kind: 'upload'; file: File }

function BackupSection() {
  const { data: backups } = useBackups()
  const { data: retentionSettings } = useBackupSettings()
  const create = useCreateBackup()
  const remove = useDeleteBackup()
  const restore = useRestoreBackup()
  const upload = useUploadRestore()
  const saveRetention = useSaveBackupSettings()

  const [retention, setRetention] = useState<number | string>(5)
  const [target, setTarget] = useState<RestoreTarget | null>(null)

  useEffect(() => {
    if (retentionSettings) setRetention(retentionSettings.retention)
  }, [retentionSettings])

  const retentionDirty =
    retentionSettings !== undefined && Number(retention) !== retentionSettings.retention

  const restarting = () =>
    notifications.show({
      title: 'Restore staged',
      message: 'Maki is restarting to apply it. Reload in a moment.',
      color: 'blue',
      autoClose: false,
    })

  const confirmRestore = () => {
    if (!target) return
    const onSuccess = () => {
      setTarget(null)
      restarting()
    }
    const onError = (e: Error) =>
      notifications.show({ title: 'Restore failed', message: e.message, color: 'red' })

    if (target.kind === 'existing') restore.mutate(target.name, { onSuccess, onError })
    else upload.mutate(target.file, { onSuccess, onError })
  }

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        Backup &amp; Restore
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        A backup is a zip of your database and <Code>config.json</Code> — your whole library and all
        settings. Big, re-downloadable data (the MangaBaka dump, embeddings, covers, cache) is left
        out. One is taken automatically right before any upgrade migration runs. Restoring replaces
        the current data and restarts Maki.
      </Text>
      <Alert color="yellow" icon={<IconAlertTriangle size={16} />} mb="md" variant="light">
        Backup files contain your settings secrets (API keys, passwords) in plain text. Treat a
        downloaded backup like a password. Restore auto-recovers only under a supervisor (Docker /
        systemd); a bare process just stops and you restart it yourself.
      </Alert>

      <Stack>
        {backups && backups.length > 0 && (
          <Table>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Created</Table.Th>
                <Table.Th>Kind</Table.Th>
                <Table.Th>Version</Table.Th>
                <Table.Th>Size</Table.Th>
                <Table.Th />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {backups.map((b) => (
                <Table.Tr key={b.name}>
                  <Table.Td>{new Date(b.manifest.createdUtc).toLocaleString()}</Table.Td>
                  <Table.Td>
                    <Badge size="sm" variant="light" color={b.manifest.kind === 'auto' ? 'gray' : 'blue'}>
                      {b.manifest.kind}
                    </Badge>
                  </Table.Td>
                  <Table.Td>
                    <Text size="xs" c="dimmed">
                      {b.manifest.appVersion}
                    </Text>
                  </Table.Td>
                  <Table.Td>{formatBytes(b.sizeBytes)}</Table.Td>
                  <Table.Td>
                    <Group gap="xs" justify="flex-end" wrap="nowrap">
                      <Button
                        size="xs"
                        variant="light"
                        onClick={() => setTarget({ kind: 'existing', name: b.name })}
                      >
                        Restore
                      </Button>
                      <ActionIcon
                        variant="subtle"
                        onClick={() => void downloadBackup(b.name)}
                        aria-label="Download backup"
                      >
                        <IconDownload size={16} />
                      </ActionIcon>
                      <ActionIcon
                        variant="subtle"
                        color="red"
                        onClick={() => remove.mutate(b.name)}
                        aria-label="Delete backup"
                      >
                        <IconTrash size={16} />
                      </ActionIcon>
                    </Group>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}

        <Group>
          <Button
            onClick={() =>
              create.mutate(undefined, {
                onSuccess: () => notifications.show({ message: 'Backup created', color: 'green' }),
              })
            }
            loading={create.isPending}
          >
            Back up now
          </Button>
          <FileButton onChange={(f) => f && setTarget({ kind: 'upload', file: f })} accept=".zip">
            {(props) => (
              <Button {...props} variant="default" leftSection={<IconUpload size={16} />}>
                Restore from file…
              </Button>
            )}
          </FileButton>
        </Group>

        <Group align="flex-end">
          <NumberInput
            label="Backups to keep (per kind)"
            min={1}
            max={50}
            clampBehavior="strict"
            value={retention}
            onChange={setRetention}
            w={220}
          />
          <Button
            variant="default"
            disabled={!retentionDirty}
            loading={saveRetention.isPending}
            onClick={() =>
              saveRetention.mutate(
                { retention: Number(retention) },
                { onSuccess: () => notifications.show({ message: 'Saved', color: 'green' }) },
              )
            }
          >
            Save
          </Button>
        </Group>
      </Stack>

      <Modal opened={target !== null} onClose={() => setTarget(null)} title="Restore backup" centered>
        <Stack>
          <Text size="sm">
            This replaces your current library and settings with{' '}
            {target?.kind === 'upload' ? (
              <b>{target.file.name}</b>
            ) : (
              <b>{target?.kind === 'existing' ? target.name : ''}</b>
            )}
            , then restarts Maki. The current data is not kept — take a backup first if you want a
            way back.
          </Text>
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setTarget(null)}>
              Cancel
            </Button>
            <Button color="red" loading={restore.isPending || upload.isPending} onClick={confirmRestore}>
              Restore &amp; restart
            </Button>
          </Group>
        </Stack>
      </Modal>
    </Card>
  )
}

function ProwlarrOptionsSection() {
  const { data: connection } = useConnectionSettings<Record<string, string | null>>('prowlarr')
  const configured = Boolean(connection?.url && connection?.apiKey)
  const { data: indexers, error: indexersError } = useProwlarrIndexers(configured)
  const { data: options } = useProwlarrOptions()
  const save = useSaveProwlarrOptions()
  const [selectedIndexers, setSelectedIndexers] = useState<Set<number>>(new Set())
  const [categories, setCategories] = useState<string[]>([])

  useEffect(() => {
    if (options) {
      setSelectedIndexers(
        new Set((options.indexerIds ?? '').split(',').filter(Boolean).map(Number)),
      )
      setCategories((options.categories ?? '').split(',').filter(Boolean))
    }
  }, [options])

  const categoryData = [
    ...new Map(
      (indexers ?? [])
        .flatMap((i) => i.categories)
        .map((c) => [String(c.id), { value: String(c.id), label: `${c.name} (${c.id})` }]),
    ).values(),
    // keep saved categories selectable even when no indexer advertises them
    ...categories
      .filter((c) => !(indexers ?? []).some((i) => i.categories.some((x) => String(x.id) === c)))
      .map((c) => ({ value: c, label: c })),
  ].sort((a, b) => Number(a.value) - Number(b.value))

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        Prowlarr search options
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        Restrict release searches to specific indexers and Torznab categories. With nothing
        selected, every indexer and category is searched.
      </Text>
      {!configured && (
        <Text size="sm" c="dimmed">
          Configure the Prowlarr URL and API key above first.
        </Text>
      )}
      {configured && indexersError != null && (
        <Text size="sm" c="red">
          Could not load indexers from Prowlarr: {String(indexersError)}
        </Text>
      )}
      {configured && indexers && (
        <Stack gap="sm">
          <Stack gap={6}>
            {indexers.map((indexer) => (
              <Checkbox
                key={indexer.id}
                label={`${indexer.name}${indexer.enable ? '' : ' (disabled in Prowlarr)'}`}
                checked={selectedIndexers.has(indexer.id)}
                onChange={(e) => {
                  const checked = e.currentTarget.checked
                  setSelectedIndexers((prev) => {
                    const next = new Set(prev)
                    if (checked) next.add(indexer.id)
                    else next.delete(indexer.id)
                    return next
                  })
                }}
              />
            ))}
            {indexers.length === 0 && (
              <Text size="sm" c="dimmed">
                No indexers configured in Prowlarr.
              </Text>
            )}
          </Stack>
          <MultiSelect
            label="Categories"
            placeholder={categories.length === 0 ? 'All categories' : undefined}
            data={categoryData}
            value={categories}
            onChange={setCategories}
            searchable
            clearable
          />
          <Group justify="flex-end">
            <Button
              loading={save.isPending}
              onClick={() =>
                save.mutate(
                  {
                    indexerIds: [...selectedIndexers].sort((a, b) => a - b).join(',') || null,
                    categories: categories.join(',') || null,
                  },
                  {
                    onSuccess: () => notifications.show({ message: 'Saved', color: 'green' }),
                  },
                )
              }
            >
              Save
            </Button>
          </Group>
        </Stack>
      )}
    </Card>
  )
}

function FlareSolverrSection() {
  const { data: settings } = useFlareSolverrSettings()
  const save = useSaveFlareSolverr()
  const test = useTestFlareSolverr()
  const [url, setUrl] = useState('')

  useEffect(() => {
    if (settings?.url) setUrl(settings.url)
  }, [settings?.url])

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        FlareSolverr
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        Required for Cloudflare-protected sources like MangaFire. Point this at a running
        FlareSolverr instance (e.g. http://localhost:8191).
      </Text>
      <Group>
        <TextInput
          placeholder="http://localhost:8191"
          value={url}
          onChange={(e) => setUrl(e.currentTarget.value)}
          style={{ flex: 1 }}
        />
        <Button
          variant="default"
          loading={test.isPending}
          onClick={() =>
            test.mutate(url || null, {
              onSuccess: () =>
                notifications.show({ message: 'FlareSolverr is reachable', color: 'green' }),
            })
          }
        >
          Test
        </Button>
        <Button
          loading={save.isPending}
          onClick={() =>
            save.mutate(url || null, {
              onSuccess: () => notifications.show({ message: 'Saved', color: 'green' }),
            })
          }
        >
          Save
        </Button>
      </Group>
    </Card>
  )
}

function ScrobbleSection() {
  const { data } = useScrobbleSettings()
  const { data: status } = useScrobbleStatus()
  const save = useSaveScrobbleSettings()
  const [form, setForm] = useState<ScrobbleSettings | null>(null)

  useEffect(() => {
    if (data && form === null) setForm(data)
  }, [data, form])

  const conn = (service: string) => status?.connections.find((c) => c.service === service)

  const set = (patch: Partial<ScrobbleSettings>) =>
    setForm((f) => (f ? { ...f, ...patch } : f))

  const origin = window.location.origin

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="xs">
        Scrobbling
      </Title>
      <Text size="sm" c="dimmed" mb="sm">
        Pushes your Kavita reading progress to AniList, MyAnimeList and MangaBaka (any
        combination — leave a site's credentials empty to disable it). Manage connections and
        review matches on the Scrobble page. Uses the Kavita connection configured above.
      </Text>
      <Stack gap="xs">
        <Text size="sm" fw={600}>
          AniList
        </Text>
        <Text size="xs" c="dimmed">
          Create an API client at anilist.co/settings/developer with redirect URL{' '}
          <Code>{origin}/api/v1/scrobble/oauth/anilist</Code>
        </Text>
        <Group grow>
          <TextInput
            label="Client ID"
            value={form?.aniListClientId ?? ''}
            onChange={(e) => set({ aniListClientId: e.currentTarget.value })}
          />
          <TextInput
            label="Client secret"
            type="password"
            value={form?.aniListClientSecret ?? ''}
            onChange={(e) => set({ aniListClientSecret: e.currentTarget.value })}
          />
        </Group>
        <TrackerSyncControls service="anilist" label="AniList" connection={conn('anilist')} />

        <Text size="sm" fw={600} mt="xs">
          MyAnimeList
        </Text>
        <Text size="xs" c="dimmed">
          Create an API client at myanimelist.net/apiconfig (App Type: web) with redirect URL{' '}
          <Code>{origin}/api/v1/scrobble/oauth/mal</Code>. Paste the <b>Client ID</b> (not the
          secret) exactly as shown there. If connecting opens a browser “sign in to
          myanimelist.net” popup and then <Code>invalid_client</Code>, MyAnimeList didn&apos;t
          recognise the Client ID — re-copy it and make sure the App Type is set.
        </Text>
        <Group grow>
          <TextInput
            label="Client ID"
            value={form?.malClientId ?? ''}
            onChange={(e) => set({ malClientId: e.currentTarget.value })}
          />
          <TextInput
            label="Client secret"
            type="password"
            value={form?.malClientSecret ?? ''}
            onChange={(e) => set({ malClientSecret: e.currentTarget.value })}
          />
        </Group>
        <TrackerSyncControls service="mal" label="MyAnimeList" connection={conn('mal')} />

        <Text size="sm" fw={600} mt="xs">
          MangaBaka
        </Text>
        <TextInput
          label="Personal Access Token"
          description="From MangaBaka settings — no OAuth needed, works immediately"
          type="password"
          placeholder="mb-..."
          value={form?.mangaBakaToken ?? ''}
          onChange={(e) => set({ mangaBakaToken: e.currentTarget.value })}
        />
        <TrackerSyncControls service="mangabaka" label="MangaBaka" connection={conn('mangabaka')} />

        <Text size="sm" fw={600} mt="xs">
          Kitsu
        </Text>
        <Group grow>
          <TextInput
            label="Email"
            value={form?.kitsuEmail ?? ''}
            onChange={(e) => set({ kitsuEmail: e.currentTarget.value })}
          />
          <TextInput
            label="Password"
            type="password"
            value={form?.kitsuPassword ?? ''}
            onChange={(e) => set({ kitsuPassword: e.currentTarget.value })}
          />
        </Group>
        <TrackerSyncControls service="kitsu" label="Kitsu" connection={conn('kitsu')} />

        <Group grow mt="xs">
          <TextInput
            label="Sync interval (minutes)"
            value={form?.intervalMinutes?.toString() ?? '30'}
            onChange={(e) => {
              const parsed = parseInt(e.currentTarget.value, 10)
              set({ intervalMinutes: Number.isNaN(parsed) ? 30 : parsed })
            }}
          />
          <TextInput
            label="Kavita library ids"
            description="Comma-separated; empty = scrobble all libraries"
            value={form?.libraryIds ?? ''}
            onChange={(e) => set({ libraryIds: e.currentTarget.value })}
          />
        </Group>
        <Switch
          label="Add unread series as plan-to-read"
          description="Series in Kavita with no reading progress are added to the sites as 'plan to read'. Never modifies entries already on your lists."
          checked={form?.planToRead ?? false}
          onChange={(e) => {
            const checked = e.currentTarget.checked
            set({ planToRead: checked })
          }}
        />
        <Group justify="flex-end">
          <Button
            loading={save.isPending}
            disabled={!form}
            onClick={() =>
              form &&
              save.mutate(form, {
                onSuccess: () => notifications.show({ message: 'Saved', color: 'green' }),
              })
            }
          >
            Save
          </Button>
        </Group>
      </Stack>
    </Card>
  )
}

function AppearanceSection() {
  const { themeId, setThemeId, presets } = useThemeChoice()

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb={4}>
        Appearance
      </Title>
      <Text size="sm" c="dimmed" mb="sm">
        Pick an accent colour, or switch to the light theme. Applies instantly and is remembered
        on this device.
      </Text>
      <Group gap="sm">
        {presets.map((p) => {
          const active = p.id === themeId
          return (
            <UnstyledButton
              key={p.id}
              onClick={() => setThemeId(p.id)}
              aria-pressed={active}
              style={{
                display: 'flex',
                alignItems: 'center',
                gap: 8,
                padding: '8px 12px',
                borderRadius: 10,
                border: `1px solid ${active ? 'var(--brand)' : 'var(--border)'}`,
                background: active ? 'var(--surface-hover)' : 'transparent',
                boxShadow: active ? '0 0 0 1px var(--brand)' : undefined,
              }}
            >
              <span
                style={{
                  width: 18,
                  height: 18,
                  borderRadius: '50%',
                  background: p.swatch,
                  border: '1px solid rgba(0,0,0,0.25)',
                  flexShrink: 0,
                }}
              />
              <Text size="sm" fw={active ? 600 : 500}>
                {p.label}
              </Text>
              {active && <IconCheck size={14} style={{ color: 'var(--brand)' }} />}
            </UnstyledButton>
          )
        })}
      </Group>
    </Card>
  )
}

function GeneralSection() {
  const { data: general } = useGeneralSettings()
  const completeSetup = useCompleteSetup()

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        General
      </Title>
      <Stack gap="xs">
        <Group>
          <Text size="sm" w={80}>
            API key
          </Text>
          <Code>{general?.apiKey ?? '...'}</Code>
        </Group>
        <Group>
          <Text size="sm" w={80}>
            Port
          </Text>
          <Code>{general?.port ?? '...'}</Code>
        </Group>
        <Group justify="space-between" mt="xs">
          <Text size="sm" c="dimmed">
            Re-open the first-time setup guide.
          </Text>
          <Button
            variant="default"
            size="xs"
            loading={completeSetup.isPending}
            onClick={() => completeSetup.mutate(false)}
          >
            Run setup guide
          </Button>
        </Group>
      </Stack>
    </Card>
  )
}

function UpdatesSection() {
  const { data: settings } = useUpdateSettings()
  const save = useSaveUpdateSettings()
  const { data: status } = useUpdateStatus()
  const checkNow = useCheckForUpdatesNow()

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        Updates
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        Checks GitHub daily for a newer release and raises a banner and a Notifications event
        when one is found.{' '}
        {status?.isDocker
          ? "Docker installs are notify-only — pull the new image and recreate the container."
          : 'Bare installs are notify-only — pull the latest code and rebuild.'}
      </Text>
      <Stack gap="sm">
        <Switch
          label="Check for updates"
          checked={settings?.checkForUpdates ?? true}
          onChange={(e) => save.mutate(e.currentTarget.checked)}
        />
        <Group justify="space-between">
          <Text size="sm" c="dimmed">
            {status?.isDevBuild
              ? 'Unofficial build — update checks are skipped.'
              : status?.updateAvailable
                ? `Update available: ${status.latestVersion}`
                : status?.checkedAt
                  ? `Up to date, last checked ${new Date(status.checkedAt).toLocaleString()}`
                  : 'Not checked yet'}
          </Text>
          <Button
            variant="default"
            size="xs"
            loading={checkNow.isPending}
            disabled={status?.isDevBuild}
            onClick={() =>
              checkNow.mutate(undefined, {
                onSuccess: (r) =>
                  notifications.show({
                    message: r.updateAvailable
                      ? `Maki ${r.latestVersion} is available`
                      : 'Already up to date',
                    color: r.updateAvailable ? 'yellow' : 'green',
                  }),
              })
            }
          >
            Check now
          </Button>
        </Group>
      </Stack>
    </Card>
  )
}

export default function SettingsPage() {
  return (
    <>
      <PageHeader
        title="Settings"
        description="Storage, metadata, download clients and integrations for your Maki instance."
      />
      <Stack maw={820}>
        <RootFoldersSection />
        <MetadataSection />
        <RecommendationIndexSection />
        <MonitoringSection />
        <LibrarySection />
        <DownloadSection />
        <BackupSection />
        <SourcesSection />
        <SourcePrioritySection />
        <FlareSolverrSection />
        <ConnectionSettingsCard
          name="prowlarr"
          title="Prowlarr"
          description="Search manga releases on your indexers. Uses Prowlarr's aggregated search API — no app sync needed."
          fields={[
            { key: 'url', label: 'URL', placeholder: 'http://localhost:9696' },
            { key: 'apiKey', label: 'API key', secret: true },
          ]}
        />
        <ProwlarrOptionsSection />
        <ConnectionSettingsCard
          name="qbittorrent"
          title="qBittorrent"
          description="Download client for grabbed releases. Completed torrents are imported into the library automatically (category defaults to 'maki'). If qBittorrent reports download paths Maki can't reach (e.g. it runs in Docker and reports /downloads while Maki sees Z:\downloads), fill the optional path mapping to translate them."
          fields={[
            { key: 'url', label: 'URL', placeholder: 'http://localhost:8080' },
            { key: 'username', label: 'Username' },
            { key: 'password', label: 'Password', secret: true },
            { key: 'category', label: 'Category', placeholder: 'maki' },
            { key: 'pathMapFrom', label: 'Path mapping — qBittorrent side', placeholder: '/downloads (optional)' },
            { key: 'pathMapTo', label: 'Path mapping — Maki side', placeholder: 'Z:\\downloads (optional)' },
          ]}
        />
        <ConnectionSettingsCard
          name="kavita"
          title="Kavita"
          description="When configured, Maki asks Kavita to scan the series folder right after new chapters download or imported files change, then pushes the series poster, web links and publication status into Kavita (covers you've set yourself in Kavita are never overwritten). Get the API key from Kavita under User Settings → 3rd Party Clients. If Kavita sees the library under a different path (e.g. it runs in Docker), fill the optional path mapping so Maki translates folder paths."
          fields={[
            { key: 'url', label: 'URL', placeholder: 'http://localhost:5000' },
            { key: 'apiKey', label: 'API key', secret: true },
            { key: 'pathMapFrom', label: 'Path mapping — Maki side', placeholder: 'C:\\Manga (optional)' },
            { key: 'pathMapTo', label: 'Path mapping — Kavita side', placeholder: '/manga (optional)' },
          ]}
        />
        <ScrobbleSection />
        <NotificationsSection />
        <UpdatesSection />
        <AppearanceSection />
        <GeneralSection />
      </Stack>
    </>
  )
}
