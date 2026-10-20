import {
  Alert,
  Badge,
  Button,
  Checkbox,
  Group,
  Loader,
  Modal,
  ScrollArea,
  SegmentedControl,
  Stack,
  Text,
  VisuallyHidden,
} from '@mantine/core'
import { IconAlertTriangle, IconBan, IconTrash } from '@tabler/icons-react'
import { useEffect, useMemo, useState } from 'react'
import { Trans, Plural, useLingui } from '@lingui/react/macro'
import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { useImportPlan, useQueue, useSettleImport } from '../api/hooks'
import { queueErrorMessage } from '../api/queue'
import type { ImportDecision, ImportPlanFileDto, TorrentImportPlanDto } from '../api/types'
import { useLabel } from '../i18n-context'
import { formatBytes } from '../format'
import { errorText } from '../api/errorText'
import { PendingLabel } from './ui/PendingLabel'
import { AllNone, useRangeText } from './ChapterMapParts'

type Mode = Exclude<ImportDecision, 'Reject'>
type CellState = 'moves' | 'readable' | 'keepsFile' | 'leftOut' | 'unchanged' | 'missing'

const CELL_LABELS: Record<CellState, MessageDescriptor> = {
  moves: msg`replaced`,
  readable: msg`new`,
  keepsFile: msg`kept`,
  leftOut: msg`skipped`,
  unchanged: msg`unchanged`,
  missing: msg`missing`,
}

const CELL_CLASS: Record<CellState, string> = {
  moves: 'is-move',
  readable: 'is-fill',
  keepsFile: 'is-held',
  leftOut: 'is-avail',
  unchanged: '',
  missing: 'is-off',
}

const STATE_ORDER: CellState[] = ['moves', 'readable', 'keepsFile', 'leftOut', 'unchanged', 'missing']

/** What a downloaded file does to its own chapters, in the row's coloured counts. */
const FILE_EFFECTS: CellState[] = ['moves', 'readable', 'keepsFile']

interface Cell {
  label: string
  number: number | null
  state: CellState
  file: string | null
}

/** The server's own rule (`TorrentImportService.ImportAsync`): which files a decision imports. */
function imports(file: ImportPlanFileDto, mode: Mode, skipped: Set<string>) {
  return !skipped.has(file.fileName) && (mode === 'Replace' || file.replaces.length === 0 || file.newChapters.length > 0)
}

/**
 * Every chapter as it would read after the decision, plus the files a Replace would leave holding
 * nothing. A file is trashed only when every chapter on it moves, the same test the import runs.
 */
function planMap(plan: TorrentImportPlanDto, mode: Mode, skipped: Set<string>) {
  const owner = new Map<string, ImportPlanFileDto>()
  for (const f of plan.files) for (const c of f.chapters) if (!owner.has(c)) owner.set(c, f)

  const cells: Cell[] = plan.chapters.map(({ label, number, hasFile }) => {
    const f = owner.get(label)
    let state: CellState
    if (!f) state = hasFile ? 'unchanged' : 'missing'
    else if (!imports(f, mode, skipped)) state = f.newChapters.includes(label) ? 'leftOut' : 'unchanged'
    else if (f.newChapters.includes(label)) state = 'readable'
    else if (mode === 'Replace' && f.replaces.some((r) => r.chapters.includes(label))) state = 'moves'
    else state = 'keepsFile'
    return { label, number, state, file: f?.fileName ?? null }
  })

  const moved = new Set(cells.filter((c) => c.state === 'moves').map((c) => c.label))
  const existing = new Map(plan.files.flatMap((f) => f.replaces).map((r) => [r.chapterFileId, r]))
  const trashed = [...existing.values()].filter((r) => r.chapters.length > 0 && r.chapters.every((c) => moved.has(c)))
  return {
    cells,
    trashedCount: trashed.length,
    trashedBytes: trashed.reduce((sum, r) => sum + r.size, 0),
  }
}

