import { useState } from 'react'
import { ActionIcon, Badge, Button, Checkbox, Group, Loader, Modal, Paper, Stack, Table, Text, Title, Tooltip } from '@mantine/core'
import {
  IconFileUnknown,
  IconFileZip,
  IconLink,
  IconLinkOff,
  IconRefresh,
  IconTrash,
  IconX,
} from '@tabler/icons-react'
import { notifications } from '@mantine/notifications'
import { useSeriesFiles, useDeleteSeriesFiles } from '../api/hooks'
import type { SeriesFileDto } from '../api/types'

function formatBytes(bytes: number): string {
  if (bytes <= 0) return '-'
  const units = ['B', 'KB', 'MB', 'GB']
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit++
  }
  return `${value.toFixed(value >= 10 || unit === 0 ? 0 : 1)} ${units[unit]}`
}

const statusVisual: Record<string, { color: string; label: string; icon: typeof IconLink }> = {
  linked: { color: 'teal', label: 'Linked', icon: IconLink },
  unlinked: { color: 'yellow', label: 'Not linked', icon: IconLinkOff },
  unrecognized: { color: 'orange', label: 'Unrecognized', icon: IconFileUnknown },
  missing: { color: 'red', label: 'Missing from disk', icon: IconFileUnknown },
}

/** "21" → "Ch. 21"; ["21","22","23"] → "Ch. 21, 22, 23". */
function mappedLabel(file: SeriesFileDto): string {
  if (file.mappedChapters.length === 0) return '-'
  return `Ch. ${file.mappedChapters.join(', ')}`
}

