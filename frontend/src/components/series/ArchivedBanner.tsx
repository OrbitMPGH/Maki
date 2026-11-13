import { Button, Text } from '@mantine/core'
import { Trans } from '@lingui/react/macro'
import { IconArchive, IconArchiveOff, IconTrash } from '../../lib/icons'
import { formatDate } from '../../format'
import { Callout } from '../ui/Callout'

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
    <Callout
      tone="brand"
      icon={IconArchive}
      style={{ marginTop: 'var(--mantine-spacing-lg)' }}
      actions={
        <>
          {onRestore && (
            <Button size="xs" variant="light" leftSection={<IconArchiveOff size={16} />} loading={restoring} onClick={onRestore}>
              <Trans context="series">Restore to library</Trans>
            </Button>
          )}
          {onRemoveFiles && (
            <Button size="xs" variant="subtle" color="var(--danger)" leftSection={<IconTrash size={16} />} onClick={onRemoveFiles}>
              <Trans>Remove files</Trans>
            </Button>
          )}
        </>
      }
    >
      <Text size="sm" c="var(--ink-2)">
        {archivedBy ? (
          <Trans>
            Archived on {date} by {archivedBy}. Maki is not checking sources for this series.
          </Trans>
        ) : (
          <Trans>Archived on {date}. Maki is not checking sources for this series.</Trans>
        )}
      </Text>
    </Callout>
  )
}