/**
 * The decision behind a download parked as "Needs review": it finished, and importing it would take
 * chapters off files already in the library. Nothing has been copied or deleted at this point, so
 * every option here is still open. The map is worked out here from the plan, with the same rules
 * the import runs, so it changes on every toggle without asking the server again.
 */
export function ImportReviewModal({
  queueItemId,
  onClose,
}: {
  queueItemId: number | null
  onClose: () => void
}) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const { data: plan, isLoading, isError, error, refetch } = useImportPlan(queueItemId)
  const { data: queue } = useQueue()
  const settle = useSettleImport()
  const [mode, setMode] = useState<Mode>('Replace')
  const [skipped, setSkipped] = useState<Set<string>>(new Set())

  useEffect(() => {
    setSkipped(new Set(plan?.isUpgrade ? plan.suggestedSkips : []))
    setMode('Replace')
  }, [plan])

  const map = useMemo(() => (plan ? planMap(plan, mode, skipped) : null), [plan, mode, skipped])

  const decide = (decision: ImportDecision) => {
    if (queueItemId === null) return
    const skipFiles = decision !== 'Reject' && skipped.size > 0 ? [...skipped] : undefined
    settle.mutate(
      { id: queueItemId, mode: decision, skipFiles, parsingFingerprint: plan?.parsingFingerprint },
      // A refusal can mean the plan went stale; the fresh one is what the next click should send.
      { onSuccess: onClose, onError: () => void refetch() },
    )
  }
  const pendingProps = (decision: ImportDecision) => {
    const mine = settle.isPending && (decision === 'Reject') === (settle.variables?.mode === 'Reject')
    return { loading: mine, disabled: settle.isPending && !mine }
  }

  const setSkip = (names: string[], skip: boolean) =>
    setSkipped((prev) => {
      const next = new Set(prev)
      for (const name of names) {
        if (skip) next.add(name)
        else next.delete(name)
      }
      return next
    })

  const queueItem = queue?.items.find((q) => q.id === queueItemId)
  const guardMessage = queueItem?.errorKey === 'error.upgrades.volumeGuard' ? queueErrorMessage(queueItem, renderLabel) : null

  return (
    <Modal opened={queueItemId !== null} onClose={onClose} title={t`Review import`} size={720}>
      {isError ? (
        <Stack gap="md">
          <Alert color="var(--danger)" icon={<IconAlertTriangle size={16} />} title={t`Can't load this import`}>
            {errorText(error)}
          </Alert>
          <Group justify="flex-end">
            <Button variant="default" onClick={onClose}>
              <Trans>Close</Trans>
            </Button>
            <Button variant="light" onClick={() => void refetch()}>
              <Trans>Retry</Trans>
            </Button>
          </Group>
        </Stack>
      ) : isLoading || !plan || !map ? (
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
              <PendingLabel pending={<Trans>Discarding</Trans>}>
                <Trans>Discard download</Trans>
              </PendingLabel>
            </Button>
          </Group>
        </Stack>
      ) : (
        <ReviewBody
          plan={plan}
          map={map}
          mode={mode}
          onMode={setMode}
          skipped={skipped}
          onSkip={setSkip}
          guardMessage={guardMessage}
          onDecide={decide}
          pendingProps={pendingProps}
          onClose={onClose}
        />
      )}
    </Modal>
  )
}

