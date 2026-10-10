import { useState } from 'react'
import { Badge, Button, Card, Group, Modal, Stack, Text, Title } from '@mantine/core'
import { IconArrowBackUp, IconArrowRight } from '@tabler/icons-react'
import { notifications } from '@mantine/notifications'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'
import { formatDateTime } from '../../format'
import {
  useImportBatches,
  useUndoImport,
  type ImportBatch,
  type ImportBatchFolder,
  type ImportUndoOutcome,
} from '../../api/libraryImport'

type UndoTarget = { batchId: string; count: number } | { folderId: number; name: string }

/** Tells the user what an undo did: one toast for the folders put back, one per refusal. */
function reportOutcomes(outcomes: ImportUndoOutcome[]) {
  const undone = outcomes.filter((o) => o.undone).length
  if (undone > 0) {
    notifications.show({
      message: plural(undone, { one: 'Undid the import of # folder', other: 'Undid the import of # folders' }),
      color: 'var(--ok)',
    })
  }
  for (const o of outcomes) {
    const lines = o.undone ? (o.warnings ?? []) : [o.error ?? '']
    for (const line of lines) {
      notifications.show({ title: o.folderName, message: line, color: o.undone ? 'var(--warn)' : 'var(--danger)' })
    }
  }
}

/** Asks before an undo, says what it does and does not touch, then runs it and reports. */
function UndoConfirm({
  target,
  onClose,
  onUndone,
}: {
  target: UndoTarget | null
  onClose: () => void
  onUndone?: () => void
}) {
  const { t } = useLingui()
  const undo = useUndoImport()
  const name = target && 'name' in target ? target.name : null
  const count = target && 'count' in target ? target.count : 0
  const run = () => {
    if (!target) return
    undo.mutate('batchId' in target ? { batchId: target.batchId } : { folderId: target.folderId }, {
      onSuccess: (outcomes) => {
        reportOutcomes(outcomes)
        onUndone?.()
      },
      onSettled: onClose,
    })
  }

  return (
    <Modal
      opened={target !== null}
      onClose={onClose}
      title={
        name !== null ? (
          t`Undo the import of ${name}?`
        ) : (
          <Plural value={count} one="Undo the import of # folder?" other="Undo the import of # folders?" />
        )
      }
    >
      <Stack gap="sm">
        <Text size="sm">
          <Trans>
            Undo removes the series the import added and the files it registered, deletes the CBZs Maki
            built from other formats, and moves each folder back to its old name. Every file you had before
            the import stays where it was.
          </Trans>
        </Text>
        <Text size="xs" c="var(--ink-3)">
          <Trans>
            A series that has been downloaded into or read since cannot be undone. ComicInfo.xml changes
            Maki wrote inside your own CBZs are not reverted.
          </Trans>
        </Text>
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>
            <Trans>Cancel</Trans>
          </Button>
          <Button color="var(--danger)" loading={undo.isPending} onClick={run}>
            <Trans>Undo import</Trans>
          </Button>
        </Group>
      </Stack>
    </Modal>
  )
}

/** Undo for the run whose results are on screen. */
export function UndoBatchButton({
  batchId,
  count,
  onUndone,
}: {
  batchId: string
  count: number
  onUndone?: () => void
}) {
  const [target, setTarget] = useState<UndoTarget | null>(null)
  return (
    <>
      <Button
        size="xs"
        variant="default"
        leftSection={<IconArrowBackUp size={14} />}
        onClick={() => setTarget({ batchId, count })}
      >
        <Trans>Undo this import</Trans>
      </Button>
      <UndoConfirm target={target} onClose={() => setTarget(null)} onUndone={onUndone} />
    </>
  )
}

