import { useId, useMemo, useState } from 'react'
import {
  Alert,
  Badge,
  Collapse,
  Group,
  Loader,
  Modal,
  SegmentedControl,
  Stack,
  Text,
  TextInput,
  UnstyledButton,
} from '@mantine/core'
import { IconAlertTriangle, IconArrowRight, IconChevronDown, IconInfoCircle, IconSearch } from '@tabler/icons-react'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { errorText } from '../../api/errorText'
import {
  IMPORT_SKIP_REASONS,
  useImportPlan,
  type ImportPlan,
  type ImportPlanFile,
  type ImportRequestItem,
} from '../../api/libraryImport'
import { formatBytes } from '../../format'
import { useLabel } from '../../i18n-context'
import { FigureStrip } from '../ui/FigureStrip'
import { MetaList } from '../ui/MetaList'

/** What the import builds a CBZ from (`ComicSourceKind`). */
const KIND_LABELS: Record<string, MessageDescriptor> = {
  zip: msg`a zip`,
  repack: msg`a RAR, 7z or tar archive`,
  looseImages: msg`loose page images`,
}

/** Past this many folders the preview opens on the summary and a filterable list. */
const SEARCH_FROM = 8
const MAX_RUNS = 4

/**
 * "1-40, 42-74": the numbers as runs, so a gap shows. Parts (5.5, or 1.1 then 2.1) do not break a
 * run; only a whole number with nothing under it does.
 */
function runs(values: number[]): string[] {
  const sorted = [...new Set(values)].sort((a, b) => a - b)
  const out: string[] = []
  let start = sorted[0]
  let prev = sorted[0]
  for (const v of sorted.slice(1)) {
    if (Math.floor(v) <= Math.floor(prev) + 1) {
      prev = v
      continue
    }
    out.push(start === prev ? `${start}` : `${start}-${prev}`)
    start = prev = v
  }
  if (sorted.length > 0) out.push(start === prev ? `${start}` : `${start}-${prev}`)
  return out
}

function RunList({ values }: { values: number[] }) {
  const all = runs(values)
  if (all.length <= MAX_RUNS) return <>{all.join(', ')}</>
  const shown = all.slice(0, MAX_RUNS - 1).join(', ')
  const more = all.length - (MAX_RUNS - 1)
  return (
    <Trans>
      {shown} +{more} more
    </Trans>
  )
}

function parsedNumbers(files: ImportPlanFile[]) {
  const chapters: number[] = []
  const volumes: number[] = []
  for (const f of files) {
    if (f.number !== null) {
      const n = Number(f.number)
      if (Number.isFinite(n)) chapters.push(n)
    } else if (f.volume !== null) {
      const end = f.volumeEnd ?? f.volume
      for (let v = f.volume; v <= end && v - f.volume < 500; v++) volumes.push(v)
    }
  }
  return { chapters, volumes }
}

/** A folder whose plan has something in it besides plain registering. */
function needsLook(plan: ImportPlan) {
  if (plan.error) return true
  if (plan.folderAction === 'merge' || (plan.skipped?.length ?? 0) > 0) return true
  return (plan.files ?? []).some(
    (f) => f.unlinked || f.loneFile || f.action === 'rebuildInPlace' || f.action === 'useExisting',
  )
}

function isBuilt(f: ImportPlanFile) {
  return f.action === 'build' || f.action === 'rebuildInPlace'
}

/** What a file's name says it holds, before any chapter list exists to check it against. */
function ParsedLabel({ file }: { file: ImportPlanFile }) {
  const { number, volume, volumeEnd } = file
  if (number !== null) return <Trans>Ch.{number}</Trans>
  if (volume !== null && volumeEnd !== null && volumeEnd !== volume) {
    return (
      <Trans>
        Vol.{volume}-{volumeEnd}
      </Trans>
    )
  }
  if (volume !== null) return <Trans>Vol.{volume}</Trans>
  return null
}

function ExpectedLink({ file, deferred }: { file: ImportPlanFile; deferred: boolean }) {
  const label = useLabel()
  if (file.loneFile) {
    return <Trans>no number in the name, links if the series has exactly one chapter</Trans>
  }
  if (file.unlinked) {
    const reason = label(IMPORT_SKIP_REASONS[file.unlinked] ?? file.unlinked)
    return <Trans>stays unlinked: {reason}</Trans>
  }
  if (deferred || file.chapters === null) {
    return (
      <Trans>
        expected to link as <ParsedLabel file={file} />
      </Trans>
    )
  }
  const chapters = file.chapters.slice(0, 12).join(', ')
  const extra = file.chapters.length - 12
  return extra > 0 ? (
    <Trans>
      links Ch. {chapters} +{extra} more
    </Trans>
  ) : (
    <Trans>links Ch. {chapters}</Trans>
  )
}

