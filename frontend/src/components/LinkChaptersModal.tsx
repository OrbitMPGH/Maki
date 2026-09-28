import { useMemo, useState } from 'react'
import { Badge, Button, Group, Loader, Modal, ScrollArea, Stack, Text, UnstyledButton } from '@mantine/core'
import { IconFileZip, IconLinkOff } from '@tabler/icons-react'
import { notifications } from '@mantine/notifications'
import { Trans, useLingui } from '@lingui/react/macro'
import { plural, t as now } from '@lingui/core/macro'
import { useLinkChapters, useSeriesFiles } from '../api/hooks'
import type { SeriesFileDto } from '../api/types'
import { formatBytes } from '../format'

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
  const { t } = useLingui()
  const { data: files, isLoading } = useSeriesFiles(seriesId, opened)
  const link = useLinkChapters()
  const [picked, setPicked] = useState<string | null>(null)
  // Files nothing points at yet float to the top: that is almost always the omnibus or the
  // oddly named release the user opened this dialog for.
  const sorted = useMemo(() => {
    const rank = (f: SeriesFileDto) => (!f.onDisk ? 2 : f.status === 'linked' ? 1 : 0)
    return [...(files ?? [])].sort((a, b) => rank(a) - rank(b))
  }, [files])
  const chapterCount = chapterIds.length
  const chapterPhrase = plural(chapterCount, { one: '# chapter', other: '# chapters' })

  const handleClose = () => {
    setPicked(null)
    onClose()
  }

  const confirm = (file: SeriesFileDto) => {
    const { fileName } = file
    link.mutate(
      { chapterIds, relativePath: file.relativePath },
      {
        onSuccess: () => {
          notifications.show({
            message: now`Linked ${chapterPhrase} to ${fileName}`,
            color: 'var(--ok)',
          })
          handleClose()
        },
      },
    )
  }

  return (
    <Modal opened={opened} onClose={handleClose} title={t`Link ${chapterPhrase} to a file`} size="lg">
      <Stack gap="sm">
        <Text size="sm" c="var(--ink-3)">
          <Trans>
            Pick the file in the series folder these chapters are actually contained in, useful for
            compilation CBZs or oddly-named releases the automatic matcher couldn't parse.
          </Trans>
        </Text>
        {isLoading ? (
          <Group py="md" gap="xs">
            <Loader size="sm" />
            <Text size="sm" c="var(--ink-3)">
              <Trans>Scanning folder…</Trans>
            </Text>
          </Group>
        ) : !files || files.length === 0 ? (
          <Text c="var(--ink-3)" size="sm" py="sm">
            <Trans>No files found in the series folder.</Trans>
          </Text>
        ) : (
          <ScrollArea.Autosize mah="min(420px, 45dvh)">
            <Stack gap={4}>
              {sorted.map((f) => (
                <UnstyledButton
                  key={f.relativePath}
                  onClick={() => setPicked(f.relativePath)}
                  disabled={!f.onDisk || link.isPending}
                  p="xs"
                  style={{
                    borderRadius: 'var(--radius-thumb)',
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
                      {f.onDisk && f.status !== 'linked' && (
                        <Badge size="sm" variant="light" color="var(--warn)" leftSection={<IconLinkOff size={11} />}>
                          <Trans>Not linked</Trans>
                        </Badge>
                      )}
                      {f.parsedLabel && (
                        <Badge size="sm" variant="light" color={f.isVolume ? 'indigo' : 'var(--neutral)'} className="tnum">
                          {f.parsedLabel}
                        </Badge>
                      )}
                      <Text size="xs" c="var(--ink-3)" className="tnum">
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
            <Trans>Cancel</Trans>
          </Button>
          <Button
            disabled={!picked}
            loading={link.isPending}
            onClick={() => {
              const file = files?.find((f) => f.relativePath === picked)
              if (file) confirm(file)
            }}
          >
            <Trans>Link</Trans>
          </Button>
        </Group>
      </Stack>
    </Modal>
  )
}
