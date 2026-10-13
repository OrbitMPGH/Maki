import type { ReactNode } from 'react'
import { Checkbox, Stack, Text } from '@mantine/core'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { ConfirmDialog } from '../ui/ConfirmDialog'
import { useRecycleBinDays } from '../../api/recycleBin'
import { PermanentDeleteFallback } from '../PermanentDeleteFallback'

/** Taking series out of the library, with the one optional extra of deleting their files from disk. */
export function RemoveSeriesDialog({
  opened,
  onClose,
  title,
  children,
  deleteFiles,
  onDeleteFilesChange,
  onConfirm,
  onConfirmPermanent,
  loading,
}: {
  opened: boolean
  onClose: () => void
  title: ReactNode
  children: ReactNode
  deleteFiles: boolean
  onDeleteFilesChange: (value: boolean) => void
  onConfirm: () => void
  /** Set once the server said the files cannot go to the recycle bin: offers to delete them for good. */
  onConfirmPermanent?: () => void
  loading?: boolean
}) {
  const { t } = useLingui()
  const binDays = useRecycleBinDays(opened)

  return (
    <ConfirmDialog
      opened={opened}
      onClose={onClose}
      title={title}
      confirmLabel={<Trans>Remove</Trans>}
      onConfirm={onConfirm}
      loading={loading}
    >
      <Stack gap="md">
        <Text size="sm" c="var(--ink-3)">{children}</Text>
        <Checkbox
          label={t`Also delete files on disk`}
          checked={deleteFiles}
          onChange={(e) => onDeleteFilesChange(e.currentTarget.checked)}
        />
        {deleteFiles && (
          <Text size="sm" c="var(--ink-3)">
            <Plural
              value={binDays}
              one="The files go to the recycle bin for # day and can be restored from Settings until then."
              other="The files go to the recycle bin for # days and can be restored from Settings until then."
            />
          </Text>
        )}
        <Text size="sm" c="var(--danger)">
          <Trans>Removing the series from Maki cannot be undone.</Trans>
        </Text>
        {onConfirmPermanent && deleteFiles && (
          <PermanentDeleteFallback loading={loading} onConfirm={onConfirmPermanent} />
        )}
      </Stack>
    </ConfirmDialog>
  )
}
