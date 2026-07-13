import { useState } from 'react'
import { ActionIcon, Badge, Button, Group, Loader, Table, Text, Title, Tooltip } from '@mantine/core'
import {
  IconChevronDown,
  IconChevronRight,
  IconFileUnknown,
  IconFileZip,
  IconLink,
  IconLinkOff,
  IconRefresh,
} from '@tabler/icons-react'
import { useSeriesFiles } from '../api/hooks'
import type { SeriesFileDto } from '../api/types'

function formatBytes(bytes: number): string {
  if (bytes <= 0) return '—'
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
  if (file.mappedChapters.length === 0) return '—'
  return `Ch. ${file.mappedChapters.join(', ')}`
}

export function SeriesFilesSection({ seriesId }: { seriesId: number }) {
  const [open, setOpen] = useState(false)
  const { data: files, isLoading, isFetching, refetch } = useSeriesFiles(seriesId, open)

  const problems = files?.filter((f) => f.status !== 'linked').length ?? 0

  return (
    <div>
      <Group justify="space-between" wrap="wrap" gap="sm">
        <Group gap="xs" align="center">
          <ActionIcon variant="subtle" color="gray" onClick={() => setOpen((v) => !v)} aria-label="Toggle files">
            {open ? <IconChevronDown size={18} /> : <IconChevronRight size={18} />}
          </ActionIcon>
          <IconFileZip size={18} />
          <Title order={3} style={{ cursor: 'pointer' }} onClick={() => setOpen((v) => !v)}>
            Files
          </Title>
          {open && files && (
            <Text size="sm" c="dimmed" className="tnum">
              {files.length}
              {problems > 0 ? ` · ${problems} need attention` : ''}
            </Text>
          )}
        </Group>
        {open && (
          <Button
            size="xs"
            variant="subtle"
            leftSection={<IconRefresh size={14} />}
            loading={isFetching}
            onClick={() => void refetch()}
          >
            Refresh
          </Button>
        )}
      </Group>

      {open &&
        (isLoading ? (
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
          <Table.ScrollContainer minWidth={640} mt="sm">
            <Table highlightOnHover verticalSpacing="xs">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>File</Table.Th>
                  <Table.Th w={90}>Parsed</Table.Th>
                  <Table.Th w={160}>Status</Table.Th>
                  <Table.Th>Mapped to</Table.Th>
                  <Table.Th w={90}>Size</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {files.map((f) => {
                  const v = statusVisual[f.status] ?? statusVisual.unrecognized
                  return (
                    <Table.Tr key={f.relativePath} opacity={f.status === 'missing' ? 0.6 : 1}>
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
                            —
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
                    </Table.Tr>
                  )
                })}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        ))}
    </div>
  )
}
