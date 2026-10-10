import { useState } from 'react'
import { Button, Group, Modal, Stack, Text } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { formatDate } from '../../format'
import { useIgnoredImportFolders, useUnignoreImportFolder } from '../../api/libraryImport'

/** The folders of one root that the import scan skips, each of which can be brought back. */
export function IgnoredFoldersModal({ rootFolderId, onClose }: { rootFolderId: number; onClose: () => void }) {
  const { t } = useLingui()
  const { data: ignored } = useIgnoredImportFolders(rootFolderId)
  const unignore = useUnignoreImportFolder(rootFolderId)
  const [restored, setRestored] = useState(0)

  return (
    <Modal opened onClose={onClose} title={t`Ignored folders`} size="lg">
      <Stack gap="xs">
        <Text size="sm" c="var(--ink-2)">
          <Trans>The scan skips these folders without searching for them.</Trans>
        </Text>
        {restored > 0 && (
          <Text size="sm" c="var(--ok)">
            <Trans>Scan again to list the folders you brought back.</Trans>
          </Text>
        )}
        {(ignored ?? []).length === 0 && (
          <Text size="sm" c="var(--ink-3)">
            <Trans>No folders are ignored in this root folder.</Trans>
          </Text>
        )}
        {(ignored ?? []).map((f) => {
          const { folderName } = f
          const since = formatDate(f.createdAt)
          return (
            <Group key={f.id} justify="space-between" wrap="nowrap">
              <div style={{ minWidth: 0 }}>
                <Text size="sm" fw={600} style={{ overflowWrap: 'anywhere' }}>
                  {folderName}
                </Text>
                <Text size="xs" c="var(--ink-3)">
                  <Trans>Ignored on {since}</Trans>
                </Text>
              </div>
              <Button
                size="xs"
                variant="default"
                loading={unignore.isPending && unignore.variables === f.id}
                onClick={() => unignore.mutate(f.id, { onSuccess: () => setRestored((n) => n + 1) })}
              >
                <Trans>Stop ignoring</Trans>
              </Button>
            </Group>
          )
        })}
      </Stack>
    </Modal>
  )
}
