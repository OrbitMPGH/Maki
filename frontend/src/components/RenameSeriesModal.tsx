import { Anchor, Button, Modal, Skeleton, Stack, Text } from '@mantine/core'
import { Link } from 'react-router-dom'
import { IconArrowRight } from '../lib/icons'
import { notifications } from '../lib/toast'
import { Trans, Plural, useLingui } from '@lingui/react/macro'
import { t as now } from '@lingui/core/macro'
import { useRenameSeries, useSeriesRenamePreview } from '../api/hooks'
import { ApiError } from '../api/client'
import { ModalFooter, ModalHeading, ModalNote } from './ui/ModalShell'
import { MODAL_SIZE } from './ui/modalSizes'

const NAMING_SETTINGS = '/settings?tab=library&s=naming'

/** Splits `to` around the part that differs from `from`, by common prefix and suffix. */
function changedPart(from: string, to: string): [string, string, string] {
  const a = Array.from(from)
  const b = Array.from(to)
  let start = 0
  while (start < a.length && start < b.length && a[start] === b[start]) start++
  let end = 0
  while (end < a.length - start && end < b.length - start && a[a.length - 1 - end] === b[b.length - 1 - end]) end++
  return [b.slice(0, start).join(''), b.slice(start, b.length - end).join(''), b.slice(b.length - end).join('')]
}

function RenameRow({ from, to }: { from: string; to: string }) {
  const [head, change, tail] = changedPart(from, to)
  return (
    <div className="rename-row">
      <span className="rename-from">{from}</span>
      <IconArrowRight size={16} className="rename-arrow" aria-hidden />
      <span className="rename-to">
        {head}
        {change && <mark className="rename-change">{change}</mark>}
        {tail}
      </span>
    </div>
  )
}

/**
 * Applies the configured naming formats to a series already on disk. Changing a format never moves
 * a file by itself, so this dialog is the only way an existing series takes a new one, which is
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
  const { data: plan, isLoading, refetch } = useSeriesRenamePreview(seriesId, opened)
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
      title={
        <ModalHeading
          title={t`Rename files`}
          description={<Trans>Matches your naming formats. Only the highlighted parts change.</Trans>}
        />
      }
      size={MODAL_SIZE.default}
    >
      <Stack gap="md">
        {isLoading && (
          <Stack gap="xs" aria-hidden>
            {[0, 1, 2, 3].map((i) => (
              <Skeleton key={i} h={12} w={i % 2 ? '70%' : '85%'} />
            ))}
          </Stack>
        )}

        {plan && !plan.hasChanges && (
          <Text size="sm">
            <Trans>The folder and every file already match the current naming formats.</Trans>
          </Text>
        )}

        {conflicted && (
          <ModalNote>
            <Text size="sm" fw={600} c="var(--ink-hi)">
              <Trans>Two chapters want one name</Trans>
            </Text>
            {plan?.conflicts.map((c) => (
              <Text key={c} size="sm">
                {c}
              </Text>
            ))}
            <Text size="sm" c="var(--ink-3)">
              <Trans>
                Add {chapterLanguageToken} to the chapter format to tell them apart.
              </Trans>{' '}
              <Anchor component={Link} to={NAMING_SETTINGS} size="sm">
                <Trans>Open naming formats</Trans>
              </Anchor>
            </Text>
          </ModalNote>
        )}

        {plan?.folderChanged && (
          <div>
            <Text fw={600} size="sm" mb={4}>
              <Trans>Folder</Trans>
            </Text>
            <RenameRow from={plan.folderFrom} to={plan.folderTo} />
          </div>
        )}

        {plan && plan.files.length > 0 && (
          <div>
            <Text fw={600} size="sm" mb={4}>
              <Trans>Files</Trans>
            </Text>
            <div className="rename-list">
              {plan.files.map((file) => (
                <RenameRow key={file.chapterFileId} from={file.from} to={file.to} />
              ))}
            </div>
          </div>
        )}
      </Stack>

      <ModalFooter
        status={
          plan?.hasChanges && !conflicted ? (
            fileCount > 0 ? (
              <Plural value={fileCount} one="# file will be renamed" other="# files will be renamed" />
            ) : (
              <Trans>The folder will be renamed</Trans>
            )
          ) : null
        }
      >
        <Button variant="default" onClick={onClose}>
          {plan?.hasChanges ? <Trans>Cancel</Trans> : <Trans>Close</Trans>}
        </Button>
        {plan?.hasChanges && (
          <Button
            loading={rename.isPending}
            disabled={conflicted}
            onClick={() =>
              rename.mutate(plan.fingerprint, {
                onError: (error) => {
                  // The plan moved on since this preview; show the current one before anything runs.
                  if (error instanceof ApiError && error.status === 409) void refetch()
                },
                onSuccess: (result) => {
                  for (const warning of result.warnings) {
                    notifications.show({ message: warning, color: 'var(--warn)' })
                  }

                  notifications.show({ message: now`Renamed`, color: 'var(--ok)' })
                  onClose()
                },
              })
            }
          >
            <Trans>Rename files</Trans>
          </Button>
        )}
      </ModalFooter>
    </Modal>
  )
}
