import { useState } from 'react'
import {
  ActionIcon,
  Button,
  Card,
  Group,
  Stack,
  Table,
  Text,
  TextInput,
  Title,
} from '@mantine/core'
import { notifications } from '@mantine/notifications'
import { useAddRootFolder, useDeleteRootFolder, useRootFolders } from '../api/hooks'

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

export default function SettingsPage() {
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
    <>
      <Title order={2} mb="md">
        Settings
      </Title>
      <Card withBorder radius="md" padding="md" maw={720}>
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
    </>
  )
}