export function SeriesFilesSection({ seriesId }: { seriesId: number }) {
  const [selectMode, setSelectMode] = useState(false)
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [confirmOpen, setConfirmOpen] = useState(false)
  const { data: files, isLoading, isFetching, refetch } = useSeriesFiles(seriesId, true)
  const deleteFiles = useDeleteSeriesFiles(seriesId)

  const problems = files?.filter((f) => f.status !== 'linked').length ?? 0

  const exitSelectMode = () => {
    setSelectMode(false)
    setSelected(new Set())
  }

  const toggleSelected = (path: string) =>
    setSelected((s) => {
      const next = new Set(s)
      if (next.has(path)) next.delete(path)
      else next.add(path)
      return next
    })

  return (
    <div>
      <Group justify="space-between" wrap="wrap" gap="sm" mb="md">
        <Group gap="xs" align="center">
          <IconFileZip size={18} style={{ color: 'var(--ink-4)' }} />
          <Title order={3} fz={17}>
            Files on disk
          </Title>
          {files && (
            <Text size="sm" c="dimmed" className="tnum">
              {files.length}
              {problems > 0 ? ` · ${problems} need attention` : ''}
            </Text>
          )}
        </Group>
        <Group gap="xs">
          <Button
            size="xs"
            variant="default"
            leftSection={<IconRefresh size={14} />}
            loading={isFetching}
            onClick={() => void refetch()}
          >
            Refresh
          </Button>
          {files && files.length > 0 && !selectMode && (
            <Button size="xs" variant="default" onClick={() => setSelectMode(true)}>
              Select
            </Button>
          )}
        </Group>
      </Group>

      {(isLoading ? (
          <Group py="md" gap="xs">
            <Loader size="sm" />
            <Text size="sm" c="dimmed">
              Scanning folder…
            </Text>
          </Group>
        ) : !files || files.length === 0 ? (
          <Text c="dimmed" size="sm" py="sm">
            No files in the series folder.
          </Text>
        ) : (
        <>
          {selectMode && (
            <Paper bg="var(--mantine-color-dark-8)" px="sm" py="xs" mt="sm" style={{ borderRadius: 'var(--mantine-radius-sm)' }}>
              <Group gap="xs" justify="space-between">
                <Group gap="xs">
                  <Text size="sm" c="dimmed">
                    {selected.size} selected
                  </Text>
                  <Button
                    size="xs"
                    variant="subtle"
                    onClick={() =>
                      setSelected(new Set(files.filter((f) => f.onDisk).map((f) => f.relativePath)))
                    }
                  >
                    Select all on disk
                  </Button>
                </Group>
                <Group gap="xs">
                  <Button
                    size="xs"
                    variant="light"
                    color="red"
                    leftSection={<IconTrash size={15} />}
                    disabled={selected.size === 0}
                    onClick={() => setConfirmOpen(true)}
                  >
                    Delete selected
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

          <Table.ScrollContainer minWidth={640} mt="sm">
            <Table highlightOnHover verticalSpacing="xs">
              <Table.Thead>
                <Table.Tr>
                  {selectMode && <Table.Th w={40} />}
                  <Table.Th>File</Table.Th>
                  <Table.Th w={90}>Parsed</Table.Th>
                  <Table.Th w={160}>Status</Table.Th>
                  <Table.Th>Mapped to</Table.Th>
                  <Table.Th w={90}>Size</Table.Th>
                  {!selectMode && <Table.Th w={40} />}
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {files.map((f) => {
                  const v = statusVisual[f.status] ?? statusVisual.unrecognized
                  return (
                    <Table.Tr key={f.relativePath} opacity={f.status === 'missing' ? 0.6 : 1}>
                      {selectMode && (
                        <Table.Td>
                          <Checkbox
                            checked={selected.has(f.relativePath)}
                            onChange={() => toggleSelected(f.relativePath)}
                            disabled={!f.onDisk}
                            aria-label={`Select ${f.fileName}`}
                          />
                        </Table.Td>
                      )}
                      <Table.Td>
                        <Text size="sm" style={{ wordBreak: 'break-all' }}>
                          {f.fileName}
                        </Text>
                      </Table.Td>
                      <Table.Td>
                        {f.parsedLabel ? (
                          <Badge
                            size="sm"
                            variant="light"
                            color={f.isVolume ? 'indigo' : 'gray'}
                            className="tnum"
                          >
                            {f.parsedLabel}
                          </Badge>
                        ) : (
                          <Text size="sm" c="dimmed">
                            -
                          </Text>
                        )}
                      </Table.Td>
                      <Table.Td>
                        <Badge size="sm" color={v.color} variant="light" leftSection={<v.icon size={12} />}>
                          {v.label}
                        </Badge>
                      </Table.Td>
                      <Table.Td>
                        {f.isVolume && f.mappedChapters.length > 0 ? (
                          <Tooltip label={`Volume file backing ${f.mappedChapters.length} chapter(s)`} withArrow>
                            <Text size="sm" className="tnum">
                              {mappedLabel(f)}
                            </Text>
                          </Tooltip>
                        ) : (
                          <Text size="sm" c={f.mappedChapters.length ? undefined : 'dimmed'} className="tnum">
                            {mappedLabel(f)}
                          </Text>
                        )}
                      </Table.Td>
                      <Table.Td>
                        <Text size="sm" c="dimmed" className="tnum">
                          {formatBytes(f.size)}
                        </Text>
                      </Table.Td>
                      {!selectMode && (
                        <Table.Td>
                          <Tooltip label={f.onDisk ? 'Delete from disk' : 'Missing from disk'} withArrow>
                            <ActionIcon
                              variant="subtle"
                              color="red"
                              disabled={!f.onDisk}
                              onClick={() => {
                                setSelected(new Set([f.relativePath]))
                                setSelectMode(true)
                                setConfirmOpen(true)
                              }}
                              aria-label={`Delete ${f.fileName}`}
                            >
                              <IconTrash size={17} />
                            </ActionIcon>
                          </Tooltip>
                        </Table.Td>
                      )}
                    </Table.Tr>
                  )
                })}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>

          <Modal
            opened={confirmOpen}
            onClose={() => setConfirmOpen(false)}
            title="Delete files from disk?"
            centered
          >
            <Stack gap="md">
              <Text size="sm" c="dimmed">
                This will permanently delete {selected.size} CBZ file(s) from disk.
                Chapters that share a volume CBZ will also lose their file.
              </Text>
              <Text size="sm" c="red">
                This action cannot be undone.
              </Text>
              <Group justify="flex-end">
                <Button variant="default" onClick={() => setConfirmOpen(false)}>
                  Cancel
                </Button>
                <Button
                  color="red"
                  leftSection={<IconTrash size={16} />}
                  loading={deleteFiles.isPending}
                  onClick={() =>
                    deleteFiles.mutate([...selected], {
                      onSuccess: (r) => {
                        notifications.show({
                          color: r.failed > 0 ? 'yellow' : 'green',
                          message:
                            r.failed > 0
                              ? `Deleted ${r.deleted} file(s), ${r.failed} could not be deleted (locked or permission denied)`
                              : `Deleted ${r.deleted} file(s)`,
                        })
                        setConfirmOpen(false)
                        exitSelectMode()
                      },
                    })
                  }
                >
                  Delete
                </Button>
              </Group>
            </Stack>
          </Modal>
        </>
      ))}
    </div>
  )
}
