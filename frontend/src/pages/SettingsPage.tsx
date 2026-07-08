import { useEffect, useState } from 'react'
import {
  ActionIcon,
  Badge,
  Button,
  Card,
  Code,
  Group,
  Stack,
  Table,
  Text,
  TextInput,
  Title,
} from '@mantine/core'
import { notifications } from '@mantine/notifications'
import {
  useAddRootFolder,
  useDeleteRootFolder,
  useFlareSolverrSettings,
  useGeneralSettings,
  useRootFolders,
  useSaveFlareSolverr,
  useSources,
  useTestFlareSolverr,
} from '../api/hooks'

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
        <SourcesSection />
        <FlareSolverrSection />
        <GeneralSection />
      </Stack>
    </>
  )
}
