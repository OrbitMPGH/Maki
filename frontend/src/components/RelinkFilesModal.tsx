import { useEffect, useState } from 'react'
import {
  Badge,
  Button,
  Checkbox,
  Group,
  Loader,
  Modal,
  ScrollArea,
  Stack,
  Table,
  Text,
  Tooltip,
} from '@mantine/core'
import { IconArrowRight, IconFileZip, IconTrash } from '@tabler/icons-react'
import { notifications } from '@mantine/notifications'
import { Trans, Plural, useLingui } from '@lingui/react/macro'
import { msg, plural } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { useApplyRelink, useRelinkPlan, type RelinkPlanFile } from '../api/hooks'
import { useAuth } from '../auth/AuthProvider'
import { formatBytes } from '../format'
import { useLabel } from '../i18n-context'

const CONFIDENCE_LABELS: Record<string, MessageDescriptor> = {
  pageMarkers: msg`From page names`,
  volumeRange: msg`From volume metadata`,
  fileName: msg`From file name`,
  estimated: msg`Estimated`,
}

const CONFIDENCE_HINTS: Record<string, MessageDescriptor> = {
  pageMarkers: msg`The chapter numbers are in the archive's page file names. Certain.`,
  volumeRange: msg`The provider assigns these chapters to this volume. Usually right, compilation boundaries can differ.`,
  fileName: msg`A single-chapter file named for its chapter.`,
  estimated: msg`A proportional guess for a finished series whose volumes are all on disk. Only used when nothing better covers the chapter.`,
}

/** "1, 2, 3, 4, 5, 6" → "1-6"; keeps gaps: "1-3, 7, 9-10". */
function compactRange(labels: string[]): string {
  const nums = labels.map(Number)
  if (labels.length === 0 || nums.some((n) => Number.isNaN(n))) return labels.join(', ')
  const parts: string[] = []
  let start = nums[0]
  let prev = nums[0]
  for (let i = 1; i <= nums.length; i++) {
    const n = nums[i]
    const consecutive = i < nums.length && Number.isInteger(prev) && Number.isInteger(n) && n === prev + 1
    if (!consecutive) {
      parts.push(start === prev ? String(start) : `${start}-${prev}`)
      start = n
    }
    prev = n
  }
  return parts.join(', ')
}

function ChapterList({ labels, c }: { labels: string[]; c?: string }) {
  const text = compactRange(labels)
  return (
    <Text span size="sm" c={c} className="tnum">
      {text}
    </Text>
  )
}

/**
 * Preview and apply a volumes-first rebuild of which file backs each chapter. The server
 * recomputes the plan on apply, so this is a picture of what will happen, not the instruction.
 */
export function RelinkFilesModal({
  seriesId,
  opened,
  onClose,
}: {
  seriesId: number
  opened: boolean
  onClose: () => void
}) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const { can } = useAuth()
  const canDelete = can('DeleteSeries')
  const { data: plan, isLoading, isError } = useRelinkPlan(seriesId, opened)
  const apply = useApplyRelink(seriesId)
  const [deleteSuperseded, setDeleteSuperseded] = useState(false)

  useEffect(() => {
    if (opened) setDeleteSuperseded(false)
  }, [opened])

  const changed = plan?.files.filter((f) => f.gains.length > 0 || f.loses.length > 0 || f.superseded) ?? []
  const nothingToDo = plan !== undefined && plan.moved === 0 && plan.supersededCount === 0
  const supersededSize = formatBytes(plan?.supersededBytes ?? 0)

  const confirm = () => {
    apply.mutate(deleteSuperseded, {
      onSuccess: (r) => {
        const moved = plural(r.moved, { one: '# chapter link moved', other: '# chapter links moved' })
        const freed = formatBytes(r.freedBytes)
        const deleted = plural(r.deleted, { one: '# file deleted', other: '# files deleted' })
        notifications.show({
          message: r.deleted > 0 ? `${moved}, ${deleted} (${freed})` : moved,
          color: r.failed > 0 ? 'var(--warn)' : 'var(--ok)',
        })
        if (r.failed > 0) {
          notifications.show({
            message: plural(r.failed, {
              one: '# file could not be deleted (locked or permission denied)',
              other: '# files could not be deleted (locked or permission denied)',
            }),
            color: 'var(--warn)',
          })
        }
        onClose()
      },
    })
  }

  return (
    <Modal opened={opened} onClose={onClose} title={t`Relink files, volumes first`} size="xl">
      <Stack gap="sm">
        <Text size="sm" c="var(--ink-3)">
          <Trans>
            Rebuilds which file backs each chapter from what is in the folder. A volume file that
            contains a chapter always wins over a single-chapter file. Single files left backing
            nothing are listed as superseded so you can reclaim the space.
          </Trans>
        </Text>

        {isLoading ? (
          <Group py="md" gap="xs">
            <Loader size="sm" />
            <Text size="sm" c="var(--ink-3)">
              <Trans>Reading volume archives…</Trans>
            </Text>
          </Group>
        ) : isError || !plan ? (
          <Text size="sm" c="var(--danger)" py="sm">
            <Trans>Could not build a plan for this series.</Trans>
          </Text>
        ) : nothingToDo ? (
          <Text size="sm" c="var(--ink-3)" py="sm">
            <Trans>Every chapter is already on the best file available. Nothing to change.</Trans>
            {plan.unrecognized > 0 && (
              <>
                {' '}
                <Plural
                  value={plan.unrecognized}
                  one="# file could not be parsed and needs linking by hand."
                  other="# files could not be parsed and need linking by hand."
                />
              </>
            )}
          </Text>
        ) : (
          <>
            <Group gap="xs" wrap="wrap">
              <Badge size="lg" variant="light" className="tnum">
                <Plural value={plan.moved} one="# chapter link moves" other="# chapter links move" />
              </Badge>
              {plan.supersededCount > 0 && (
                <Badge size="lg" variant="light" color="var(--warn)" className="tnum">
                  <Plural value={plan.supersededCount} one="# file superseded" other="# files superseded" />
                  {` (${supersededSize})`}
                </Badge>
              )}
              {plan.unrecognized > 0 && (
                <Badge size="lg" variant="light" color="gray" className="tnum">
                  <Plural value={plan.unrecognized} one="# unparsed file" other="# unparsed files" />
                </Badge>
              )}
            </Group>

            <ScrollArea.Autosize mah="min(460px, 50dvh)">
              <Table className="panel-table" verticalSpacing="xs">
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th><Trans>File</Trans></Table.Th>
                    <Table.Th w={140}><Trans>Basis</Trans></Table.Th>
                    <Table.Th><Trans>Change</Trans></Table.Th>
                    <Table.Th w={90}><Trans>Size</Trans></Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {changed.map((f) => (
                    <PlanRow key={f.relativePath} file={f} renderLabel={renderLabel} />
                  ))}
                </Table.Tbody>
              </Table>
            </ScrollArea.Autosize>

            {plan.supersededCount > 0 && (
              <Checkbox
                checked={deleteSuperseded}
                disabled={!canDelete}
                onChange={(e) => setDeleteSuperseded(e.currentTarget.checked)}
                label={
                  <>
                    <Plural
                      value={plan.supersededCount}
                      one="Also delete the # superseded file from disk"
                      other="Also delete the # superseded files from disk"
                    />
                    {` (${supersededSize})`}
                  </>
                }
                description={
                  canDelete ? (
                    <Trans>Only files whose every chapter now lives on a volume. This cannot be undone.</Trans>
                  ) : (
                    <Trans>Deleting files needs the Delete series permission.</Trans>
                  )
                }
              />
            )}
          </>
        )}

        <Group justify="flex-end" mt="xs">
          <Button variant="default" onClick={onClose} disabled={apply.isPending}>
            <Trans>Cancel</Trans>
          </Button>
          <Button
            disabled={!plan || nothingToDo}
            loading={apply.isPending}
            color={deleteSuperseded ? 'var(--danger-fill)' : undefined}
            leftSection={deleteSuperseded ? <IconTrash size={16} /> : undefined}
            onClick={confirm}
          >
            {deleteSuperseded ? <Trans>Relink and delete</Trans> : <Trans>Relink</Trans>}
          </Button>
        </Group>
      </Stack>
    </Modal>
  )
}

