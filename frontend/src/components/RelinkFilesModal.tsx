import { useEffect, useMemo, useRef, useState } from 'react'
import { Badge, Button, Checkbox, Group, Loader, Modal, Radio, ScrollArea, Stack, Text, Tooltip } from '@mantine/core'
import { IconTrash } from '@tabler/icons-react'
import { notifications } from '@mantine/notifications'
import { Trans, Plural, useLingui } from '@lingui/react/macro'
import { msg, plural } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import {
  useApplyRelink,
  useRelinkPlan,
  type RelinkOptions,
  type RelinkPlanChapter,
  type RelinkPlanFile,
} from '../api/hooks'
import { useAuth } from '../auth/AuthProvider'
import { formatBytes } from '../format'
import { useLabel } from '../i18n-context'

const BASIS_LABELS: Record<string, MessageDescriptor> = {
  pageMarkers: msg`From its pages`,
  volumeRange: msg`From metadata`,
  fileName: msg`From its name`,
  estimated: msg`Estimated`,
  existing: msg`Kept as linked`,
}

const BASIS_HINTS: Record<string, MessageDescriptor> = {
  pageMarkers: msg`The chapter numbers are in the archive's page file names. Certain.`,
  volumeRange: msg`The provider assigns these chapters to this volume. Usually right, compilation boundaries can differ.`,
  fileName: msg`The file is named for its chapter.`,
  estimated: msg`A proportional guess for a finished series whose volumes are all on disk. Only used when nothing better covers the chapter.`,
  existing: msg`Nothing in the file explains these chapters, so the link you have today is trusted.`,
}

const CELL_STATE_LABELS: Record<string, MessageDescriptor> = {
  movesToVolume: msg`moves onto a volume`,
  becomesReadable: msg`becomes readable`,
  unchanged: msg`unchanged`,
  kept: msg`kept where it is`,
  availableNotLinked: msg`file on disk, not linked`,
  missing: msg`missing`,
}

const CELL_CLASS: Record<string, string> = {
  movesToVolume: 'is-move',
  becomesReadable: 'is-fill',
  unchanged: 'is-static',
  kept: 'is-pin',
  availableNotLinked: 'is-avail is-static',
  missing: 'is-off',
}

interface Row {
  key: string
  kind: 'volume' | 'fill'
  paths: string[]
  /** File name for a lone file, else null and the row names its count. */
  fileName: string | null
  fileCount: number
  /** Gained chapter labels, in chapter order. */
  labels: string[]
  basis: string | null
  excluded: boolean
}

/** "0" → "Ch. 0"; a run of integers → "Ch. 0 to 8"; anything else lists them. */
function useRangeText(labels: string[]): string {
  const { t } = useLingui()
  if (labels.length === 0) return ''
  const first = labels[0]
  const last = labels[labels.length - 1]
  if (labels.length === 1) return t`Ch. ${first}`
  const nums = labels.map(Number)
  const run =
    nums.every((n, i) => Number.isInteger(n) && (i === 0 || n === nums[i - 1] + 1))
  const list = labels.join(', ')
  return run ? t`Ch. ${first} to ${last}` : t`Ch. ${list}`
}

/**
 * Turns the plan's files into the rows the dialog offers: volumes that take chapters, and
 * single files that fill chapters marked missing, the latter collapsed into runs of consecutive
 * chapters so twelve files are one line and one checkbox.
 */
