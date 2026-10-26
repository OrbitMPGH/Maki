import { Button, Group, Modal } from '@mantine/core'
import { Plural, Trans } from '@lingui/react/macro'

/**
 * Restoring a series whose files were removed: the chapters can be downloaded again or left as
 * history. Shared by the Archived view, the series page banner and the actions menu.
 */
export function RestoreSeriesDialog({
  opened,
  onClose,
  removedCount,
  onRestore,
  canDownload = true,
  loading,
}: {
  opened: boolean
  onClose: () => void
  /** Chapters whose files were removed. */
  removedCount: number
  onRestore: (redownload: boolean) => void
  /** DownloadChapters: without it the server refuses the download, so the button is left out. */
  canDownload?: boolean
  loading?: boolean
}) {
  return (
    <Modal
      opened={opened}
      onClose={onClose}
      title={
        <Plural
          value={removedCount}
          one="Download the # removed chapter again?"
          other="Download the # removed chapters again?"
        />
      }
      centered
    >
      <Group justify="flex-end">
        <Button variant="default" onClick={onClose} disabled={loading}>
          <Trans>Cancel</Trans>
        </Button>
        <Button variant={canDownload ? 'default' : 'filled'} disabled={loading} onClick={() => onRestore(false)}>
          <Trans context="series">Restore only</Trans>
        </Button>
        {canDownload && (
          <Button loading={loading} onClick={() => onRestore(true)}>
            <Trans context="series">Restore and download</Trans>
          </Button>
        )}
      </Group>
    </Modal>
  )
}
