import { Button, Group, Modal, Stack, Text } from '@mantine/core'
import { Plural, Trans } from '@lingui/react/macro'
import { useArchivePreview } from '../../api/hooks'

/**
 * Restoring a series whose files were removed: the chapters can be downloaded again or left as
 * history. Shared by the Archived view, the series page banner and the actions menu.
 */
export function RestoreSeriesDialog({
  opened,
  onClose,
  seriesId = null,
  removedCount = 0,
  onRestore,
  canDownload = true,
  loading,
}: {
  opened: boolean
  onClose: () => void
  /**
   * One series: the count comes from the server, the chapters a restore would actually queue. Null
   * for a bulk restore, which shows `removedCount` instead.
   */
  seriesId?: number | null
  /** Chapters whose files were removed, summed over the selection. Only read without `seriesId`. */
  removedCount?: number
  onRestore: (redownload: boolean) => void
  /** DownloadChapters: without it the server refuses the download, so the button is left out. */
  canDownload?: boolean
  loading?: boolean
}) {
  const { data: preview } = useArchivePreview(seriesId, opened)
  const count = seriesId === null ? removedCount : preview?.redownloadableChapters
  const nothingWanted = seriesId !== null && count === 0
  const offerDownload = canDownload && count !== 0

  return (
    <Modal
      opened={opened}
      onClose={onClose}
      title={
        nothingWanted ? (
          <Trans context="series">Restore to the library?</Trans>
        ) : count ? (
          <Plural value={count} one="Download the # removed chapter again?" other="Download the # removed chapters again?" />
        ) : (
          <Trans>Download the removed chapters again?</Trans>
        )
      }
      centered
    >
      <Stack gap="md">
        {nothingWanted && (
          <Text size="sm" c="dimmed">
            <Trans>None of the removed chapters are still wanted, so nothing would be downloaded.</Trans>
          </Text>
        )}
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose} disabled={loading}>
            <Trans>Cancel</Trans>
          </Button>
          <Button variant={offerDownload ? 'default' : 'filled'} loading={!offerDownload && loading} disabled={loading} onClick={() => onRestore(false)}>
            {nothingWanted ? <Trans context="series">Restore</Trans> : <Trans context="series">Restore only</Trans>}
          </Button>
          {offerDownload && (
            <Button loading={loading} onClick={() => onRestore(true)}>
              <Trans context="series">Restore and download</Trans>
            </Button>
          )}
        </Group>
      </Stack>
    </Modal>
  )
}