function buildRows(files: RelinkPlanFile[], chapters: RelinkPlanChapter[]): Row[] {
  const numberByLabel = new Map(chapters.map((c) => [c.label, c.number]))
  const rows: Row[] = []
  const gaining = files.filter((f) => f.gains.length > 0 && !f.excluded)

  for (const f of gaining.filter((f) => f.isVolume)) {
    rows.push({
      key: f.relativePath,
      kind: 'volume',
      paths: [f.relativePath],
      fileName: f.fileName,
      fileCount: 1,
      labels: f.gains.map((g) => g.label),
      basis: f.confidence,
      excluded: false,
    })
  }

  const singles = gaining
    .filter((f) => !f.isVolume && f.recognized)
    .map((f) => ({ f, n: numberByLabel.get(f.gains[0].label) ?? null }))
    .sort((a, b) => (a.n ?? Infinity) - (b.n ?? Infinity))
  let run: { files: RelinkPlanFile[]; last: number | null; basis: string | null } | null = null
  const flush = () => {
    if (!run) return
    rows.push({
      key: run.files[0].relativePath,
      kind: 'fill',
      paths: run.files.map((x) => x.relativePath),
      fileName: run.files.length === 1 ? run.files[0].fileName : null,
      fileCount: run.files.length,
      labels: run.files.flatMap((x) => x.gains.map((g) => g.label)),
      basis: run.basis,
      excluded: false,
    })
    run = null
  }
  for (const { f, n } of singles) {
    const contiguous =
      run !== null &&
      n !== null &&
      run.last !== null &&
      Number.isInteger(n) &&
      n === run.last + 1 &&
      f.gains.length === 1 &&
      run.basis === f.confidence
    if (contiguous && run) {
      run.files.push(f)
      run.last = n
    } else {
      flush()
      run = { files: [f], last: f.gains.length === 1 ? n : null, basis: f.confidence }
    }
  }
  flush()
  return rows
}