/** The short form of `ExpectedLink`, for the file list's own column. */
function LinkCell({ file, deferred }: { file: ImportPlanFile; deferred: boolean }) {
  if (file.unlinked) return <Trans>Unlinked</Trans>
  if (file.loneFile) return <Trans>No number</Trans>
  if (deferred || file.chapters === null) return <ParsedLabel file={file} />
  const numbers = file.chapters.map(Number).filter(Number.isFinite)
  return numbers.length === file.chapters.length && numbers.length > 0 ? (
    <Trans>
      Ch.
      <RunList values={numbers} />
    </Trans>
  ) : (
    <>{file.chapters.join(', ')}</>
  )
}

function FileAction({ file }: { file: ImportPlanFile }) {
  const label = useLabel()
  const { aside, source } = file
  switch (file.action) {
    case 'build': {
      const kind = label(KIND_LABELS[file.kind] ?? file.kind)
      return source && source !== '.' ? (
        <Trans>CBZ built from {kind} ({source}), the original stays</Trans>
      ) : (
        <Trans>CBZ built from {kind}, the original stays</Trans>
      )
    }
    case 'rebuildInPlace':
      return <Trans>not really a CBZ: the original is renamed to {aside} and a CBZ is built under this name</Trans>
    case 'useExisting':
      return <Trans>a file already has this name, so that file is registered</Trans>
    default:
      return <Trans>registered as it is</Trans>
  }
}

/** One line per file. Only a file that does something other than plain registering explains itself. */
function FileRow({ file, deferred }: { file: ImportPlanFile; deferred: boolean }) {
  const { name, size } = file
  const odd = file.action !== 'register'
  const warn = file.unlinked !== null || file.loneFile
  return (
    <div className="import-plan-file" data-warn={warn || undefined}>
      <div className="import-plan-file-name">
        <Text size="xs" fw={500} lineClamp={1} title={name} style={{ overflowWrap: 'anywhere' }}>
          {name}
        </Text>
        {odd && (
          <Text size="xs" c="var(--ink-3)">
            <FileAction file={file} />
          </Text>
        )}
        {warn && (
          <Text size="xs" c="var(--warn)">
            <ExpectedLink file={file} deferred={deferred} />
          </Text>
        )}
      </div>
      <Text size="xs" c={warn ? 'var(--warn)' : 'var(--ink-2)'} className="tnum import-plan-file-link">
        <LinkCell file={file} deferred={deferred} />
      </Text>
      <Text size="xs" c="var(--ink-3)" className="tnum import-plan-file-size">
        {formatBytes(size > 0 ? size : null)}
      </Text>
    </div>
  )
}

/** The headline facts of a folder, readable while it is closed. */
function FolderFacts({ plan }: { plan: ImportPlan }) {
  const files = plan.files ?? []
  const { chapters, volumes } = useMemo(() => parsedNumbers(files), [files])
  const built = files.filter(isBuilt).length
  const unlinked = files.filter((f) => f.unlinked).length
  const lone = files.filter((f) => f.loneFile).length
  const skipped = plan.skipped?.length ?? 0

  if (plan.error) {
    return (
      <Text span size="xs" c="var(--danger)">
        {plan.error}
      </Text>
    )
  }

  return (
    <MetaList
      inline
      items={[
        <Plural key="files" value={files.length} one="# file" other="# files" />,
        chapters.length > 0 && (
          <Trans key="ch">
            Ch.
            <RunList values={chapters} />
          </Trans>
        ),
        volumes.length > 0 && (
          <Trans key="vol">
            Vol.
            <RunList values={volumes} />
          </Trans>
        ),
        built > 0 && <Plural key="built" value={built} one="# CBZ built" other="# CBZs built" />,
        unlinked > 0 && (
          <Text key="unlinked" span size="xs" c="var(--warn)">
            <Plural value={unlinked} one="# stays unlinked" other="# stay unlinked" />
          </Text>
        ),
        lone > 0 && (
          <Text key="lone" span size="xs" c="var(--warn)">
            <Plural value={lone} one="# file with no number" other="# files with no number" />
          </Text>
        ),
        skipped > 0 && (
          <Text key="skipped" span size="xs" c="var(--warn)">
            <Plural value={skipped} one="# left out" other="# left out" />
          </Text>
        ),
      ]}
    />
  )
}

