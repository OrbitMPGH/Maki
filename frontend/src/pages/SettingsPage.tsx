import { useEffect, useState } from 'react'
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
  Progress,
  Stack,
  Switch,
  Table,
  Text,
  TextInput,
  Title,
  UnstyledButton,
} from '@mantine/core'
import { IconAlertTriangle, IconCheck, IconDownload, IconTrash, IconUpload } from '@tabler/icons-react'
import { notifications } from '@mantine/notifications'
import { PageHeader } from '../components/ui/PageHeader'
import {
  useAddRootFolder,
  useBackups,
  useBackupSettings,
  useBuildRecommendationIndex,
  useCreateBackup,
  useDeleteBackup,
  useRestoreBackup,
  useSaveBackupSettings,
  useUploadRestore,
  downloadBackup,
  useConnectionSettings,
  useDeleteRootFolder,
  useDownloadSettings,
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
  useSetRecommendationAutoIndex,
  useScrobbleSettings,
  useSources,
  useTestFlareSolverr,
  type ScrobbleSettings,
} from '../api/hooks'
import { ConnectionSettingsCard } from '../components/ConnectionSettingsCard'
import { NotificationsSection } from '../components/NotificationsSection'
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
        Series metadata comes from MangaBaka. With the local database enabled, Mangarr keeps a
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
  const build = useBuildRecommendationIndex()
  const setAutoIndex = useSetRecommendationAutoIndex()

  const running = status?.running ?? false
  const total = status?.recommendableTotal ?? null
  const done = running ? status?.embedded ?? 0 : status?.vectorCount ?? 0
  const pct = total && total > 0 ? Math.min(100, Math.round((done / total) * 100)) : null

  let state = '…'
  if (status) {
    if (running) {
      state =
        status.phase === 'preparing'
          ? 'Preparing model…'
          : `Indexing… ${status.embedded.toLocaleString()}${total ? ` / ${total.toLocaleString()}` : ''}`
    } else if (!status.dumpPresent) {
      state = 'Waiting for the MangaBaka snapshot to download first.'
    } else if (status.vectorCount === 0) {
      state = 'Not built yet — recommendations use genre matching until you build it.'
    } else {
      state = `${status.vectorCount.toLocaleString()}${total ? ` / ${total.toLocaleString()}` : ''} series embedded.${
        status.finishedAt ? ` Last run ${new Date(status.finishedAt).toLocaleString()}.` : ''
      }`
    }
  }

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        Recommendation index
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        Discover recommends by semantic "feel" using a local embedding model (~34 MB, downloaded on
        first build). The first pass takes a few minutes and is CPU-heavy; recommendations fall back
        to genre matching until it's ready. Build it on demand below, or enable automatic rebuilds.
      </Text>
      <Stack gap="sm">
        <Switch
          label="Rebuild automatically"
          description="Refresh the index shortly after startup and daily. Off by default so the CPU-heavy pass only runs when you build it."
          checked={status?.autoIndex ?? false}
          disabled={!status || setAutoIndex.isPending}
          onChange={(e) =>
            setAutoIndex.mutate(e.currentTarget.checked, {
            })
          }
        />
        {(running || pct !== null) && (
          <Progress
            value={running && pct === null ? 100 : (pct ?? 0)}
            animated={running}
            striped={running}
            color={status?.lastError ? 'red' : 'brand'}
          />
        )}
        <Group justify="space-between" wrap="nowrap">
          <div>
            <Text size="sm">{state}</Text>
            {status && !status.modelPresent && !running && (
              <Text size="xs" c="dimmed">
                Model not downloaded yet.
              </Text>
            )}
            {status?.lastError && (
              <Text size="xs" c="red">
                Last error: {status.lastError}
              </Text>
            )}
          </div>
          <Button
            variant="default"
            size="xs"
            loading={build.isPending}
            disabled={running || !(status?.dumpPresent ?? false)}
            onClick={() =>
              build.mutate(undefined, {
                onSuccess: (r) =>
                  notifications.show({
                    message: r.started ? 'Indexing started in the background' : r.message ?? 'Already running',
                    color: r.started ? 'green' : 'yellow',
                  }),
              })
            }
          >
            {running ? 'Indexing…' : status && status.vectorCount > 0 ? 'Rebuild index' : 'Build index'}
          </Button>
        </Group>
      </Stack>
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

