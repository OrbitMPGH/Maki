import type { ReactNode } from 'react'
import { Stack, Text } from '@mantine/core'
import { Plural, Trans } from '@lingui/react/macro'
import { ConfirmDialog } from '../ui/ConfirmDialog'
import { useRecycleBinDays } from '../../api/recycleBin'
import { PermanentDeleteFallback } from '../PermanentDeleteFallback'

/** Removing what is left on disk of an archived series. The series, its chapters and history stay. */
export function RemoveArchivedFilesDialog({
  opened,
  onClose,
  title,
  onConfirm,
  onConfirmPermanent,
  loading,
}: {
  opened: boolean
  onClose: () => void
  title: ReactNode
  onConfirm: () => void
  /** Set once the server said the files cannot go to the recycle bin: offers to delete them for good. */
  onConfirmPermanent?: () => void
  loading?: boolean
}) {
  const binDays = useRecycleBinDays(opened)

  return (
    <ConfirmDialog
      opened={opened}
      onClose={onClose}
      title={title}
      confirmLabel={<Trans>Remove files</Trans>}
      onConfirm={onConfirm}
      loading={loading}
    >
      <Stack gap="md">
        <Text size="sm" c="var(--ink-3)">
          <Trans context="series">
            The series stays in Archived with its chapters, reading history and rating. Restoring it later
            can download the chapters again.
          </Trans>
        </Text>
        <Text size="sm" c="var(--ink-3)">
          <Plural
            value={binDays}
            one="The files go to the recycle bin for # day and can be restored from Settings until then."
            other="The files go to the recycle bin for # days and can be restored from Settings until then."
          />
        </Text>
        {onConfirmPermanent && <PermanentDeleteFallback loading={loading} onConfirm={onConfirmPermanent} />}
      </Stack>
    </ConfirmDialog>
  )
}