function ReviewBody({
  plan,
  map,
  mode,
  onMode,
  skipped,
  onSkip,
  guardMessage,
  onDecide,
  pendingProps,
  onClose,
}: {
  plan: TorrentImportPlanDto
  map: ReturnType<typeof planMap>
  mode: Mode
  onMode: (mode: Mode) => void
  skipped: Set<string>
  onSkip: (names: string[], skip: boolean) => void
  guardMessage: string | null
  onDecide: (decision: ImportDecision) => void
  pendingProps: (decision: ImportDecision) => { loading: boolean; disabled: boolean }
  onClose: () => void
}) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const [lit, setLit] = useState<string | null>(null)
  const { cells, trashedCount, trashedBytes } = map
  const replace = mode === 'Replace'

  const totalSize = formatBytes(plan.files.reduce((sum, f) => sum + f.size, 0) || null)
  const importedCount = plan.files.filter((f) => imports(f, mode, skipped)).length
  const trashedSize = formatBytes(trashedBytes || null)
  const showNumbers = cells.length <= 40
  const counts = STATE_ORDER.map((state) => ({ state, n: cells.filter((c) => c.state === state).length })).filter(
    (c) => c.n > 0,
  )
  const suggested = new Set(plan.suggestedSkips)
  const allNames = plan.files.map((f) => f.fileName)
  const effectsOf = (fileName: string) =>
    FILE_EFFECTS.map((state) => ({
      state,
      n: cells.filter((c) => c.file === fileName && c.state === state).length,
    })).filter((e) => e.n > 0)

  return (
    <Stack gap="md">
      <div>
        <Text fw={600} c="var(--ink-hi)">
          {plan.seriesTitle}
        </Text>
        <Text size="xs" c="var(--ink-4)" truncate title={plan.releaseName}>
          {plan.releaseName} · <span className="tnum">{totalSize}</span>
        </Text>
      </div>

      {guardMessage && (
        <Alert color="var(--warn)" icon={<IconAlertTriangle size={16} />} title={t`Held back before importing`}>
          {guardMessage}
        </Alert>
      )}

      <SegmentedControl
        fullWidth
        className="import-review-mode"
        aria-label={t`How to import`}
        value={mode}
        onChange={(v) => onMode(v === 'SkipExisting' ? 'SkipExisting' : 'Replace')}
        data={[
          { value: 'Replace', label: t`Replace what it covers` },
          { value: 'SkipExisting', label: t`Only add what is missing` },
        ]}
      />

      <div className="import-review-map">
        <ScrollArea.Autosize mah={220}>
          <div className="relink-map" aria-hidden>
            {cells.map((cell) => {
              const { label } = cell
              const stateText = renderLabel(CELL_LABELS[cell.state])
              const shown =
                showNumbers || (cell.number !== null && Number.isInteger(cell.number) && cell.number % 5 === 0)
              return (
                <span
                  key={label}
                  className={`relink-cell is-static tnum ${CELL_CLASS[cell.state]} ${lit && cell.file === lit ? 'is-lit' : ''}`}
                  title={t`Ch. ${label}: ${stateText}`}
                >
                  {shown ? label : ''}
                </span>
              )
            })}
          </div>
        </ScrollArea.Autosize>
        <Group gap="4px 16px" wrap="wrap" mt={10} px={2}>
          {counts.map(({ state, n }) => (
            <Group key={state} gap={6} wrap="nowrap">
              <span className={`relink-swatch ${CELL_CLASS[state]}`} aria-hidden />
              <Text size="sm" c="var(--ink-3)">
                <Text span size="sm" fw={700} c="var(--ink-hi)" className="tnum">
                  {n}
                </Text>{' '}
                {renderLabel(CELL_LABELS[state])}
              </Text>
            </Group>
          ))}
        </Group>
      </div>

      {replace && trashedCount > 0 && (
        <Group gap={8} wrap="nowrap" align="flex-start">
          <IconTrash size={16} color="var(--warn)" style={{ flexShrink: 0, marginTop: 2 }} />
          <Text size="sm">
            <Plural value={trashedCount} one="# file goes to the trash" other="# files go to the trash" />
            {', '}
            <span className="tnum">{trashedSize}</span>.{' '}
            <Text span size="sm" c="var(--ink-3)">
              <Trans>You can undo this from Activity.</Trans>
            </Text>
          </Text>
        </Group>
      )}

      <div className="relink-groups" onMouseLeave={() => setLit(null)}>
        <div className="relink-section">
          <Text size="xs" fw={600} c="var(--ink-4)">
            <Trans>Files in this download</Trans>
          </Text>
          <AllNone onAll={() => onSkip(allNames, false)} onNone={() => onSkip(allNames, true)} />
        </div>
        {plan.files.map((file) => (
          <FileRow
            key={file.fileName}
            file={file}
            skipped={skipped.has(file.fileName)}
            imported={imports(file, mode, skipped)}
            atCutoff={plan.isUpgrade && suggested.has(file.fileName)}
            effects={effectsOf(file.fileName)}
            lit={lit === file.fileName}
            onLit={setLit}
            onToggle={() => onSkip([file.fileName], !skipped.has(file.fileName))}
          />
        ))}
      </div>

      <Group justify="space-between" gap="xs">
        <Button
          variant="subtle"
          color="var(--neutral)"
          onClick={() => onDecide('Reject')}
          {...pendingProps('Reject')}
          leftSection={<IconBan size={16} />}
        >
          <PendingLabel pending={<Trans>Ignoring</Trans>}>
            <Trans>Ignore this download</Trans>
          </PendingLabel>
        </Button>
        <Group gap="xs">
          <Button variant="default" onClick={onClose}>
            <Trans>Decide later</Trans>
          </Button>
          <Button
            onClick={() => onDecide(mode)}
            {...pendingProps(mode)}
            disabled={importedCount === 0 || pendingProps(mode).disabled}
          >
            <PendingLabel pending={<Trans>Importing</Trans>}>
              {importedCount === 0 ? (
                <Trans>Nothing to import</Trans>
              ) : (
                <Plural value={importedCount} one="Import # file" other="Import # files" />
              )}
            </PendingLabel>
          </Button>
        </Group>
      </Group>
    </Stack>
  )
}