/**
 * The relink dialog: pick which files to trust, watch the chapter map answer, then relink.
 * Every toggle re-plans on the server with the same exclusions the apply call will carry, so
 * the map is the plan, not a picture of it.
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
  const [leftover, setLeftover] = useState<'keep' | 'delete'>('keep')
  // An excluded file gains nothing in the next plan, so its row would vanish and could never
  // be ticked back on. Remember each row as it was when it was last offered.
  const rowMemory = useRef(new Map<string, Row>())

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
      setLeftover('keep')
      rowMemory.current.clear()
    }
  }, [opened])

  const rows = useMemo(() => {
    if (!plan) return []
    const live = buildRows(plan.files ?? [], plan.chapters ?? [])
    for (const row of live) rowMemory.current.set(row.key, row)
    const excludedRows = [...rowMemory.current.values()]
      .filter((row) => row.paths.every((p) => excluded.has(p)))
      .map((row) => ({ ...row, excluded: true }))
    const all = [...live, ...excludedRows]
    const first = (row: Row) => {
      const n = Number(row.labels[0])
      return Number.isNaN(n) ? Infinity : n
    }
    return all.sort((a, b) => first(a) - first(b))
  }, [plan, excluded])

  const volumeRows = rows.filter((r) => r.kind === 'volume')
  const fillRows = rows.filter((r) => r.kind === 'fill')

  const setRows = (targets: Row[], off: boolean) =>
    setExcluded((prev) => {
      const next = new Set(prev)
      for (const row of targets) {
        for (const p of row.paths) {
          if (off) next.add(p)
          else next.delete(p)
        }
      }
      return next
    })
  const togglePinned = (id: number) =>
    setPinned((prev) => {
      const next = new Set(prev)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })

  const counts = useMemo(() => {
    const c = { moves: 0, fills: 0, kept: 0 }
    for (const ch of plan?.chapters ?? []) {
      if (ch.state === 'movesToVolume') c.moves++
      else if (ch.state === 'becomesReadable') c.fills++
      else if (ch.state === 'kept') c.kept++
    }
    return c
  }, [plan])
  const total = counts.moves + counts.fills
  const emptied = plan?.supersededCount ?? 0
  const emptiedSize = formatBytes(plan?.supersededBytes ?? 0)
  const completeVolumes =
    plan?.files.filter((f) => f.isVolume && f.gains.length === 0 && f.loses.length === 0 && f.chapters.length > 0 && !f.excluded).length ?? 0
  const unmatched = plan?.unrecognized ?? 0
  const showNumbers = (plan?.chapters.length ?? 0) <= 40
  const deleting = leftover === 'delete' && emptied > 0
  const nothingToDo = plan !== undefined && rows.length === 0 && total === 0 && emptied === 0

  const confirm = () => {
    apply.mutate(
      {
        ...options,
        deleteSuperseded: deleting,
        confirmedSuperseded: plan?.files.filter((f) => f.superseded).map((f) => f.relativePath) ?? [],
      },
      {
        onSuccess: (r) => {
          const moved = plural(r.moved, { one: '# chapter relinked', other: '# chapters relinked' })
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
    <Modal opened={opened} onClose={onClose} title={t`Relink files`} size={720}>
      <Stack gap="md">
        {isLoading ? (
          <Group py="md" gap="xs">
            <Loader size="sm" />
            <Text size="sm" c="var(--ink-3)">
              <Trans>Reading the folder and every volume archive…</Trans>
            </Text>
          </Group>
        ) : isError || !plan ? (
          <Text size="sm" c="var(--danger)" py="sm">
            <Trans>Could not build a plan for this series.</Trans>
          </Text>
        ) : nothingToDo ? (
          <Text size="sm" c="var(--ink-3)" py="sm">
            <Trans>Every chapter is already on the best file available. Nothing to change.</Trans>
            {unmatched > 0 && (
              <>
                {' '}
                <Plural
                  value={unmatched}
                  one="# file could not be matched to a chapter. Link it by hand on the Files tab."
                  other="# files could not be matched to a chapter. Link them by hand on the Files tab."
                />
              </>
            )}
          </Text>
        ) : (
          <>
            <Text size="sm" c="var(--ink-3)">
              <Trans>
                Maki checked the folder against the chapter list. Tick what to trust and watch the map.
                Nothing changes until you press Relink.
              </Trans>
            </Text>

            <Stack gap={6}>
              {rows.length > 0 && (
              <div className="relink-groups">
                {volumeRows.length > 0 && (
                  <>
                    <div className="relink-section">
                      <Text size="xs" fw={600} c="var(--ink-4)" tt="uppercase" style={{ letterSpacing: '0.06em' }}>
                        <Trans>Volumes that hold chapters still read from single files</Trans>
                      </Text>
                      <AllNone onAll={() => setRows(volumeRows, false)} onNone={() => setRows(volumeRows, true)} />
                    </div>
                    {volumeRows.map((row) => (
                      <PlanRow key={row.key} row={row} onToggle={() => setRows([row], !row.excluded)} renderLabel={renderLabel} />
                    ))}
                  </>
                )}
                {fillRows.length > 0 && (
                  <>
                    <div className="relink-section">
                      <Text size="xs" fw={600} c="var(--ink-4)" tt="uppercase" style={{ letterSpacing: '0.06em' }}>
                        <Trans>Files that hold chapters marked missing</Trans>
                      </Text>
                      <AllNone onAll={() => setRows(fillRows, false)} onNone={() => setRows(fillRows, true)} />
                    </div>
                    {fillRows.map((row) => (
                      <PlanRow key={row.key} row={row} onToggle={() => setRows([row], !row.excluded)} renderLabel={renderLabel} />
                    ))}
                  </>
                )}
              </div>
              )}
              {(completeVolumes > 0 || unmatched > 0) && (
                <Text size="xs" c="var(--ink-4)">
                  <Trans>Not listed:</Trans>{' '}
                  {completeVolumes > 0 && (
                    <Plural value={completeVolumes} one="# volume already complete" other="# volumes already complete" />
                  )}
                  {completeVolumes > 0 && unmatched > 0 && (
                    <>
                      {' '}
                      <Trans>and</Trans>{' '}
                    </>
                  )}
                  {unmatched > 0 && (
                    <>
                      <Plural
                        value={unmatched}
                        one="# file Maki could not match to a chapter"
                        other="# files Maki could not match to a chapter"
                      />
                      {'. '}
                      <Trans>Link those by hand on the Files tab.</Trans>
                    </>
                  )}
                  {completeVolumes > 0 && unmatched === 0 && '.'}
                </Text>
              )}
            </Stack>

            <Stack gap={6}>
              <Group justify="space-between" align="baseline" gap="sm" wrap="wrap">
                <Text size="sm" fw={700} c="var(--ink-hi)">
                  <Trans>Every chapter after</Trans>{' '}
                  <Text span size="sm" fw={500} c="var(--ink-3)">
                    <Trans>click one to keep it where it is</Trans>
                  </Text>
                </Text>
                <Group gap={10} wrap="wrap">
                  <Legend className="is-move" label={renderLabel(CELL_STATE_LABELS.movesToVolume)} />
                  <Legend className="is-fill" label={renderLabel(CELL_STATE_LABELS.becomesReadable)} />
                  <Legend className="" label={renderLabel(CELL_STATE_LABELS.unchanged)} />
                  <Legend className="is-avail" label={renderLabel(CELL_STATE_LABELS.availableNotLinked)} />
                  <Legend className="is-off" label={renderLabel(CELL_STATE_LABELS.missing)} />
                  <Legend className="is-pin" label={renderLabel(CELL_STATE_LABELS.kept)} />
                </Group>
              </Group>
              <div
                style={{
                  padding: '10px 10px 6px',
                  border: '1px solid var(--border)',
                  borderRadius: 'var(--radius-surface)',
                  background: 'color-mix(in srgb, var(--surface-2) 46%, transparent)',
                  opacity: isFetching ? 0.7 : 1,
                  transition: 'opacity 0.2s',
                }}
              >
                <ScrollArea.Autosize mah={220}>
                  <div className="relink-map">
                    {plan.chapters.map((ch) => {
                      const { label } = ch
                      const stateText = renderLabel(CELL_STATE_LABELS[ch.state] ?? ch.state)
                      const detail = ch.toLabel ? `${stateText} (${ch.toLabel})` : stateText
                      const pinnable = ch.state === 'movesToVolume' || ch.state === 'becomesReadable' || ch.state === 'kept'
                      return (
                        <button
                          key={ch.id}
                          type="button"
                          className={`relink-cell tnum ${CELL_CLASS[ch.state] ?? ''}`}
                          title={t`Ch. ${label}: ${detail}`}
                          aria-label={t`Ch. ${label}: ${detail}`}
                          aria-pressed={ch.state === 'kept'}
                          disabled={!pinnable}
                          onClick={() => togglePinned(ch.id)}
                        >
                          {showNumbers || (ch.number !== null && Number.isInteger(ch.number) && ch.number % 5 === 0) ? label : ''}
                        </button>
                      )
                    })}
                  </div>
                </ScrollArea.Autosize>
                <Group justify="space-between" mt={6} px={2}>
                  <Text size="xs" c="var(--ink-3)">
                    <Trans>Hover a cell for its number.</Trans>
                  </Text>
                  <Group gap={6}>
                    <Text size="xs" c="var(--ink-2)" className="tnum">
                      <Plural value={counts.kept} one="# kept" other="# kept" />
                    </Text>
                    <Button size="compact-xs" variant="subtle" disabled={pinned.size === 0} onClick={() => setPinned(new Set())}>
                      <Trans>Clear</Trans>
                    </Button>
                  </Group>
                </Group>
              </div>
            </Stack>

            <Stack gap={8} className="relink-outcome" p="sm">
              <Text size="sm">
                {total > 0 || counts.kept > 0 ? (
                  <>
                    <Plural value={counts.moves} one="# chapter moves onto a volume" other="# chapters move onto volumes" />
                    {', '}
                    <Plural value={counts.fills} one="# becomes readable" other="# become readable" />
                    {counts.kept > 0 && (
                      <>
                        {', '}
                        <Plural value={counts.kept} one="# kept where it is" other="# kept where they are" />
                      </>
                    )}
                    {'. '}
                  </>
                ) : (
                  <>
                    <Trans>Every chapter is already on the best file available.</Trans>{' '}
                  </>
                )}
                {emptied > 0 && (
                  <>
                    <Plural value={emptied} one="# single file ends up holding nothing" other="# single files end up holding nothing" />
                    {', '}
                    <Text span size="sm" className="tnum">
                      {emptiedSize}
                    </Text>
                    {'.'}
                  </>
                )}
              </Text>
              {emptied > 0 && (
                <Radio.Group value={leftover} onChange={(v) => setLeftover(v === 'delete' ? 'delete' : 'keep')}>
                  <Group gap="lg">
                    <Radio
                      value="keep"
                      size="xs"
                      label={<Plural value={emptied} one="Keep that file" other="Keep those # files" />}
                    />
                    <Tooltip label={t`Deleting files needs the Delete series permission`} disabled={canDelete} withArrow>
                      <Radio
                        value="delete"
                        size="xs"
                        disabled={!canDelete}
                        label={
                          <>
                            <Trans>Delete them, free {emptiedSize}</Trans>{' '}
                            <Text span size="xs" c="var(--ink-3)">
                              <Trans>(cannot be undone)</Trans>
                            </Text>
                          </>
                        }
                      />
                    </Tooltip>
                  </Group>
                </Radio.Group>
              )}
            </Stack>
          </>
        )}

        <Group justify="flex-end">
          <Button variant="default" onClick={onClose} disabled={apply.isPending}>
            <Trans>Cancel</Trans>
          </Button>
          <Button
            disabled={!plan || (total === 0 && !deleting) || isFetching}
            loading={apply.isPending}
            color={deleting ? 'var(--danger-fill)' : undefined}
            leftSection={deleting ? <IconTrash size={16} /> : undefined}
            onClick={confirm}
          >
            {total === 0 && deleting ? (
              <Plural value={emptied} one="Delete # file" other="Delete # files" />
            ) : total === 0 ? (
              <Trans>Nothing to relink</Trans>
            ) : deleting ? (
              <Plural value={total} one="Relink # chapter and delete" other="Relink # chapters and delete" />
            ) : (
              <Plural value={total} one="Relink # chapter" other="Relink # chapters" />
            )}
          </Button>
        </Group>
      </Stack>
    </Modal>
  )
}

function AllNone({ onAll, onNone }: { onAll: () => void; onNone: () => void }) {
  return (
    <Group gap={2}>
      <Button size="compact-xs" variant="subtle" onClick={onAll}>
        <Trans>All</Trans>
      </Button>
      <Text size="xs" c="var(--ink-4)">
        ·
      </Text>
      <Button size="compact-xs" variant="subtle" onClick={onNone}>
        <Trans>None</Trans>
      </Button>
    </Group>
  )
}

function Legend({ className, label }: { className: string; label: string }) {
  return (
    <Group gap={6} wrap="nowrap">
      <span className={`relink-swatch ${className}`} aria-hidden />
      <Text size="xs" c="var(--ink-3)">
        {label}
      </Text>
    </Group>
  )
}

function PlanRow({
  row,
  onToggle,
  renderLabel,
}: {
  row: Row
  onToggle: () => void
  renderLabel: (d: string | MessageDescriptor) => string
}) {
  const { t } = useLingui()
  const range = useRangeText(row.labels)
  const { fileCount } = row
  const name = row.fileName ?? plural(fileCount, { one: '# single file', other: '# single files' })
  const hint = row.basis ? BASIS_HINTS[row.basis] : undefined
  const basisLabel = row.basis
    ? row.basis === 'fileName' && fileCount > 1
      ? t`From their names`
      : renderLabel(BASIS_LABELS[row.basis] ?? row.basis)
    : null
  return (
    <div className={`relink-row ${row.excluded ? 'is-off' : ''}`}>
      <Checkbox size="xs" checked={!row.excluded} onChange={onToggle} aria-label={t`Trust ${name}`} />
      <Text size="sm" fw={600} c="var(--ink-hi)" truncate title={row.fileName ?? undefined}>
        {name}
      </Text>
      <Text size="sm">
        <Text span size="sm" fw={600} c="var(--ink-hi)" className="tnum">
          {range}
        </Text>{' '}
        <Text span size="sm" c="var(--ink-3)">
          {row.excluded ? (
            row.kind === 'volume' ? (
              <Trans>stay where they are</Trans>
            ) : (
              <Trans>stay missing</Trans>
            )
          ) : row.kind === 'volume' ? (
            <Plural value={row.labels.length} one="# chapter onto this volume" other="# chapters onto this volume" />
          ) : (
            <Plural value={row.labels.length} one="becomes readable" other="become readable" />
          )}
        </Text>
      </Text>
      {basisLabel ? (
        <Tooltip label={hint ? renderLabel(hint) : undefined} withArrow disabled={!hint} multiline w={280}>
          <Badge size="sm" variant="light" color={row.basis === 'estimated' ? 'gray' : row.basis === 'existing' ? 'indigo' : 'teal'}>
            {basisLabel}
          </Badge>
        </Tooltip>
      ) : (
        <span />
      )}
    </div>
  )
}