function FolderHead({ plan }: { plan: ImportPlan }) {
  const { t } = useLingui()
  const { folderName, targetFolderName, seriesTitle } = plan
  const title = seriesTitle ?? folderName
  return (
    <span className="import-plan-folder-line">
      <Text span size="sm" fw={600} style={{ overflowWrap: 'anywhere' }}>
        {folderName}
      </Text>
      {!plan.error && plan.folderAction !== 'keep' && (
        <>
          <IconArrowRight size={14} className="import-plan-arrow" aria-hidden />
          <Text span size="sm" fw={600} c="var(--ink-2)" style={{ overflowWrap: 'anywhere' }}>
            {targetFolderName}
          </Text>
        </>
      )}
      {plan.kind === 'restore' ? (
        <Badge size="xs" variant="light">
          {t({ message: `Restore ${title}`, context: 'series' })}
        </Badge>
      ) : (
        plan.existingSeriesId !== null && (
          <Badge size="xs" variant="light">
            <Trans>In library</Trans>
          </Badge>
        )
      )}
    </span>
  )
}

function FolderBody({ plan }: { plan: ImportPlan }) {
  const label = useLabel()
  const { seriesFolderName, targetFolderName, seriesTitle } = plan
  const files = plan.files ?? []
  const skipped = plan.skipped ?? []

  return (
    <Stack gap={8}>
      <Text size="xs" c="var(--ink-3)">
        {plan.folderAction === 'rename' ? (
          <Trans>Imported as {seriesTitle}. The folder is renamed.</Trans>
        ) : plan.folderAction === 'merge' ? (
          <Trans>Imported into {seriesTitle}. The files move into its existing folder.</Trans>
        ) : (
          <Trans>Imported as {seriesTitle}. The folder keeps its name.</Trans>
        )}{' '}
        {seriesFolderName !== targetFolderName && <Trans>New downloads go to {seriesFolderName}.</Trans>}{' '}
        {plan.writesCover && <Trans>Writes the series cover into the folder as cover.jpg.</Trans>}
      </Text>
      {files.length > 0 && (
        <div className="import-plan-files">
          {files.map((f) => (
            <FileRow key={f.name} file={f} deferred={plan.linkDeferred} />
          ))}
        </div>
      )}
      {skipped.length > 0 && (
        <Stack gap={2}>
          <Text size="xs" fw={600} c="var(--warn)">
            <Plural value={skipped.length} one="# file is left out" other="# files are left out" />
          </Text>
          {skipped.map((s) => {
            const { name } = s
            const reason = label(IMPORT_SKIP_REASONS[s.reason] ?? s.reason)
            return (
              <Text key={name} size="xs" c="var(--ink-3)" pl="sm">
                <Trans>
                  {name}: {reason}
                </Trans>
              </Text>
            )
          })}
        </Stack>
      )}
    </Stack>
  )
}

/** A folder as one line that opens onto its files. The files mount only while open: a first import can list thousands. */
function FolderRow({ plan, defaultOpen }: { plan: ImportPlan; defaultOpen: boolean }) {
  const [opened, setOpened] = useState(defaultOpen)
  const bodyId = useId()
  const text = (
    <span className="import-plan-folder-text">
      <FolderHead plan={plan} />
      <Text span size="xs" c="var(--ink-3)">
        <FolderFacts plan={plan} />
      </Text>
    </span>
  )

  if (plan.error) {
    return (
      <div className="import-plan-folder">
        <div className="import-plan-folder-head">{text}</div>
      </div>
    )
  }

  return (
    <div className="import-plan-folder" data-open={opened || undefined}>
      <UnstyledButton
        className="import-plan-folder-head"
        aria-expanded={opened}
        aria-controls={bodyId}
        onClick={() => setOpened((o) => !o)}
      >
        {text}
        <IconChevronDown size={16} className="import-plan-chevron" aria-hidden />
      </UnstyledButton>
      <Collapse expanded={opened} id={bodyId}>
        {opened && (
          <div className="import-plan-folder-body">
            <FolderBody plan={plan} />
          </div>
        )}
      </Collapse>
    </div>
  )
}

type Filter = 'look' | 'renamed' | 'all'

