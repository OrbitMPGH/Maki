import {
  Alert,
  Badge,
  Button,
  Card,
  Group,
  Loader,
  Modal,
  Stack,
  Text,
} from '@mantine/core'
import { IconAlertTriangle, IconArrowRight, IconBan, IconFileTypePdf, IconFileZip } from '@tabler/icons-react'
import { Trans, Plural, useLingui } from '@lingui/react/macro'
import { useImportPlan, useSettleImport } from '../api/hooks'
import type { ImportDecision, ImportPlanFileDto } from '../api/types'
import { isPdfFile } from '../lib/files'

function formatSize(bytes: number): string {
  if (bytes <= 0) return '-'
  const mb = bytes / 1024 / 1024
  return mb >= 1024 ? `${(mb / 1024).toFixed(1)} GB` : `${Math.round(mb)} MB`
}

/** "Ch. 1, 2, 3" with a tail when the list runs long, so a 40-chapter volume stays one line. */
function ChapterList({ chapters }: { chapters: string[] }) {
  if (chapters.length === 0) return <Trans>no chapters matched</Trans>
  const shown = chapters.slice(0, 12).join(', ')
  const extra = chapters.length - 12
  return extra > 0 ? (
    <Trans>
      Ch. {shown} +{extra} more
    </Trans>
  ) : (
    <Trans>Ch. {shown}</Trans>
  )
}

function PlanFile({ file }: { file: ImportPlanFileDto }) {
  const { fileName, chapters, label, size, newChapters, replaces } = file
  const newChapterCount = newChapters.length
  return (
    <Card withBorder padding="sm" radius="md">
      <Group justify="space-between" wrap="nowrap" align="flex-start">
        <Group gap={8} wrap="nowrap" align="flex-start">
          {isPdfFile(fileName) ? (
            <IconFileTypePdf size={16} style={{ marginTop: 2, flexShrink: 0 }} />
          ) : (
            <IconFileZip size={16} style={{ marginTop: 2, flexShrink: 0 }} />
          )}
          <div>
            <Text size="sm" fw={600} lineClamp={1}>
              {fileName}
            </Text>
            <Text size="xs" c="var(--ink-3)">
              <ChapterList chapters={chapters} />
            </Text>
          </div>
        </Group>
        <Group gap={6} wrap="nowrap">
          {label && (
            <Badge size="sm" variant="light" color="var(--neutral)">
              {label}
            </Badge>
          )}
          <Text size="xs" c="var(--ink-3)" className="tnum">
            {formatSize(size)}
          </Text>
        </Group>
      </Group>

      {newChapterCount > 0 && (
        <Text size="xs" c="var(--ok)" mt={6}>
          <Plural
            value={newChapterCount}
            one="Brings # chapter you do not have"
            other="Brings # chapters you do not have"
          />
        </Text>
      )}

      {replaces.length > 0 && (
        <Stack gap={4} mt={8}>
          {replaces.map((existing) => {
            const replacedFileName = existing.relativePath.split(/[\\/]/).pop()
            return (
              <Group key={existing.chapterFileId} gap={6} wrap="nowrap" c="var(--ink-3)">
                <IconArrowRight size={13} style={{ flexShrink: 0 }} />
                <Text size="xs" lineClamp={1} style={{ flex: 1 }}>
                  <Trans>replaces {replacedFileName}</Trans>
                </Text>
                <Text size="xs" className="tnum">
                  {formatSize(existing.size)}
                </Text>
              </Group>
            )
          })}
        </Stack>
      )}
    </Card>
  )
}

/**
 * The decision behind a download parked as "Needs review": it finished, and importing it would take
 * chapters off files already in the library. Nothing has been copied or deleted at this point, so
 * every option here is still open.
 */
export function ImportReviewModal({
  queueItemId,
  onClose,
}: {
  queueItemId: number | null
  onClose: () => void
}) {
  const { t } = useLingui()
  const { data: plan, isLoading } = useImportPlan(queueItemId)
  const settle = useSettleImport()

  const decide = (mode: ImportDecision) => {
    if (queueItemId === null) return
    settle.mutate({ id: queueItemId, mode }, { onSuccess: onClose })
  }

  const replacedFiles = plan?.replacedFileCount ?? 0
  const newChapters = plan?.newChapterCount ?? 0
  const seriesTitle = plan?.seriesTitle ?? ''
  const fileCount = plan?.files.length ?? 0

  return (
    <Modal
      opened={queueItemId !== null}
      onClose={onClose}
      title={t`Review import`}
      size="lg"
    >
      {isLoading || !plan ? (
        <Group justify="center" py="xl">
          <Loader size="sm" />
        </Group>
      ) : plan.error ? (
        <Stack gap="md">
          <Alert color="var(--danger)" icon={<IconAlertTriangle size={16} />} title={t`Can't read this download`}>
            {plan.error}
          </Alert>
          <Group justify="flex-end">
            <Button variant="default" onClick={onClose}>
              <Trans>Close</Trans>
            </Button>
            <Button color="var(--danger)" variant="light" onClick={() => decide('Reject')} loading={settle.isPending}>
              <Trans>Discard download</Trans>
            </Button>
          </Group>
        </Stack>
      ) : (
        <Stack gap="md">
          <div>
            <Text size="sm" fw={600} lineClamp={2}>
              {plan.releaseName}
            </Text>
            <Text size="xs" c="var(--ink-3)">
              <Trans>
                {seriesTitle} - <Plural value={fileCount} one="# file" other="# files" /> downloaded,{' '}
                <Plural value={replacedFiles} one="# existing file" other="# existing files" /> affected,{' '}
                <Plural value={newChapters} one="# new chapter" other="# new chapters" />
              </Trans>
            </Text>
          </div>

          <Stack gap="xs" mah="min(360px, 35dvh)" style={{ overflowY: 'auto' }}>
            {plan.files.map((file) => (
              <PlanFile key={file.fileName} file={file} />
            ))}
          </Stack>

          <Stack gap="xs">
            <Button
              color="var(--danger-fill)"
              onClick={() => decide('Replace')}
              loading={settle.isPending}
              leftSection={<IconAlertTriangle size={16} />}
            >
              <Trans>
                Import everything, delete the{' '}
                <Plural value={replacedFiles} one="# file" other="# files" /> it replaces
              </Trans>
            </Button>
            <Button variant="light" onClick={() => decide('SkipExisting')} loading={settle.isPending}>
              <Trans>Import only what is missing, keep existing files</Trans>
            </Button>
            <Button
              variant="subtle"
              color="var(--neutral)"
              onClick={() => decide('Reject')}
              loading={settle.isPending}
              leftSection={<IconBan size={16} />}
            >
              <Trans>Ignore this download</Trans>
            </Button>
          </Stack>

          <Text size="xs" c="var(--ink-3)">
            <Trans>The torrent keeps seeding whichever you pick.</Trans>{' '}
            <Trans>Deleted files are removed from disk and cannot be recovered from Maki.</Trans>
          </Text>
        </Stack>
      )}
    </Modal>
  )
}