function PlanRow({
  file,
  renderLabel,
}: {
  file: RelinkPlanFile
  renderLabel: (d: string | MessageDescriptor) => string
}) {
  const { t } = useLingui()
  const hint = file.confidence ? CONFIDENCE_HINTS[file.confidence] : undefined
  return (
    <Table.Tr opacity={file.superseded ? 0.7 : 1}>
      <Table.Td>
        <Group gap={6} wrap="nowrap">
          <IconFileZip size={15} style={{ flexShrink: 0 }} />
          <Stack gap={0} style={{ minWidth: 0 }}>
            <Text size="sm" style={{ wordBreak: 'break-all' }}>
              {file.fileName}
            </Text>
            {file.label && (
              <Text size="xs" c="var(--ink-3)" className="tnum">
                {file.label}
              </Text>
            )}
          </Stack>
        </Group>
      </Table.Td>
      <Table.Td>
        {file.superseded ? (
          <Badge size="sm" variant="light" color="var(--warn)">
            <Trans>Superseded</Trans>
          </Badge>
        ) : file.confidence ? (
          <Tooltip label={hint ? renderLabel(hint) : undefined} withArrow disabled={!hint} multiline w={280}>
            <Badge size="sm" variant="light" color={file.confidence === 'estimated' ? 'gray' : 'teal'}>
              {renderLabel(CONFIDENCE_LABELS[file.confidence] ?? file.confidence)}
            </Badge>
          </Tooltip>
        ) : (
          <Text size="sm" c="var(--ink-3)">
            -
          </Text>
        )}
      </Table.Td>
      <Table.Td>
        <Stack gap={2}>
          {file.gains.length > 0 && (
            <Group gap={4} wrap="nowrap" align="baseline">
              <IconArrowRight size={13} style={{ flexShrink: 0, color: 'var(--ok)' }} aria-label={t`Gains`} />
              <Text span size="sm" c="var(--ok)">
                <Trans>Takes ch.</Trans>{' '}
                <ChapterList labels={file.gains} />
              </Text>
            </Group>
          )}
          {file.loses.length > 0 && (
            <Group gap={4} wrap="nowrap" align="baseline">
              <IconArrowRight size={13} style={{ flexShrink: 0, color: 'var(--warn)' }} aria-label={t`Loses`} />
              <Text span size="sm" c="var(--warn)">
                <Trans>Gives up ch.</Trans> <ChapterList labels={file.loses} />
              </Text>
            </Group>
          )}
          {file.chapters.length > 0 && (
            <Text size="xs" c="var(--ink-3)">
              <Trans>After:</Trans> <ChapterList labels={file.chapters} c="var(--ink-3)" />
            </Text>
          )}
          {file.superseded && (
            <Text size="xs" c="var(--ink-3)">
              <Trans>Backs nothing after the move.</Trans>
            </Text>
          )}
        </Stack>
      </Table.Td>
      <Table.Td>
        <Text size="sm" c="var(--ink-3)" className="tnum">
          {formatBytes(file.size)}
        </Text>
      </Table.Td>
    </Table.Tr>
  )
}
