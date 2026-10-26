import { Button, Group, Paper, Text } from '@mantine/core'
import { Trans } from '@lingui/react/macro'
import { IconArchive, IconArchiveOff, IconTrash } from '@tabler/icons-react'
import { formatDate } from '../../format'

/** Sits above the series page while it is archived: what that means and the two ways out of it. */
export function ArchivedBanner({
  archivedAt,
  archivedBy,
  onRestore,
  restoring,
  onRemoveFiles,
}: {
  archivedAt: string
  /** The archiver's display name, only when the page can resolve one. */
  archivedBy: string | null
  /** Omitted without ArchiveSeries, which hides the button. */
  onRestore?: () => void
  restoring?: boolean
  /** Omitted when no file is left or without DeleteSeries, which hides the button. */
  onRemoveFiles?: () => void
}) {
  const date = formatDate(archivedAt)
  return (
    <Paper
      className="series-archived-banner"
      withBorder
      p="sm"
      radius="lg"
      mt="lg"
      style={{
        borderLeft: '2px solid var(--brand)',
        background: 'color-mix(in srgb, var(--brand) 7%, var(--surface-2))',
      }}
    >
      <Group justify="space-between" wrap="wrap" gap="sm">
        <Group gap="xs" wrap="nowrap" align="flex-start" style={{ flex: 1, minWidth: 240 }}>
          <IconArchive size={18} style={{ flexShrink: 0, marginTop: 2, color: 'var(--brand-fg)' }} />
          <Text size="sm" c="var(--ink-2)">
            {archivedBy ? (
              <Trans>
                Archived on {date} by {archivedBy}. Maki is not checking sources for this series.
              </Trans>
            ) : (
              <Trans>Archived on {date}. Maki is not checking sources for this series.</Trans>
            )}
          </Text>
        </Group>
        <Group gap="xs">
          {onRestore && (
            <Button size="xs" variant="light" leftSection={<IconArchiveOff size={14} />} loading={restoring} onClick={onRestore}>
              <Trans context="series">Restore to library</Trans>
            </Button>
          )}
          {onRemoveFiles && (
            <Button size="xs" variant="subtle" leftSection={<IconTrash size={14} />} onClick={onRemoveFiles}>
              <Trans>Remove files</Trans>
            </Button>
          )}
        </Group>
      </Group>
    </Paper>
  )
}
