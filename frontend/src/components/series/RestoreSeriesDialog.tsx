import { useState } from 'react'
import { Button, Modal, Radio, Stack } from '@mantine/core'
import { Plural, Trans } from '@lingui/react/macro'
import { useArchivePreview } from '../../api/hooks'
import { ModalFooter, ModalHeading, ModalNote } from '../ui/ModalShell'

type Choice = 'download' | 'only'

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
  const [choice, setChoice] = useState<Choice>('download')
  const download = offerDownload && choice === 'download'

  return (
    <Modal
      opened={opened}
      onClose={onClose}
      centered
      title={
        <ModalHeading
          title={<Trans context="series">Restore to the library?</Trans>}
          description={<Trans>The series comes back to your library.</Trans>}
        />
      }
    >
      <Stack gap="md">
        {nothingWanted && (
          <ModalNote tone="info">
            <Trans>None of the removed chapters are still wanted, so nothing would be downloaded.</Trans>
          </ModalNote>
        )}
        {offerDownload && (
          <Radio.Group
            value={choice}
            onChange={(next) => setChoice(next as Choice)}
            label={
              count ? (
                <Plural value={count} one="Download the # removed chapter again?" other="Download the # removed chapters again?" />
              ) : (
                <Trans>Download the removed chapters again?</Trans>
              )
            }
          >
            <div className="modal-choice-list">
              <Radio
                value="download"
                label={<Trans context="series">Restore and download</Trans>}
                description={<Trans>Queues the removed chapters that are still wanted.</Trans>}
              />
              <Radio
                value="only"
                label={<Trans context="series">Restore only</Trans>}
                description={<Trans>Keeps the series as it is. Nothing is downloaded.</Trans>}
              />
            </div>
          </Radio.Group>
        )}
      </Stack>

      <ModalFooter
        status={
          count ? <Plural value={count} one="# chapter wanted" other="# chapters wanted" /> : null
        }
      >
        <Button variant="default" onClick={onClose} disabled={loading}>
          <Trans>Cancel</Trans>
        </Button>
        <Button loading={loading} onClick={() => onRestore(download)}>
          {download ? (
            <Trans context="series">Restore and download</Trans>
          ) : nothingWanted ? (
            <Trans context="series">Restore</Trans>
          ) : (
            <Trans context="series">Restore only</Trans>
          )}
        </Button>
      </ModalFooter>
    </Modal>
  )
}