function FolderRow({
  folder,
  onUndo,
  busy,
}: {
  folder: ImportBatchFolder
  onUndo: () => void
  busy: boolean
}) {
  const { originalFolderName, folderName, seriesTitle } = folder
  const undoneOn = folder.undoneAt ? formatDateTime(folder.undoneAt) : null
  return (
    <Group justify="space-between" wrap="nowrap" align="flex-start">
      <div style={{ minWidth: 0 }}>
        <Group gap={6} wrap="wrap">
          <Text size="sm" fw={600} style={{ overflowWrap: 'anywhere' }}>
            {originalFolderName}
          </Text>
          {folder.folderAction !== 'keep' && (
            <>
              <IconArrowRight size={13} />
              <Text size="sm" style={{ overflowWrap: 'anywhere' }}>
                {folderName}
              </Text>
            </>
          )}
          {!folder.createdSeries && (
            <Badge size="xs" variant="light">
              <Trans>Existing series</Trans>
            </Badge>
          )}
        </Group>
        <Text size="xs" c="var(--ink-3)">
          {seriesTitle} · <Plural value={folder.fileCount} one="# file" other="# files" />
          {folder.builtCount > 0 && (
            <>
              {' · '}
              <Plural value={folder.builtCount} one="# CBZ built" other="# CBZs built" />
            </>
          )}
        </Text>
        {undoneOn && folder.diskPending ? (
          <Text size="xs" c="var(--warn)">
            <Trans>Undone {undoneOn}, but not every file could be put back. Retry once the folder is free.</Trans>
          </Text>
        ) : undoneOn ? (
          <Text size="xs" c="var(--ink-3)">
            <Trans>Undone {undoneOn}</Trans>
          </Text>
        ) : folder.seriesGone ? (
          <Text size="xs" c="var(--ink-3)">
            <Trans>The series was removed since, nothing to undo.</Trans>
          </Text>
        ) : folder.linkPending ? (
          <Text size="xs" c="var(--ink-3)">
            <Trans>Still finding sources. Undoing cancels that.</Trans>
          </Text>
        ) : null}
      </div>
      {folder.diskPending ? (
        <Button size="xs" variant="default" leftSection={<IconArrowBackUp size={14} />} onClick={onUndo} disabled={busy}>
          <Trans>Retry</Trans>
        </Button>
      ) : (
        !folder.undoneAt &&
        !folder.seriesGone && (
          <Button size="xs" variant="default" leftSection={<IconArrowBackUp size={14} />} onClick={onUndo} disabled={busy}>
            <Trans>Undo</Trans>
          </Button>
        )
      )}
    </Group>
  )
}

function BatchCard({ batch, onUndo, busy }: { batch: ImportBatch; onUndo: (t: UndoTarget) => void; busy: boolean }) {
  const when = formatDateTime(batch.createdAt)
  const open = batch.folders.filter((f) => f.diskPending || (!f.undoneAt && !f.seriesGone))
  return (
    <Card withBorder padding="sm" radius="md">
      <Group justify="space-between" mb="xs">
        <Text size="sm" fw={600}>
          <Trans>
            Imported {when}, <Plural value={batch.folders.length} one="# folder" other="# folders" />
          </Trans>
        </Text>
        {open.length > 1 && (
          <Button
            size="xs"
            variant="light"
            color="var(--danger)"
            leftSection={<IconArrowBackUp size={14} />}
            disabled={busy}
            onClick={() => onUndo({ batchId: batch.batchId, count: open.length })}
          >
            <Trans>Undo all</Trans>
          </Button>
        )}
      </Group>
      <Stack gap="xs">
        {batch.folders.map((f) => (
          <FolderRow
            key={f.id}
            folder={f}
            busy={busy}
            onUndo={() => onUndo({ folderId: f.id, name: f.originalFolderName })}
          />
        ))}
      </Stack>
    </Card>
  )
}

/**
 * Import runs from the last 30 days with an Undo per folder and per run. Undo removes what the
 * import made and moves the folder back; the user's own files stay.
 */
export function RecentImports({ rootFolderId, onUndone }: { rootFolderId: number | null; onUndone?: () => void }) {
  const { data: batches } = useImportBatches(rootFolderId)
  const [confirm, setConfirm] = useState<UndoTarget | null>(null)

  if (!batches || batches.length === 0) return null

  return (
    <Stack gap="sm" mt="xl">
      <Title order={4}>
        <Trans>Recent imports</Trans>
      </Title>
      <Text size="xs" c="var(--ink-3)">
        <Trans>Imports can be undone for 30 days.</Trans>
      </Text>
      {batches.map((b) => (
        <BatchCard key={b.batchId} batch={b} onUndo={setConfirm} busy={confirm !== null} />
      ))}
      <UndoConfirm target={confirm} onClose={() => setConfirm(null)} onUndone={onUndone} />
    </Stack>
  )
}