function DownloadSection() {
  const { data: settings } = useDownloadSettings()
  const save = useSaveDownloadSettings()
  const [value, setValue] = useState<number | string>(2)

  useEffect(() => {
    if (settings) setValue(settings.concurrentChapters)
  }, [settings])

  const dirty = settings !== undefined && Number(value) !== settings.concurrentChapters

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
      <Group align="flex-end">
        <NumberInput
          label="Concurrent chapter downloads"
          min={1}
          max={8}
          clampBehavior="strict"
          value={value}
          onChange={setValue}
          w={220}
        />
        <Button
          variant="default"
          disabled={!dirty}
          loading={save.isPending}
          onClick={() =>
            save.mutate(Number(value), {
              onSuccess: () =>
                notifications.show({ message: 'Saved — restart Mangarr to apply', color: 'green' }),
            })
          }
        >
          Save
        </Button>
      </Group>
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
      message: 'Mangarr is restarting to apply it. Reload in a moment.',
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
        the current data and restarts Mangarr.
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
            , then restarts Mangarr. The current data is not kept — take a backup first if you want a
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
  const save = useSaveScrobbleSettings()
  const [form, setForm] = useState<ScrobbleSettings | null>(null)

  useEffect(() => {
    if (data && form === null) setForm(data)
  }, [data, form])

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
      </Stack>
    </Card>
  )
}

export default function SettingsPage() {
  return (
    <>
      <PageHeader
        title="Settings"
        description="Storage, metadata, download clients and integrations for your Mangarr instance."
      />
      <Stack maw={820}>
        <RootFoldersSection />
        <MetadataSection />
        <RecommendationIndexSection />
        <MonitoringSection />
        <DownloadSection />
        <BackupSection />
        <SourcesSection />
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
          description="Download client for grabbed releases. Completed torrents are imported into the library automatically (category defaults to 'mangarr'). If qBittorrent reports download paths Mangarr can't reach (e.g. it runs in Docker and reports /downloads while Mangarr sees Z:\downloads), fill the optional path mapping to translate them."
          fields={[
            { key: 'url', label: 'URL', placeholder: 'http://localhost:8080' },
            { key: 'username', label: 'Username' },
            { key: 'password', label: 'Password', secret: true },
            { key: 'category', label: 'Category', placeholder: 'mangarr' },
            { key: 'pathMapFrom', label: 'Path mapping — qBittorrent side', placeholder: '/downloads (optional)' },
            { key: 'pathMapTo', label: 'Path mapping — Mangarr side', placeholder: 'Z:\\downloads (optional)' },
          ]}
        />
        <ConnectionSettingsCard
          name="kavita"
          title="Kavita"
          description="When configured, Mangarr asks Kavita to scan the series folder right after new chapters download or imported files change, then pushes the series poster, web links and publication status into Kavita (covers you've set yourself in Kavita are never overwritten). Get the API key from Kavita under User Settings → 3rd Party Clients. If Kavita sees the library under a different path (e.g. it runs in Docker), fill the optional path mapping so Mangarr translates folder paths."
          fields={[
            { key: 'url', label: 'URL', placeholder: 'http://localhost:5000' },
            { key: 'apiKey', label: 'API key', secret: true },
            { key: 'pathMapFrom', label: 'Path mapping — Mangarr side', placeholder: 'C:\\Manga (optional)' },
            { key: 'pathMapTo', label: 'Path mapping — Kavita side', placeholder: '/manga (optional)' },
          ]}
        />
        <ScrobbleSection />
        <NotificationsSection />
        <AppearanceSection />
        <GeneralSection />
      </Stack>
    </>
  )
}
