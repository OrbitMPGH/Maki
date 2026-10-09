import type { ReactNode } from 'react'
import { Checkbox, Stack, Text } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { ConfirmDialog } from '../ui/ConfirmDialog'

/** Taking series out of the library, with the one optional extra of deleting their files from disk. */
export function RemoveSeriesDialog({
  opened,
  onClose,
  title,
  children,
  deleteFiles,
  onDeleteFilesChange,
  onConfirm,
  loading,
}: {
  opened: boolean
  onClose: () => void
  title: ReactNode
  children: ReactNode
  deleteFiles: boolean
  onDeleteFilesChange: (value: boolean) => void
  onConfirm: () => void
  loading?: boolean
}) {
  const { t } = useLingui()

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
        <Text size="sm" c="var(--danger)">
          <Trans>This action cannot be undone.</Trans>
        </Text>
      </Stack>
    </ConfirmDialog>
  )
}