function PlanList({ plans }: { plans: ImportPlan[] }) {
  const { t } = useLingui()
  const look = plans.filter(needsLook)
  const renamed = plans.filter((p) => !p.error && p.folderAction === 'rename')
  const [filter, setFilter] = useState<Filter>(look.length > 0 ? 'look' : 'all')
  const [query, setQuery] = useState('')

  const allFiles = plans.flatMap((p) => p.files ?? [])
  const errors = plans.filter((p) => p.error).length
  const figures = [
    { label: t`Folders`, value: plans.length - errors },
    { label: t`Files`, value: allFiles.length },
    { label: t`Renamed`, value: renamed.length },
    { label: t`CBZs built`, value: allFiles.filter(isBuilt).length },
    { label: t`Stay unlinked`, value: allFiles.filter((f) => f.unlinked).length, tone: 'warn' as const },
    { label: t`Left out`, value: plans.reduce((n, p) => n + (p.skipped?.length ?? 0), 0), tone: 'warn' as const },
    ...(errors > 0 ? [{ label: t`Can't import`, value: errors, tone: 'danger' as const }] : []),
  ]

  const lookCount = look.length
  const renamedCount = renamed.length
  const total = plans.length
  const base = filter === 'look' ? look : filter === 'renamed' ? renamed : plans
  const needle = query.trim().toLocaleLowerCase()
  const shown = needle
    ? base.filter((p) =>
        [p.folderName, p.targetFolderName, p.seriesTitle].some((s) => s?.toLocaleLowerCase().includes(needle)),
      )
    : base

  return (
    <Stack gap="sm">
      <FigureStrip figures={figures} className="import-plan-figures" />
      <Group gap="sm" wrap="wrap" justify="space-between">
        <SegmentedControl
          aria-label={t`Show folders`}
          size="xs"
          value={filter}
          onChange={(v) => setFilter(v as Filter)}
          data={[
            { value: 'look', label: t`Worth a look (${lookCount})`, disabled: lookCount === 0 },
            { value: 'renamed', label: t`Renamed (${renamedCount})`, disabled: renamedCount === 0 },
            { value: 'all', label: t`All (${total})` },
          ]}
        />
        <TextInput
          size="xs"
          aria-label={t`Find a folder`}
          placeholder={t`Find a folder`}
          leftSection={<IconSearch size={14} />}
          value={query}
          onChange={(e) => setQuery(e.currentTarget.value)}
          style={{ flex: '1 1 180px', maxWidth: 280 }}
        />
      </Group>
      {filter === 'look' && (
        <Text size="xs" c="var(--ink-3)">
          <Trans>
            Folders with files that may not link, files left out, or files Maki changes. Everything else is
            registered as it is.
          </Trans>
        </Text>
      )}
      {shown.length === 0 ? (
        <Text size="sm" c="var(--ink-3)" py="md" ta="center">
          <Trans>No folder matches.</Trans>
        </Text>
      ) : (
        <div className="import-plan-folders">
          {shown.map((p) => (
            <FolderRow key={p.folderName} plan={p} defaultOpen={false} />
          ))}
        </div>
      )}
    </Stack>
  )
}

/**
 * A dry run of the import for one or more folders. Nothing is written: the server works out the
 * same folder decision and file plan the import would run and reports it.
 */
export function ImportPlanModal({
  rootFolderId,
  items,
  onClose,
}: {
  rootFolderId: number
  items: ImportRequestItem[]
  onClose: () => void
}) {
  const { t } = useLingui()
  const plan = useImportPlan(rootFolderId, items)
  const deferred = plan.data?.some((p) => !p.error && p.linkDeferred) ?? false
  const plans = plan.data ?? []

  return (
    <Modal
      opened
      onClose={onClose}
      title={t`Import preview`}
      size="xl"
      attributes={{ body: { tabIndex: 0 } }}
    >
      <Stack gap="sm">
        <Text size="sm" c="var(--ink-2)">
          <Trans>Nothing has been changed. This is what importing would do.</Trans>
        </Text>
        {deferred && (
          <Alert color="var(--info)" icon={<IconInfoCircle size={16} />}>
            <Trans>Chapters below are read off the file names until sources are found.</Trans>
          </Alert>
        )}
        {plan.isPending && (
          <Group justify="center" py="lg">
            <Loader size="sm" />
          </Group>
        )}
        {plan.isError && (
          <Alert color="var(--danger)" icon={<IconAlertTriangle size={16} />}>
            {errorText(plan.error)}
          </Alert>
        )}
        {plans.length >= SEARCH_FROM ? (
          <PlanList plans={plans} />
        ) : (
          plans.length > 0 && (
            <div className="import-plan-folders">
              {plans.map((p) => (
                <FolderRow key={p.folderName} plan={p} defaultOpen={plans.length === 1 || needsLook(p)} />
              ))}
            </div>
          )
        )}
      </Stack>
    </Modal>
  )
}
