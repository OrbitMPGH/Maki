import { Checkbox, Stack, Text } from '@mantine/core'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { ConfirmDialog } from '../ui/ConfirmDialog'
import { useRecycleBinDays } from '../../api/recycleBin'
import { formatBytes } from '../../format'
import { PermanentDeleteFallback } from '../PermanentDeleteFallback'

/** Moving a series out of the library, with the one optional extra of removing its files from disk. */
export function ArchiveSeriesDialog({
  opened,
  onClose,
  title,
  sizeBytes,
  otherReaders,
  removeFiles,
  onRemoveFilesChange,
  canRemoveFiles = true,
  onConfirm,
  onConfirmPermanent,
  loading,
}: {
  opened: boolean
  onClose: () => void
  title: string
  /** What removing the files would free, hardlinked files left out. Zero hides the figure from the label. */
  sizeBytes: number
  /** Other users with unfinished progress in the last 30 days. */
  otherReaders: number
  removeFiles: boolean
  onRemoveFilesChange: (value: boolean) => void
  /** DeleteSeries: removing the files is a delete, so the box stays off for anyone without it. */
  canRemoveFiles?: boolean
  onConfirm: () => void
  /** Set once the server said the files cannot go to the recycle bin: offers to delete them for good. */
  onConfirmPermanent?: () => void
  loading?: boolean
}) {
  const { t } = useLingui()
  const binDays = useRecycleBinDays(opened && removeFiles)
  const size = formatBytes(sizeBytes)

  return (
    <ConfirmDialog
      opened={opened}
      onClose={onClose}
      title={t({ message: `Archive ${title}?`, context: 'series' })}
      confirmLabel={<Trans context="series">Archive</Trans>}
      onConfirm={onConfirm}
      loading={loading}
    >
      <Stack gap="md">
        <Text size="sm" c="var(--ink-3)">
          <Trans context="series">
            Maki stops checking sources for new chapters and moves it to Archived. Your reading history,
            rating and chapters stay. You can restore it any time.
          </Trans>
        </Text>
        <Checkbox
          label={sizeBytes > 0 ? t`Also remove the files to free ${size}` : t`Also remove the files`}
          checked={removeFiles && canRemoveFiles}
          disabled={!canRemoveFiles}
          onChange={(e) => onRemoveFilesChange(e.currentTarget.checked)}
        />
        {removeFiles && canRemoveFiles && (
          <Text size="sm" c="var(--ink-3)">
            <Plural
              value={binDays}
              one="The files go to the recycle bin for # day and can be restored from Settings until then."
              other="The files go to the recycle bin for # days and can be restored from Settings until then."
            />
          </Text>
        )}
        {otherReaders > 0 && (
          <Text size="sm" c="var(--warn)">
            <Plural
              value={otherReaders}
              one="# other reader is partway through this series. Archiving stops new chapters for everyone."
              other="# other readers are partway through this series. Archiving stops new chapters for everyone."
            />
          </Text>
        )}
        {onConfirmPermanent && removeFiles && canRemoveFiles && (
          <PermanentDeleteFallback loading={loading} onConfirm={onConfirmPermanent} />
        )}
      </Stack>
    </ConfirmDialog>
  )
}
