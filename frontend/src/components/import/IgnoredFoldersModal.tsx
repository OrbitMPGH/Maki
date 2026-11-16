import { useState } from 'react'
import { Button, Modal, Skeleton, Text } from '@mantine/core'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { formatDate } from '../../format'
import { useIgnoredImportFolders, useUnignoreImportFolder } from '../../api/libraryImport'
import { EmptyState } from '../ui/EmptyState'
import { ModalFooter, ModalHeading } from '../ui/ModalShell'
import { MODAL_SIZE } from '../ui/modalSizes'

/** The folders of one root that the import scan skips, each of which can be brought back. */
export function IgnoredFoldersModal({ rootFolderId, onClose }: { rootFolderId: number; onClose: () => void }) {
  const { t } = useLingui()
  const { data: ignored } = useIgnoredImportFolders(rootFolderId)
  const unignore = useUnignoreImportFolder(rootFolderId)
  const [restored, setRestored] = useState(0)
  const count = ignored?.length ?? 0

  return (
    <Modal
      opened
      onClose={onClose}
      size={MODAL_SIZE.default}
      title={
        <ModalHeading
          title={t`Ignored folders`}
          description={<Trans>The scan skips these folders without searching for them.</Trans>}
        />
      }
    >
      {!ignored && (
        <div className="modal-rows" aria-hidden>
          {[62, 44].map((w) => (
            <div key={w} className="modal-row">
              <Skeleton h={10} w={`${w}%`} />
            </div>
          ))}
        </div>
      )}
      {ignored && count === 0 && (
        <EmptyState
          compact
          mood="asleep"
          title={t`No folders are ignored in this root folder.`}
          description={restored > 0 ? <Trans>Scan again to list the folders you brought back.</Trans> : undefined}
        />
      )}
      {count > 0 && (
        <div className="modal-rows">
          {ignored?.map((f) => {
            const { folderName } = f
            const since = formatDate(f.createdAt)
            return (
              <div key={f.id} className="modal-row">
                <div className="modal-row-grow">
                  <Text size="sm" fw={600} c="var(--ink-hi)" style={{ overflowWrap: 'anywhere' }}>
                    {folderName}
                  </Text>
                  <Text size="xs" c="var(--ink-4)" className="tnum">
                    <Trans>Ignored on {since}</Trans>
                  </Text>
                </div>
                <Button
                  size="compact-sm"
                  variant="default"
                  loading={unignore.isPending && unignore.variables === f.id}
                  onClick={() => unignore.mutate(f.id, { onSuccess: () => setRestored((n) => n + 1) })}
                >
                  <Trans>Stop ignoring</Trans>
                </Button>
              </div>
            )
          })}
        </div>
      )}
      <ModalFooter
        status={
          <>
            {ignored && <Plural value={count} one="# ignored" other="# ignored" />}
            {restored > 0 && count > 0 && (
              <span style={{ color: 'var(--ok)', marginInlineStart: 12 }}>
                <Trans>Scan again to list the folders you brought back.</Trans>
              </span>
            )}
          </>
        }
      >
        <Button variant="default" onClick={onClose}>
          <Trans>Close</Trans>
        </Button>
      </ModalFooter>
    </Modal>
  )
}
