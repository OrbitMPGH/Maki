import { useEffect, useMemo, useState } from 'react'
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
  UnstyledButton,
} from '@mantine/core'
import { IconArrowRight, IconFileZip, IconPin, IconPinnedOff, IconTrash } from '@tabler/icons-react'
import { notifications } from '@mantine/notifications'
import { Trans, Plural, useLingui } from '@lingui/react/macro'
import { msg, plural } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import {
  useApplyRelink,
  useRelinkPlan,
  type RelinkChapterRef,
  type RelinkOptions,
  type RelinkPlanFile,
} from '../api/hooks'
import { useAuth } from '../auth/AuthProvider'
import { formatBytes } from '../format'
import { useLabel } from '../i18n-context'

const CONFIDENCE_LABELS: Record<string, MessageDescriptor> = {
  pageMarkers: msg`From page names`,
  volumeRange: msg`From volume metadata`,
  fileName: msg`From file name`,
  estimated: msg`Estimated`,
  existing: msg`Kept as linked`,
}

const CONFIDENCE_HINTS: Record<string, MessageDescriptor> = {
  pageMarkers: msg`The chapter numbers are in the archive's page file names. Certain.`,
  volumeRange: msg`The provider assigns these chapters to this volume. Usually right, compilation boundaries can differ.`,
  fileName: msg`A single-chapter file named for its chapter.`,
  estimated: msg`A proportional guess for a finished series whose volumes are all on disk. Only used when nothing better covers the chapter.`,
  existing: msg`Nothing in the file explains these chapters, so the link you have today is trusted.`,
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

/**
 * Preview and apply a volumes-first rebuild of which file backs each chapter. The plan is a
 * dry run: untick a file to leave it exactly as it is, click a chapter to pin it where it sits
 * today. Every change re-plans on the server, so knock-on effects (a volume you exclude means
 * its single files are no longer superseded) show before anything is applied. The server
 * recomputes the plan on apply with the same exclusions, so what you see is what happens.
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
  const [excluded, setExcluded] = useState<Set<string>>(new Set())
  const [pinned, setPinned] = useState<Set<number>>(new Set())
  const [deleteSuperseded, setDeleteSuperseded] = useState(false)

  const options = useMemo<RelinkOptions>(
    () => ({ excludedPaths: [...excluded].sort(), pinnedChapterIds: [...pinned].sort((a, b) => a - b) }),
    [excluded, pinned],
  )
  const { data: plan, isLoading, isFetching, isError } = useRelinkPlan(seriesId, options, opened)
  const apply = useApplyRelink(seriesId)

  useEffect(() => {
    if (opened) {
      setExcluded(new Set())
      setPinned(new Set())
      setDeleteSuperseded(false)
    }
  }, [opened])

  const toggleExcluded = (path: string) =>
    setExcluded((prev) => {
      const next = new Set(prev)
      if (next.has(path)) next.delete(path)
      else next.add(path)
      return next
    })
  const togglePinned = (id: number) =>
    setPinned((prev) => {
      const next = new Set(prev)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })

  // Excluded files stay listed even once they have nothing to change, or there would be no way
  // to tick them back on.
  const rows = plan?.files.filter((f) => f.gains.length > 0 || f.loses.length > 0 || f.superseded || f.excluded) ?? []
  const nothingToDo = plan !== undefined && plan.moved === 0 && plan.supersededCount === 0
  const supersededSize = formatBytes(plan?.supersededBytes ?? 0)
  const held = excluded.size + pinned.size

  const confirm = () => {
    apply.mutate(
      { ...options, deleteSuperseded },
      {
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
      },
    )
  }

  return (
    <Modal opened={opened} onClose={onClose} title={t`Relink files, volumes first`} size="xl">
      <Stack gap="sm">
        <Text size="sm" c="var(--ink-3)">
          <Trans>
            A dry run of rebuilding which file backs each chapter. A volume that contains a chapter
            wins over a single-chapter file, and links the planner can't explain are kept as they
            are. Untick a file to leave it untouched, click a chapter to pin it where it is. Nothing
            changes until you press Relink.
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
        ) : nothingToDo && rows.length === 0 ? (
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
            <Group gap="xs" wrap="wrap" align="center">
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
              {held > 0 && (
                <Badge size="lg" variant="outline" color="gray" className="tnum" leftSection={<IconPin size={12} />}>
                  <Plural value={held} one="# held back" other="# held back" />
                </Badge>
              )}
              {isFetching && <Loader size="xs" />}
            </Group>

            <ScrollArea.Autosize mah="min(460px, 50dvh)">
              <Table className="panel-table" verticalSpacing="xs" style={{ opacity: isFetching ? 0.6 : 1 }}>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th w={36} />
                    <Table.Th><Trans>File</Trans></Table.Th>
                    <Table.Th w={140}><Trans>Basis</Trans></Table.Th>
                    <Table.Th><Trans>Change</Trans></Table.Th>
                    <Table.Th w={90}><Trans>Size</Trans></Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {rows.map((f) => (
                    <PlanRow
                      key={f.relativePath}
                      file={f}
                      pinned={pinned}
                      onToggleExcluded={() => toggleExcluded(f.relativePath)}
                      onTogglePinned={togglePinned}
                      renderLabel={renderLabel}
                    />
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
            disabled={!plan || nothingToDo || isFetching}
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

function ChapterChips({
  refs,
  pinned,
  color,
  onToggle,
}: {
  refs: RelinkChapterRef[]
  pinned: Set<number>
  color: string
  onToggle: (id: number) => void
}) {
  const { t } = useLingui()
  return (
    <Group gap={3} wrap="wrap" style={{ rowGap: 3 }}>
      {refs.map((c) => {
        const isPinned = pinned.has(c.id)
        const { label } = c
        return (
          <Tooltip key={c.id} label={isPinned ? t`Pinned. Click to let it move` : t`Click to keep ch. ${label} where it is`} withArrow>
            <UnstyledButton
              onClick={() => onToggle(c.id)}
              aria-pressed={isPinned}
              aria-label={t`Pin chapter ${label}`}
              className="tnum"
              style={{
                fontSize: 'var(--mantine-font-size-xs)',
                lineHeight: 1.4,
                padding: '0 6px',
                borderRadius: 'var(--radius-thumb)',
                border: `1px solid ${isPinned ? 'var(--border-strong)' : 'transparent'}`,
                background: isPinned ? 'var(--surface-sunken)' : `color-mix(in srgb, ${color} 14%, transparent)`,
                color: isPinned ? 'var(--ink-3)' : color,
                textDecoration: isPinned ? 'line-through' : undefined,
              }}
            >
              {c.label}
            </UnstyledButton>
          </Tooltip>
        )
      })}
    </Group>
  )
}

function PlanRow({
  file,
  pinned,
  onToggleExcluded,
  onTogglePinned,
  renderLabel,
}: {
  file: RelinkPlanFile
  pinned: Set<number>
  onToggleExcluded: () => void
  onTogglePinned: (id: number) => void
  renderLabel: (d: string | MessageDescriptor) => string
}) {
  const { t } = useLingui()
  const { fileName } = file
  const hint = file.confidence ? CONFIDENCE_HINTS[file.confidence] : undefined
  const dimmed = file.excluded || file.superseded
  return (
    <Table.Tr opacity={dimmed ? 0.65 : 1}>
      <Table.Td>
        <Tooltip label={file.excluded ? t`Excluded, click to include` : t`Included, click to leave this file untouched`} withArrow>
          <Checkbox
            size="xs"
            checked={!file.excluded}
            onChange={onToggleExcluded}
            aria-label={t`Include ${fileName} in the relink`}
          />
        </Tooltip>
      </Table.Td>
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
        {file.excluded ? (
          <Badge size="sm" variant="outline" color="gray" leftSection={<IconPinnedOff size={11} />}>
            <Trans>Untouched</Trans>
          </Badge>
        ) : file.superseded ? (
          <Badge size="sm" variant="light" color="var(--warn)">
            <Trans>Superseded</Trans>
          </Badge>
        ) : file.confidence ? (
          <Tooltip label={hint ? renderLabel(hint) : undefined} withArrow disabled={!hint} multiline w={280}>
            <Badge
              size="sm"
              variant="light"
              color={file.confidence === 'estimated' ? 'gray' : file.confidence === 'existing' ? 'indigo' : 'teal'}
            >
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
        <Stack gap={4}>
          {file.excluded ? (
            <Text size="xs" c="var(--ink-3)">
              <Trans>Keeps exactly what it has today.</Trans>
            </Text>
          ) : (
            <>
              {file.gains.length > 0 && (
                <Group gap={4} wrap="nowrap" align="flex-start">
                  <IconArrowRight size={13} style={{ flexShrink: 0, color: 'var(--ok)', marginTop: 2 }} />
                  <Stack gap={2}>
                    <Text size="xs" c="var(--ok)">
                      <Trans>Takes</Trans>
                    </Text>
                    <ChapterChips refs={file.gains} pinned={pinned} color="var(--ok)" onToggle={onTogglePinned} />
                  </Stack>
                </Group>
              )}
              {file.loses.length > 0 && (
                <Group gap={4} wrap="nowrap" align="flex-start">
                  <IconArrowRight size={13} style={{ flexShrink: 0, color: 'var(--warn)', marginTop: 2 }} />
                  <Stack gap={2}>
                    <Text size="xs" c="var(--warn)">
                      <Trans>Gives up</Trans>
                    </Text>
                    <ChapterChips refs={file.loses} pinned={pinned} color="var(--warn)" onToggle={onTogglePinned} />
                  </Stack>
                </Group>
              )}
              {file.chapters.length > 0 && (
                <Text size="xs" c="var(--ink-3)">
                  <Trans>After:</Trans>{' '}
                  <Text span size="xs" className="tnum">
                    {compactRange(file.chapters)}
                  </Text>
                </Text>
              )}
              {file.superseded && (
                <Text size="xs" c="var(--ink-3)">
                  <Trans>Backs nothing after the move.</Trans>
                </Text>
              )}
            </>
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