function FileRow({
  file,
  skipped,
  imported,
  atCutoff,
  effects,
  lit,
  onLit,
  onToggle,
}: {
  file: ImportPlanFileDto
  skipped: boolean
  imported: boolean
  atCutoff: boolean
  effects: { state: CellState; n: number }[]
  lit: boolean
  onLit: (fileName: string | null) => void
  onToggle: () => void
}) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const range = useRangeText(file.chapters)

  let effect
  if (file.chapters.length === 0)
    effect = (
      <Text size="xs" c="var(--ink-4)">
        <Trans>no chapters matched</Trans>
      </Text>
    )
  else if (skipped && atCutoff)
    effect = (
      <Badge size="sm" variant="light" color="var(--neutral)">
        <Trans>At cutoff</Trans>
      </Badge>
    )
  else if (skipped) effect = null
  else if (!imported)
    effect = (
      <Text size="xs" c="var(--ink-4)">
        <Trans>nothing new</Trans>
      </Text>
    )
  else
    effect = (
      <Group gap={10} wrap="nowrap">
        {effects.map(({ state, n }) => (
          <Group key={state} gap={4} wrap="nowrap" title={`${n} ${renderLabel(CELL_LABELS[state])}`}>
            <span className={`relink-swatch ${CELL_CLASS[state]}`} aria-hidden />
            <Text size="sm" fw={600} c="var(--ink-hi)" className="tnum">
              {n}
              <VisuallyHidden> {renderLabel(CELL_LABELS[state])}</VisuallyHidden>
            </Text>
          </Group>
        ))}
      </Group>
    )

  return (
    <div className={`relink-row import-review-row ${lit ? 'is-lit' : ''}`} onMouseEnter={() => onLit(file.fileName)}>
      <Checkbox
        size="xs"
        className="import-review-tick"
        checked={!skipped}
        onChange={onToggle}
        aria-label={t`Import ${file.fileName}`}
      />
      <Text
        size="sm"
        fw={600}
        c={imported ? 'var(--ink-hi)' : 'var(--ink-3)'}
        truncate
        title={file.fileName}
        className="import-review-name"
      >
        {file.label ?? file.fileName}
      </Text>
      <Text size="sm" c="var(--ink-3)" className="tnum import-review-range">
        {range}
      </Text>
      <div className="import-review-effect">{effect}</div>
      <Text size="xs" c="var(--ink-4)" className="tnum import-review-size">
        {formatBytes(file.size > 0 ? file.size : null)}
      </Text>
    </div>
  )
}
