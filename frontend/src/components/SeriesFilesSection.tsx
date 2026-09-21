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
import { formatBytes } from '../format'
import { Trans, Plural, useLingui } from '@lingui/react/macro'
import { msg, plural, t as now } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { useLabel } from '../i18n-context'

const statusVisual: Record<string, { color: string; label: MessageDescriptor; icon: typeof IconLink }> = {
  linked: { color: 'teal', label: msg`Linked`, icon: IconLink },
  unlinked: { color: 'yellow', label: msg`Not linked`, icon: IconLinkOff },
  unrecognized: { color: 'orange', label: msg`Unrecognized`, icon: IconFileUnknown },
  missing: { color: 'red', label: msg`Missing from disk`, icon: IconFileUnknown },
}

/** "21" → "Ch. 21"; ["21","22","23"] → "Ch. 21, 22, 23". */
function mappedLabel(file: SeriesFileDto): string {
  const { mappedChapters } = file
  if (mappedChapters.length === 0) return '-'
  const chapters = mappedChapters.join(', ')
  return now`Ch. ${chapters}`
}

export function SeriesFilesSection({ seriesId }: { seriesId: number }) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const [selectMode, setSelectMode] = useState(false)
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [confirmOpen, setConfirmOpen] = useState(false)
  const { data: files, isLoading, isFetching, refetch } = useSeriesFiles(seriesId)
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
      <Group justify="space-between" wrap="wrap" gap="sm">
        <Group gap="xs" align="center">
          <IconFileZip size={18} />
          <Title order={3}>
            <Trans>Files</Trans>
          </Title>
          {files && (
            <Text size="sm" c="dimmed" className="tnum">
              {files.length}
              {problems > 0 && (
                <>
                  {' · '}
                  <Plural value={problems} one="# needs attention" other="# need attention" />
                </>
              )}
            </Text>
          )}
        </Group>
        <Group gap="xs">
            <Button
              size="xs"
              variant="subtle"
              leftSection={<IconRefresh size={14} />}
              loading={isFetching}
              onClick={() => void refetch()}
            >
              <Trans>Refresh</Trans>
            </Button>
            {files && files.length > 0 && !selectMode && (
              <Button
                size="xs"
                variant="subtle"
                onClick={() => setSelectMode(true)}
              >
                <Trans>Select</Trans>
              </Button>
            )}
          </Group>
      </Group>

      {(isLoading ? (
          <Group py="md" gap="xs">
            <Loader size="sm" />
            <Text size="sm" c="dimmed">
              <Trans>Scanning folder…</Trans>
            </Text>
          </Group>
        ) : !files || files.length === 0 ? (
          <Text c="dimmed" size="sm" py="sm">
            <Trans>No files in the series folder.</Trans>
          </Text>
        ) : (
        <>
          {selectMode && (
            <Paper bg="var(--mantine-color-dark-8)" px="sm" py="xs" mt="sm" style={{ borderRadius: 'var(--mantine-radius-sm)' }}>
              <Group gap="xs" justify="space-between">
                <Group gap="xs">
                  <Text size="sm" c="dimmed">
                    <Plural value={selected.size} one="# selected" other="# selected" />
                  </Text>
                  <Button
                    size="xs"
                    variant="subtle"
                    onClick={() =>
                      setSelected(new Set(files.filter((f) => f.onDisk).map((f) => f.relativePath)))
                    }
                  >
                    <Trans>Select all on disk</Trans>
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
                    <Trans>Delete selected</Trans>
                  </Button>
                  <Button
                    size="xs"
                    variant="default"
                    leftSection={<IconX size={15} />}
                    onClick={exitSelectMode}
                  >
                    <Trans>Done</Trans>
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
                  <Table.Th><Trans>File</Trans></Table.Th>
                  <Table.Th w={90}><Trans>Parsed</Trans></Table.Th>
                  <Table.Th w={160}><Trans>Status</Trans></Table.Th>
                  <Table.Th><Trans>Mapped to</Trans></Table.Th>
                  <Table.Th w={90}><Trans>Size</Trans></Table.Th>
                  {!selectMode && <Table.Th w={40} />}
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {files.map((f) => {
                  const v = statusVisual[f.status] ?? statusVisual.unrecognized
                  const { fileName } = f
                  return (
                    <Table.Tr key={f.relativePath} opacity={f.status === 'missing' ? 0.6 : 1}>
                      {selectMode && (
                        <Table.Td>
                          <Checkbox
                            checked={selected.has(f.relativePath)}
                            onChange={() => toggleSelected(f.relativePath)}
                            disabled={!f.onDisk}
                            aria-label={t`Select ${fileName}`}
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
                          {renderLabel(v.label)}
                        </Badge>
                      </Table.Td>
                      <Table.Td>
                        {f.isVolume && f.mappedChapters.length > 0 ? (
                          <Tooltip
                            label={plural(f.mappedChapters.length, {
                              one: 'Volume file backing # chapter',
                              other: 'Volume file backing # chapters',
                            })}
                            withArrow
                          >
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
                          <Tooltip label={f.onDisk ? t`Delete from disk` : t`Missing from disk`} withArrow>
                            <ActionIcon
                              variant="subtle"
                              color="red"
                              disabled={!f.onDisk}
                              onClick={() => {
                                setSelected(new Set([f.relativePath]))
                                setSelectMode(true)
                                setConfirmOpen(true)
                              }}
                              aria-label={t`Delete ${fileName}`}
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
            title={t`Delete files from disk?`}
            centered
          >
            <Stack gap="md">
              <Text size="sm" c="dimmed">
                <Plural
                  value={selected.size}
                  one="This will permanently delete # CBZ file from disk."
                  other="This will permanently delete # CBZ files from disk."
                />{' '}
                <Trans>Chapters that share a volume CBZ will also lose their file.</Trans>
              </Text>
              <Text size="sm" c="red">
                <Trans>This action cannot be undone.</Trans>
              </Text>
              <Group justify="flex-end">
                <Button variant="default" onClick={() => setConfirmOpen(false)}>
                  <Trans>Cancel</Trans>
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
                              ? `${plural(r.deleted, { one: 'Deleted # file', other: 'Deleted # files' })}, ${plural(
                                  r.failed,
                                  {
                                    one: '# could not be deleted (locked or permission denied)',
                                    other: '# could not be deleted (locked or permission denied)',
                                  },
                                )}`
                              : plural(r.deleted, { one: 'Deleted # file', other: 'Deleted # files' }),
                        })
                        setConfirmOpen(false)
                        exitSelectMode()
                      },
                    })
                  }
                >
                  <Trans>Delete</Trans>
                </Button>
              </Group>
            </Stack>
          </Modal>
        </>
      ))}
    </div>
  )
}
