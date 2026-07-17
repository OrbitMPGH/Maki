import { useState } from 'react'
import { Badge, Button, Group, Loader, Modal, ScrollArea, Stack, Text, UnstyledButton } from '@mantine/core'
import { IconFileZip } from '@tabler/icons-react'
import { notifications } from '@mantine/notifications'
import { useLinkChapters, useSeriesFiles } from '../api/hooks'
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

export function LinkChaptersModal({
  seriesId,
  chapterIds,
  opened,
  onClose,
}: {
  seriesId: number
  chapterIds: number[]
  opened: boolean
  onClose: () => void
}) {
  const { data: files, isLoading } = useSeriesFiles(seriesId, opened)
  const link = useLinkChapters()
  const [picked, setPicked] = useState<string | null>(null)

  const handleClose = () => {
    setPicked(null)
    onClose()
  }

  const confirm = (file: SeriesFileDto) => {
    link.mutate(
      { chapterIds, relativePath: file.relativePath },
      {
        onSuccess: () => {
          notifications.show({
            message: `Linked ${chapterIds.length} chapter(s) to ${file.fileName}`,
            color: 'green',
          })
          handleClose()
        },
      },
    )
  }

  return (
    <Modal opened={opened} onClose={handleClose} title={`Link ${chapterIds.length} chapter(s) to a file`} size="lg">
      <Stack gap="sm">
        <Text size="sm" c="dimmed">
          Pick the file in the series folder these chapters are actually contained in — useful for
          compilation CBZs or oddly-named releases the automatic matcher couldn't parse.
        </Text>
        {isLoading ? (
          <Group py="md" gap="xs">
            <Loader size="sm" />
            <Text size="sm" c="dimmed">
              Scanning folder…
            </Text>
          </Group>
        ) : !files || files.length === 0 ? (
          <Text c="dimmed" size="sm" py="sm">
            No files found in the series folder.
          </Text>
        ) : (
          <ScrollArea.Autosize mah={420}>
            <Stack gap={4}>
              {files.map((f) => (
                <UnstyledButton
                  key={f.relativePath}
                  onClick={() => setPicked(f.relativePath)}
                  disabled={!f.onDisk || link.isPending}
                  p="xs"
                  style={{
                    borderRadius: 8,
                    border: `1px solid ${picked === f.relativePath ? 'var(--mantine-color-brand-5)' : 'var(--border)'}`,
                    opacity: f.onDisk ? 1 : 0.5,
                  }}
                >
                  <Group justify="space-between" wrap="nowrap">
                    <Group gap="xs" wrap="nowrap" style={{ minWidth: 0 }}>
                      <IconFileZip size={16} style={{ flexShrink: 0 }} />
                      <Text size="sm" style={{ wordBreak: 'break-all' }}>
                        {f.fileName}
                      </Text>
                    </Group>
                    <Group gap={6} wrap="nowrap">
                      {f.parsedLabel && (
                        <Badge size="sm" variant="light" color={f.isVolume ? 'indigo' : 'gray'} className="tnum">
                          {f.parsedLabel}
                        </Badge>
                      )}
                      <Text size="xs" c="dimmed" className="tnum">
                        {formatBytes(f.size)}
                      </Text>
                    </Group>
                  </Group>
                </UnstyledButton>
              ))}
            </Stack>
          </ScrollArea.Autosize>
        )}
        <Group justify="flex-end" mt="xs">
          <Button variant="default" onClick={handleClose} disabled={link.isPending}>
            Cancel
          </Button>
          <Button
            disabled={!picked}
            loading={link.isPending}
            onClick={() => {
              const file = files?.find((f) => f.relativePath === picked)
              if (file) confirm(file)
            }}
          >
            Link
          </Button>
        </Group>
      </Stack>
    </Modal>
  )
}
