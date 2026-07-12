import { useEffect, useState } from 'react'
import {
  ActionIcon,
  Badge,
  Button,
  Card,
  Checkbox,
  Code,
  Group,
  MultiSelect,
  Progress,
  Stack,
  Switch,
  Table,
  Text,
  TextInput,
  Title,
} from '@mantine/core'
import { notifications } from '@mantine/notifications'
import {
  useAddRootFolder,
  useBuildRecommendationIndex,
  useConnectionSettings,
  useDeleteRootFolder,
  useFlareSolverrSettings,
  useGeneralSettings,
  useMetadataSettings,
  useMonitoringSettings,
  useProwlarrIndexers,
  useProwlarrOptions,
  useRecommendationIndex,
  useRefreshMetadataDump,
  useRootFolders,
  useSaveFlareSolverr,
  useSaveMetadataSettings,
  useSaveMonitoringSettings,
  useSaveProwlarrOptions,
  useSaveScrobbleSettings,
  useScrobbleSettings,
  useSources,
  useTestFlareSolverr,
  type ScrobbleSettings,
} from '../api/hooks'
import { ConnectionSettingsCard } from '../components/ConnectionSettingsCard'

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
      onError: (err) => notifications.show({ message: String(err), color: 'red' }),
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
                          onError: (err) =>
                            notifications.show({ message: String(err), color: 'red' }),
                        })
                      }
                      aria-label="Delete root folder"
                    >
                      ✕
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
              onError: (err) => notifications.show({ message: String(err), color: 'red' }),
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
                onError: (err) => notifications.show({ message: String(err), color: 'red' }),
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
        first build). The index is precomputed once (a few minutes, in the background) and updated
        automatically; recommendations fall back to genre matching until it's ready.
      </Text>
      <Stack gap="sm">
        {(running || pct !== null) && (
          <Progress
            value={running && pct === null ? 100 : (pct ?? 0)}
            animated={running}
            striped={running}
            color={status?.lastError ? 'red' : 'indigo'}
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
                onError: (err) => notifications.show({ message: String(err), color: 'red' }),
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
            onError: (err) => notifications.show({ message: String(err), color: 'red' }),
          })
        }
      />
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
                    onError: (err) => notifications.show({ message: String(err), color: 'red' }),
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
              onError: (err) => notifications.show({ message: String(err), color: 'red' }),
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
              onError: (err) => notifications.show({ message: String(err), color: 'red' }),
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
                onError: (err) => notifications.show({ message: String(err), color: 'red' }),
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
      <Title order={2} mb="md">
        Settings
      </Title>
      <Stack maw={760}>
        <RootFoldersSection />
        <MetadataSection />
        <RecommendationIndexSection />
        <MonitoringSection />
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
        <GeneralSection />
      </Stack>
    </>
  )
}
