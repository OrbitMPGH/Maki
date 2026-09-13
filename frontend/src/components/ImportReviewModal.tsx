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
import { IconAlertTriangle, IconArrowRight, IconBan, IconFileZip } from '@tabler/icons-react'
import { useImportPlan, useSettleImport } from '../api/hooks'
import type { ImportDecision, ImportPlanFileDto } from '../api/types'

function formatSize(bytes: number): string {
  if (bytes <= 0) return '-'
  const mb = bytes / 1024 / 1024
  return mb >= 1024 ? `${(mb / 1024).toFixed(1)} GB` : `${Math.round(mb)} MB`
}

/** "Ch. 1, 2, 3" with a tail when the list runs long, so a 40-chapter volume stays one line. */
function chapterList(chapters: string[]): string {
  if (chapters.length === 0) return 'no chapters matched'
  const shown = chapters.slice(0, 12).join(', ')
  return chapters.length > 12 ? `Ch. ${shown} +${chapters.length - 12} more` : `Ch. ${shown}`
}

function PlanFile({ file }: { file: ImportPlanFileDto }) {
  return (
    <Card withBorder padding="sm" radius="md">
      <Group justify="space-between" wrap="nowrap" align="flex-start">
        <Group gap={8} wrap="nowrap" align="flex-start">
          <IconFileZip size={16} style={{ marginTop: 2, flexShrink: 0 }} />
          <div>
            <Text size="sm" fw={600} lineClamp={1}>
              {file.fileName}
            </Text>
            <Text size="xs" c="dimmed">
              {chapterList(file.chapters)}
            </Text>
          </div>
        </Group>
        <Group gap={6} wrap="nowrap">
          {file.label && (
            <Badge size="sm" variant="light" color="gray">
              {file.label}
            </Badge>
          )}
          <Text size="xs" c="dimmed" className="tnum">
            {formatSize(file.size)}
          </Text>
        </Group>
      </Group>

      {file.newChapters.length > 0 && (
        <Text size="xs" c="teal" mt={6}>
          Brings {file.newChapters.length} chapter{file.newChapters.length === 1 ? '' : 's'} you do not have
        </Text>
      )}

      {file.replaces.length > 0 && (
        <Stack gap={4} mt={8}>
          {file.replaces.map((existing) => (
            <Group key={existing.chapterFileId} gap={6} wrap="nowrap" c="dimmed">
              <IconArrowRight size={13} style={{ flexShrink: 0 }} />
              <Text size="xs" lineClamp={1} style={{ flex: 1 }}>
                replaces {existing.relativePath.split(/[\\/]/).pop()}
              </Text>
              <Text size="xs" className="tnum">
                {formatSize(existing.size)}
              </Text>
            </Group>
          ))}
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
  const { data: plan, isLoading } = useImportPlan(queueItemId)
  const settle = useSettleImport()

  const decide = (mode: ImportDecision) => {
    if (queueItemId === null) return
    settle.mutate({ id: queueItemId, mode }, { onSuccess: onClose })
  }

  const replacedFiles = plan?.replacedFileCount ?? 0
  const newChapters = plan?.newChapterCount ?? 0

  return (
    <Modal
      opened={queueItemId !== null}
      onClose={onClose}
      title="Review import"
      size="lg"
      radius="md"
    >
      {isLoading || !plan ? (
        <Group justify="center" py="xl">
          <Loader size="sm" />
        </Group>
      ) : plan.error ? (
        <Stack gap="md">
          <Alert color="red" icon={<IconAlertTriangle size={16} />} title="Can't read this download">
            {plan.error}
          </Alert>
          <Group justify="flex-end">
            <Button variant="default" onClick={onClose}>
              Close
            </Button>
            <Button color="red" variant="light" onClick={() => decide('Reject')} loading={settle.isPending}>
              Discard download
            </Button>
          </Group>
        </Stack>
      ) : (
        <Stack gap="md">
          <div>
            <Text size="sm" fw={600} lineClamp={2}>
              {plan.releaseName}
            </Text>
            <Text size="xs" c="dimmed">
              {plan.seriesTitle} - {plan.files.length} file{plan.files.length === 1 ? '' : 's'} downloaded,{' '}
              {replacedFiles} existing file{replacedFiles === 1 ? '' : 's'} affected, {newChapters} new chapter
              {newChapters === 1 ? '' : 's'}
            </Text>
          </div>

          <Stack gap="xs" mah={360} style={{ overflowY: 'auto' }}>
            {plan.files.map((file) => (
              <PlanFile key={file.fileName} file={file} />
            ))}
          </Stack>

          <Stack gap="xs">
            <Button
              color="red"
              onClick={() => decide('Replace')}
              loading={settle.isPending}
              leftSection={<IconAlertTriangle size={16} />}
            >
              Import everything, delete the {replacedFiles} file{replacedFiles === 1 ? '' : 's'} it replaces
            </Button>
            <Button variant="light" onClick={() => decide('SkipExisting')} loading={settle.isPending}>
              Import only what is missing, keep existing files
            </Button>
            <Button
              variant="subtle"
              color="gray"
              onClick={() => decide('Reject')}
              loading={settle.isPending}
              leftSection={<IconBan size={16} />}
            >
              Ignore this download
            </Button>
          </Stack>

          <Text size="xs" c="dimmed">
            The torrent keeps seeding whichever you pick. Deleted files are removed from disk and cannot be
            recovered from Maki.
          </Text>
        </Stack>
      )}
    </Modal>
  )
}
