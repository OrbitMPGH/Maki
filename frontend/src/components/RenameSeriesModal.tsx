import {
  Alert,
  Button,
  Code,
  Group,
  Loader,
  Modal,
  ScrollArea,
  Stack,
  Table,
  Text,
} from '@mantine/core'
import { IconAlertTriangle, IconArrowRight } from '@tabler/icons-react'
import { notifications } from '@mantine/notifications'
import { Trans, Plural, useLingui } from '@lingui/react/macro'
import { t as now } from '@lingui/core/macro'
import { useRenameSeries, useSeriesRenamePreview } from '../api/hooks'

/**
 * Applies the configured naming formats to a series already on disk. Changing a format never moves
 * a file by itself, so this dialog is the only way an existing series takes a new one — which is
 * also why it shows the full list of moves before doing anything.
 */
export function RenameSeriesModal({
  seriesId,
  opened,
  onClose,
}: {
  seriesId: number
  opened: boolean
  onClose: () => void
}) {
  const { t } = useLingui()
  const { data: plan, isLoading } = useSeriesRenamePreview(seriesId, opened)
  const rename = useRenameSeries(seriesId)

  const conflicted = (plan?.conflicts.length ?? 0) > 0
  const fileCount = plan?.files.length ?? 0
  // Kept out of the translated sentence below so the token spelling itself is never handed to a
  // translator or mistaken for a placeholder.
  const chapterLanguageToken = '{Chapter Language}'

  return (
    <Modal
      opened={opened}
      onClose={onClose}
      title={t`Rename files`}
      size="lg"
      centered
      scrollAreaComponent={ScrollArea.Autosize}
    >
      <Stack gap="md">
        {isLoading && <Loader size="sm" />}

        {plan && !plan.hasChanges && (
          <Text size="sm">
            <Trans>The folder and every file already match the current naming formats.</Trans>
          </Text>
        )}

        {conflicted && (
          <Alert color="red" icon={<IconAlertTriangle size={18} />} title={t`Two chapters want one name`}>
            <Stack gap={4}>
              {plan?.conflicts.map((c) => (
                <Text key={c} size="sm">
                  {c}
                </Text>
              ))}
              <Text size="sm">
                <Trans>
                  Add {chapterLanguageToken} to the chapter format in Settings to tell them apart.
                </Trans>
              </Text>
            </Stack>
          </Alert>
        )}

        {plan?.folderChanged && (
          <div>
            <Text fw={500} size="sm" mb={4}>
              <Trans>Folder</Trans>
            </Text>
            <Group gap="xs" wrap="nowrap">
              <Code>{plan.folderFrom}</Code>
              <IconArrowRight size={14} />
              <Code>{plan.folderTo}</Code>
            </Group>
          </div>
        )}

        {plan && plan.files.length > 0 && (
          <div>
            <Text fw={500} size="sm" mb={4}>
              <Plural value={fileCount} one="# file" other="# files" />
            </Text>
            <Table striped highlightOnHover fz="sm">
              <Table.Tbody>
                {plan.files.map((file) => (
                  <Table.Tr key={file.chapterFileId}>
                    <Table.Td>{file.from}</Table.Td>
                    <Table.Td w={20}>
                      <IconArrowRight size={14} />
                    </Table.Td>
                    <Table.Td>{file.to}</Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </div>
        )}

        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>
            <Trans>Close</Trans>
          </Button>
          <Button
            loading={rename.isPending}
            disabled={!plan?.hasChanges || conflicted}
            onClick={() =>
              rename.mutate(undefined, {
                onSuccess: (result) => {
                  for (const warning of result.warnings) {
                    notifications.show({ message: warning, color: 'yellow' })
                  }

                  notifications.show({ message: now`Renamed`, color: 'green' })
                  onClose()
                },
              })
            }
          >
            <Trans>Rename</Trans>
          </Button>
        </Group>
      </Stack>
    </Modal>
  )
}
