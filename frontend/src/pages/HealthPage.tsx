import {
  ActionIcon,
  Alert,
  Anchor,
  Badge,
  Button,
  Checkbox,
  Divider,
  Group,
  Image,
  Loader,
  Menu,
  Modal,
  Pagination,
  Select,
  SimpleGrid,
  Skeleton,
  Stack,
  Switch,
  Table,
  Tabs,
  Text,
  TextInput,
  Title,
  Tooltip,
  VisuallyHidden,
} from '@mantine/core'
import {
  IconAlertOctagon,
  IconAlertTriangle,
  IconBooks,
  IconCircleCheck,
  IconChevronDown,
  IconClockPlay,
  IconDatabase,
  IconDownload,
  IconEye,
  IconFileImport,
  IconLink,
  IconPlugConnected,
  IconPhotoScan,
  IconRefresh,
  IconScan,
  IconServer,
  IconSettings,
  IconStethoscope,
  IconTrash,
  type Icon,
} from '../lib/icons'
import { Fragment, useMemo, useState, type ReactNode } from 'react'
import { useDebouncedValue } from '@mantine/hooks'
import { Link, useSearchParams } from 'react-router-dom'
import { useImageCache, useRebuildImageCache } from '../api/hooks'
import { chapterRangeLabel } from '../api/requests'
import { notifications } from '../lib/toast'
import {
  useHealthAction,
  useHealthData,
  type Analysis,
  type FileDetail,
  type FileRef,
  type HealthCheck,
  type HealthFile,
  type HealthFilesPage,
  type HealthOperation,
  type HealthOverview,
  type MatchCounterpart,
  type MissingSummary,
  type MoveEvidence,
  type OperationDetail,
  type OperationGroup as OperationGroupData,
  type SourceFailures,
  type UnlinkedCase,
  type UnlinkedMatch,
  type UnlinkedSummary,
} from '../api/health'
import { MetaList } from '../components/ui/MetaList'
import { PageHeader } from '../components/ui/PageHeader'
import { SectionHeader } from '../components/ui/SectionHeader'
import { EmptyState } from '../components/ui/EmptyState'
import { Panel } from '../components/ui/Panel'
import { TableSkeleton } from '../components/ui/TableSkeleton'
import { FigureStrip } from '../components/ui/FigureStrip'
import { IconBrandMark } from '../components/IconBrandMark'
import { StatusDot } from '../components/ui/StatusDot'
import { MonoValue } from '../components/ui/MonoValue'
import { SeriesCell } from '../components/SeriesCell'
import { formatBytes, formatDateTime, formatNumber, formatTime, formatWeekdayDate } from '../format'
import { useLabel } from '../i18n-context'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { msg, plural } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { SurfaceFrame } from '../components/ui/SurfaceFrame'

/** Select value standing for "no pinned source": let the series' priority order decide. */
const AUTOMATIC = 'automatic'

/** Statuses that mean somebody has to look at this. Everything else is passing or informational. */
const ISSUE = ['error', 'warning', 'unavailable']

/** Sort key for a check: unresolved first, then acknowledged, then passing. */
const weight = (check: HealthCheck) =>
  !ISSUE.includes(check.status) ? 2 : check.acknowledged ? 1 : 0

/** HealthMonitor's category strings. Anything unknown falls back to the generic system icon. */
const CATEGORY_LABEL: Record<string, MessageDescriptor> = {
  library: msg`Library`,
  storage: msg`Storage`,
  connections: msg`Connections`,
  system: msg`System`,
  downloads: msg`Downloads`,
  job: msg`Scheduled jobs`,
}

const CATEGORY_ICON: Record<string, Icon> = {
  library: IconBooks,
  storage: IconDatabase,
  connections: IconPlugConnected,
  system: IconServer,
  downloads: IconDownload,
  job: IconClockPlay,
}

const tone = (status: string) =>
  status === 'error' || status === 'failed' || status === 'corrupt'
    ? 'danger'
    : ['warning', 'partial', 'open', 'review'].includes(status)
      ? 'warn'
      : ['healthy', 'complete', 'completed', 'resolved', 'ok'].includes(status)
        ? 'ok'
        : ['running', 'applying', 'deleting', 'downloading', 'info'].includes(status)
          ? 'info'
          : 'neutral'

const color = (status: string) => `var(--${tone(status)})`

/** The wire values HealthMonitor and the scan/operation services send. Anything else shows as sent. */
const STATUS_LABEL: Record<string, MessageDescriptor> = {
  healthy: msg`Healthy`,
  ok: msg`OK`,
  warning: msg`Warning`,
  error: msg`Error`,
  failed: msg`Failed`,
  corrupt: msg`Corrupt`,
  partial: msg`Partial`,
  open: msg`Open`,
  review: msg`Needs review`,
  resolved: msg`Resolved`,
  ignored: msg`Ignored`,
  acknowledged: msg`Acknowledged`,
  pending: msg`Pending`,
  running: msg`Running`,
  applying: msg`Applying`,
  deleting: msg`Deleting`,
  downloading: msg`Downloading`,
  complete: msg`Complete`,
  completed: msg`Completed`,
  cancelled: msg`Cancelled`,
  unavailable: msg`Unavailable`,
}

const bytes = (size: number, missing: string) => (size < 0 ? missing : formatBytes(size))

/** Rows per page of every paged list, as HealthController returns them. */
const PAGE_SIZE = 30

const RUNNING_STATES = ['pending', 'running', 'applying', 'deleting', 'downloading']

/** Operation states with nothing left to do, which the Repairs list lets you dismiss. */
const FINISHED_STATES = ['completed', 'cancelled', 'failed']

/** HealthScanService's finding kinds, in the order the filter lists them. */
const FINDING_LABEL: Record<string, MessageDescriptor> = {
  missing: msg`Missing`,
  empty: msg`Empty`,
  corrupt: msg`Corrupt`,
  noPages: msg`No pages`,
  damagedImage: msg`Damaged image`,
  duplicate: msg`Duplicate`,
  unlinked: msg`Not linked`,
  sizeMismatch: msg`Size mismatch`,
  ambiguousNames: msg`Ambiguous names`,
  unreadable: msg`Unreadable`,
}

const FINDING_STATES = ['open', 'acknowledged', 'ignored', 'resolved']

/** What one copy of a set of identical archives is for. */
const COPY_ROLE: Record<'keep' | 'spare' | 'missing', MessageDescriptor> = {
  keep: msg`Keep`,
  spare: msg`Spare`,
  missing: msg`Missing`,
}

const copyRole = (c: { kept: boolean; missing: boolean }) => (c.kept ? 'keep' : c.missing ? 'missing' : 'spare')

/** HealthMatchService.Cases, in the order the Unlinked summary lists them: one-click first. */
const CASE_ORDER: UnlinkedCase[] = ['import', 'rival', 'noChapter', 'outside', 'unrecognized', 'identical', 'moved']

const CASE_LABEL: Record<UnlinkedCase, MessageDescriptor> = {
  import: msg`Ready to import`,
  rival: msg`Chapter already has a file`,
  noChapter: msg`Chapter not in the series yet`,
  outside: msg`Not in a series folder`,
  unrecognized: msg`Name not recognised`,
  identical: msg`Identical to another file`,
  moved: msg`New path of a missing file`,
}

const CASE_HINT: Record<UnlinkedCase, MessageDescriptor> = {
  import: msg`Nothing holds these chapters yet, so importing just links them. Nothing to compare.`,
  rival: msg`Another file already backs the chapter. Open each one to compare the two before keeping either.`,
  noChapter: msg`The series has no chapter with this number. Refresh the series, then import.`,
  outside: msg`No series owns the folder these sit in. Bring the folder in from the Import page.`,
  unrecognized: msg`The file name has no chapter or volume number. Rename it to the library's format and rescan.`,
  identical: msg`Byte for byte the same as another file. The Duplicate filter deletes the spare copy.`,
  moved: msg`A missing file's record most likely belongs to this file. The Missing filter relinks it.`,
}

/** "Ch. 77–85" from whatever chapters a file backs, or null when none carries a number. */
function chapterSpan(chapters: { number?: number }[]) {
  const numbers = chapters.map((c) => c.number).filter((n): n is number => n != null)
  return numbers.length === 0 ? null : chapterRangeLabel(Math.min(...numbers), Math.max(...numbers))
}

/** What a history entry records, as HealthScanService/HealthOperationService/HealthMonitor tag it. */
const HISTORY_KIND_LABEL: Record<string, MessageDescriptor> = {
  scan: msg`Scan`,
  repair: msg`Repair`,
  delete: msg`Delete`,
  job: msg`Job`,
  transition: msg`Status change`,
  review: msg`Review`,
  import: msg`Import`,
  relink: msg`Relink`,
  record: msg`Record`,
}

const HISTORY_KIND_ICON: Record<string, Icon> = {
  scan: IconScan,
  repair: IconRefresh,
  delete: IconTrash,
  job: IconClockPlay,
  transition: IconStethoscope,
  review: IconEye,
  import: IconFileImport,
  relink: IconLink,
  record: IconRefresh,
}

/** Keyed by tone, so every status that reads as fixed, a warning or broken shares a shape. */
const TRANSITION_ICON: Record<string, Icon> = {
  ok: IconCircleCheck,
  warn: IconAlertTriangle,
  danger: IconAlertOctagon,
}

const TODAY = msg`Today`
const YESTERDAY = msg`Yesterday`

interface HistoryEntry {
  id: number
  createdAt: string
  kind: string
  message: string
  status?: string | null
  /** The status a status change left. Unknown for a check's first change. */
  from?: string | null
}

function Status({ value, hint }: { value: string; hint?: boolean }) {
  const renderLabel = useLabel()
  return (
    <StatusDot tone={hint ? 'info' : tone(value)} live={value === 'running'}>
      {renderLabel(STATUS_LABEL[value] ?? value)}
    </StatusDot>
  )
}

export default function HealthPage() {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const [params, setParams] = useSearchParams()
  const tab = params.get('tab') ?? 'overview'
  // Quick while a scan is in flight so its progress moves; otherwise only a slow check for scans
  // that started elsewhere.
  const overview = useHealthData<HealthOverview>('', true, (data) =>
    data?.scans.some((s) => RUNNING_STATES.includes(s.status)) ? 5000 : 30_000,
  )
  const scanning = overview.data?.scans.some((s) => RUNNING_STATES.includes(s.status)) ?? false
  const [fileId, setFileId] = useState<number | null>(null)
  const [operationId, setOperationId] = useState<number | null>(null)
  const [search, setSearch] = useState('')
  const [root, setRoot] = useState<string | null>(null)
  const [kind, setKind] = useState<string | null>(null)
  const [state, setState] = useState<string | null>('open')
  const [page, setPage] = useState(1)
  // Value is the file's displayed version at the moment it was selected, so a bulk delete can
  // tell the server what the reviewer actually saw rather than trusting whatever is current now.
  // Path and role ride along so a selection spanning pages can still name and filter what it deletes.
  const [selected, setSelected] = useState<Map<number, Picked>>(new Map())
  // What the bulk delete dialog will remove. `spares` words it for identical copies, where nothing is lost.
  const [deleteTargets, setDeleteTargets] = useState<{ files: FileRef[]; spares: boolean } | null>(null)
  // What a bulk action could not do, kept on screen until dismissed.
  const [report, setReport] = useState<{ summary: string; failures: DeleteReport['failures'] } | null>(null)
  // Under the Unlinked filter, one case of it.
  const [fileCase, setFileCase] = useState<UnlinkedCase | null>(null)
  const [historyPage, setHistoryPage] = useState(1)
  const [repairPage, setRepairPage] = useState(1)

  const [debouncedSearch] = useDebouncedValue(search, 300)
  const query = new URLSearchParams({ page: String(page), search: debouncedSearch })
  if (root) query.set('rootId', root)
  if (kind) query.set('kind', kind)
  if (state) query.set('state', state)
  if (kind === 'unlinked' && fileCase) query.set('case', fileCase)

  // The server answers the Duplicate filter with one row per set of identical copies.
  const grouped = kind === 'duplicate'
  const files = useHealthData<HealthFilesPage>(
    `/files?${query}`,
    tab === 'files',
    scanning ? 5000 : false,
  )
  const operations = useHealthData<{ items: OperationGroupData[]; total: number }>(
    `/operations?page=${repairPage}`,
    tab === 'repairs',
    (data) => (data?.items.some((g) => RUNNING_STATES.includes(g.status)) ? 5000 : false),
  )
  const history = useHealthData<{ items: HistoryEntry[]; total: number }>(
    `/history?page=${historyPage}`,
    tab === 'history',
  )

  // Separate instances so one control's pending state does not spin the others.
  const action = useHealthAction()
  const refresh = useHealthAction()
  const quick = useHealthAction()
  const run = (path: string, body = {}) => quick.mutate({ path, body })
  const scanRoot = tab === 'files' && root ? Number(root) : null
  const scanRootPath = scanRoot ? overview.data?.roots.find((r) => r.id === scanRoot)?.path : undefined
  // Every filter change re-queries a different set of rows, so a selection made against the old
  // one would silently act on files the user can no longer see.
  const refilter = (apply: () => void) => {
    apply()
    setPage(1)
    setSelected(new Map())
  }
  const bulk = (path: string, body: object) =>
    action.mutate({ path, body }, { onSuccess: () => setSelected(new Map()) })
  // A bulk action that reports what it could not do: a toast when everything went, the list otherwise.
  const bulkWithReport = (path: string, body: object, summarize: (result: BulkResult) => string) =>
    action.mutate(
      { path, body },
      {
        onSuccess: (raw) => {
          const result = raw as unknown as BulkResult
          setSelected(new Map())
          const summary = summarize(result)
          if (result.failures?.length) setReport({ summary, failures: result.failures })
          else {
            setReport(null)
            notifications.show({ color: 'var(--ok)', message: summary })
          }
        },
      },
    )
  // The bulk buttons share one mutation; only the one that started it shows pending.
  const bulkKey = (v: typeof action.variables) => {
    if (!v) return null
    const body = v.body as { verify?: boolean; state?: string }
    if (v.path === '/scans') return body.verify ? 'verify' : 'rescan'
    if (v.path === '/findings/review') return body.state ?? null
    return v.path
  }
  const bulkPending = (key: string) => {
    const mine = action.isPending && bulkKey(action.variables) === key
    return { loading: mine, disabled: action.isPending && !mine }
  }
  const ids = [...selected.keys()]
  // A grouped row stands for every copy in it, so selecting it selects them all.
  const rowEntries = (file: HealthFile): [number, Picked][] =>
    grouped && file.copies
      ? file.copies.map((c) => [
          c.id,
          { version: c.version, relativePath: c.relativePath, spare: c.spare, kinds: c.findings.map((f) => f.kind) },
        ])
      : [
          [
            file.id,
            { version: file.version, relativePath: file.relativePath, spare: false, kinds: file.findings.map((f) => f.kind) },
          ],
        ]
  const toggle = (entries: [number, Picked][], on: boolean) => {
    const next = new Map(selected)
    for (const [id, pick] of entries) {
      if (on) next.set(id, pick)
      else next.delete(id)
    }
    setSelected(next)
  }
  const pageEntries = files.data?.items.flatMap(rowEntries) ?? []
  const pageIds = pageEntries.map(([id]) => id)
  // Under the Duplicate filter a selection holds kept copies too; deleting only ever takes the spares.
  const deletable: FileRef[] = [...selected]
    .filter(([, pick]) => !grouped || pick.spare)
    .map(([id, { version, relativePath }]) => ({ id, version, relativePath }))
  const spares = grouped ? files.data?.spares : undefined
  const sparesTotal = spares?.total ?? 0
  const sparesShown = spares?.items.length ?? 0
  const deletableCount = deletable.length
  const pickedWith = (kinds: string[]) =>
    [...selected].filter(([, pick]) => pick.kinds.some((k) => kinds.includes(k)))
  // Replacements are capped at 100 by the server, like deletion.
  const damaged = pickedWith(DAMAGE).slice(0, 100)
  const damagedCount = damaged.length
  const resized = pickedWith(['sizeMismatch'])
  const resizedCount = resized.length
  const moves = kind === 'missing' ? files.data?.moves : undefined
  const cases = kind === 'unlinked' ? files.data?.cases : undefined
  const queryError = overview.error ?? files.error ?? operations.error ?? history.error
  const error = queryError ?? action.error ?? quick.error ?? refresh.error
  // Acknowledged checks are still issues, but they are issues someone has already decided about,
  // so they do not belong in a number whose job is to say "something needs you".
  const openChecks = overview.data?.checks.filter((c) => ISSUE.includes(c.status) && !c.acknowledged) ?? []
  const issues = openChecks.length
  // Red is kept for something broken; a page of warnings reads as a page of failures otherwise.
  const issuesTone = openChecks.some((c) => c.status !== 'warning') ? 'danger' : issues > 0 ? 'warn' : undefined
  const partialScanError = overview.data?.scans.find((s) => s.error)?.error
  const selectedCount = selected.size

  return (
    <SurfaceFrame pageStyle="operational" className="health-surface">
      <PageHeader
        compact
        title={t`Health`}
        description={t`Checks on the server and the library, and the repairs they found.`}
        actions={
          <>
            <Button
              variant="default"
              leftSection={<IconRefresh size={16} />}
              loading={refresh.isPending}
              onClick={() => refresh.mutate({ path: '/refresh', body: {} })}
            >
              <Trans>Check now</Trans>
            </Button>
            <Tooltip label={t`Health settings`} withArrow>
              <ActionIcon
                component={Link}
                to="/settings?tab=system&s=health"
                variant="default"
                size="lg"
                aria-label={t`Health settings`}
              >
                <IconSettings size={18} />
              </ActionIcon>
            </Tooltip>
            <Menu position="bottom-end" withinPortal width={320}>
              <Menu.Target>
                <Button leftSection={<IconScan size={16} />} rightSection={<IconChevronDown size={16} />}>
                  <Trans>Scan files</Trans>
                </Button>
              </Menu.Target>
              <Menu.Dropdown>
                {scanRootPath && (
                  <Menu.Label>
                    <Trans>Only {scanRootPath}, the root chosen on the Files tab</Trans>
                  </Menu.Label>
                )}
                <Menu.Item onClick={() => run('/scans', { rootFolderId: scanRoot })}>
                  <Text size="sm" fw={600}>
                    <Trans>Index</Trans>
                  </Text>
                  <Text size="xs" c="var(--ink-4)">
                    <Trans>Finds missing, new or broken files. Takes seconds.</Trans>
                  </Text>
                </Menu.Item>
                <Menu.Item onClick={() => run('/scans', { rootFolderId: scanRoot, verify: true })}>
                  <Text size="sm" fw={600}>
                    <Trans>Verify</Trans>
                  </Text>
                  <Text size="xs" c="var(--ink-4)">
                    <Trans>Reads every byte to catch rotted files. About half an hour per 100 GB.</Trans>
                  </Text>
                </Menu.Item>
              </Menu.Dropdown>
            </Menu>
          </>
        }
      />

      {error && (
        <Alert
          color="var(--danger)"
          mb="lg"
          withCloseButton={!queryError}
          closeButtonLabel={t`Dismiss`}
          onClose={() => {
            action.reset()
            quick.reset()
            refresh.reset()
          }}
        >
          {error.message}
        </Alert>
      )}
      <FigureStrip
        loading={overview.isPending}
        figures={[
          {
            label: t`Server issues`,
            value: issues,
            tone: issuesTone,
            mark: (
              <span className="brand-mark empty-state-mark">
                <IconBrandMark mood={overview.isPending ? 'awake' : issues > 0 ? 'asking' : 'pleased'} size={40} />
              </span>
            ),
          },
          { label: t`Open file problems`, value: overview.data?.openFindings ?? 0, tone: 'warn' },
          { label: t`Files scanned`, value: overview.data?.files ?? 0 },
        ]}
      />

      {overview.data?.scans
        .filter((s) => ['pending', 'running'].includes(s.status))
        .map((scan) => {
          const { id: scanId, status: scanStatus, completed, total } = scan
          const scanTitle = scan.verify
            ? t`Verify scan ${scanId}: ${scanStatus}`
            : t`Index scan ${scanId}: ${scanStatus}`
          return (
            <Alert key={scan.id} title={scanTitle} mb="lg">
              <Group justify="space-between">
                <Text>
                  {plural(total, {
                    one: `${completed} / # file inspected`,
                    other: `${completed} / # files inspected`,
                  })}
                </Text>
                <Button size="xs" variant="default" onClick={() => run(`/scans/${scan.id}/cancel`)}>
                  <Trans>Cancel scan</Trans>
                </Button>
              </Group>
            </Alert>
          )
        })}
      {partialScanError && (
        <Alert color="var(--warn)" mb="lg">
          <Trans>Last partial scan: {partialScanError}</Trans>
        </Alert>
      )}

      <Tabs
        value={tab}
        onChange={(value) => setParams({ tab: value ?? 'overview' })}
        variant="unstyled"
        classNames={{ list: 'series-tabs page-tabs', tab: 'series-tab' }}
      >
        <Tabs.List>
          <Tabs.Tab value="overview">
            <Trans>Overview</Trans>
          </Tabs.Tab>
          <Tabs.Tab value="files">
            <Trans>Files</Trans>
          </Tabs.Tab>
          <Tabs.Tab value="repairs">
            <Trans>Repairs</Trans>
          </Tabs.Tab>
          <Tabs.Tab value="history">
            <Trans>History</Trans>
          </Tabs.Tab>
        </Tabs.List>

        <Tabs.Panel value="overview">
          <Stack gap="xl">
            <ChecksPanel checks={overview.data?.checks ?? []} loading={overview.isPending} run={run} />
            <CachePanel />
          </Stack>
        </Tabs.Panel>

        <Tabs.Panel value="files">
          <Stack>
            <Group className="health-filter-rail">
              <TextInput
                placeholder={t`Search file paths`}
                aria-label={t`Search file paths`}
                value={search}
                onChange={(e) => refilter(() => setSearch(e.currentTarget.value))}
              />
              <Select
                aria-label={t`Filter by root folder`}
                placeholder={t`All roots`}
                clearable
                value={root}
                onChange={(v) => refilter(() => setRoot(v))}
                data={overview.data?.roots.map((r) => ({ value: String(r.id), label: r.path })) ?? []}
              />
              <Select
                aria-label={t`Filter by finding`}
                placeholder={t`All findings`}
                clearable
                value={kind}
                onChange={(v) =>
                  refilter(() => {
                    setKind(v)
                    setFileCase(null)
                  })
                }
                data={Object.entries(FINDING_LABEL)
                  .filter(([value]) => value !== 'ambiguousNames')
                  .map(([value, label]) => ({ value, label: renderLabel(label) }))}
              />
              <Select
                aria-label={t`Filter by state`}
                placeholder={t`All states`}
                clearable
                value={state}
                onChange={(v) => refilter(() => setState(v))}
                data={FINDING_STATES.map((value) => ({ value, label: renderLabel(STATUS_LABEL[value]) }))}
              />
            </Group>

            {selectedCount > 0 && (
              <Panel p="xs" className="health-bulk-bar">
                <Text size="sm" fw={600} className="tnum">
                  <Trans>{selectedCount} selected</Trans>
                </Text>
                <Group gap="xs" wrap="wrap">
                  <Button
                    size="xs"
                    variant="default"
                    {...bulkPending('rescan')}
                    onClick={() => bulk('/scans', { fileIds: ids, force: true })}
                  >
                    <Trans>Rescan</Trans>
                  </Button>
                  <Button
                    size="xs"
                    variant="default"
                    {...bulkPending('verify')}
                    onClick={() => bulk('/scans', { fileIds: ids, force: true, verify: true })}
                  >
                    <Trans>Verify</Trans>
                  </Button>
                  <Button
                    size="xs"
                    variant="default"
                    {...bulkPending('/imports')}
                    onClick={() => bulk('/imports', { fileIds: ids })}
                  >
                    <Trans>Import unlinked</Trans>
                  </Button>
                  {(['acknowledged', 'ignored', 'open'] as const).map((next) => (
                    <Button
                      key={next}
                      size="xs"
                      variant="default"
                      {...bulkPending(next)}
                      onClick={() => bulk('/findings/review', { fileIds: ids, state: next })}
                    >
                      {next === 'open' ? (
                        <Trans>Reopen</Trans>
                      ) : next === 'ignored' ? (
                        <Trans>Ignore</Trans>
                      ) : (
                        <Trans>Acknowledge</Trans>
                      )}
                    </Button>
                  ))}
                  {damagedCount > 0 && (
                    <Button
                      size="xs"
                      variant="default"
                      leftSection={<IconDownload size={16} />}
                      {...bulkPending('/repairs/bulk')}
                      onClick={() =>
                        bulkWithReport(
                          '/repairs/bulk',
                          { files: damaged.map(([fileId, { version }]) => ({ fileId, version })) },
                          (r) => {
                            const requested = r.requested ?? 0
                            const verifying = r.verifying ?? 0
                            return verifying > 0
                              ? t`Requested ${requested}. ${verifying} only indexed so far are being read in full first; request those again once that finishes.`
                              : plural(requested, { one: 'Requested # replacement', other: 'Requested # replacements' })
                          },
                        )
                      }
                    >
                      <Plural value={damagedCount} one="Get a new copy" other="Get # new copies" />
                    </Button>
                  )}
                  {resizedCount > 0 && (
                    <Button
                      size="xs"
                      variant="default"
                      leftSection={<IconRefresh size={16} />}
                      {...bulkPending('/records/refresh')}
                      onClick={() =>
                        bulkWithReport('/records/refresh', { fileIds: resized.map(([id]) => id) }, ({ updated = 0 }) =>
                          plural(updated, { one: 'Updated # record', other: 'Updated # records' }),
                        )
                      }
                    >
                      <Plural value={resizedCount} one="Update the record" other="Update # records" />
                    </Button>
                  )}
                  <Button
                    size="xs"
                    color="var(--danger)"
                    variant="light"
                    leftSection={<IconTrash size={16} />}
                    disabled={deletable.length === 0}
                    onClick={() => setDeleteTargets({ files: deletable, spares: grouped })}
                  >
                    {grouped ? (
                      <Plural value={deletableCount} one="Delete # spare copy" other="Delete # spare copies" />
                    ) : (
                      <Trans>Delete</Trans>
                    )}
                  </Button>
                  <Button size="xs" variant="default" onClick={() => setSelected(new Map())}>
                    <Trans>Clear</Trans>
                  </Button>
                </Group>
              </Panel>
            )}

            {selectedCount === 0 && sparesTotal > 0 && (
              <Panel p="xs" className="health-bulk-bar">
                <Text size="sm">
                  <Plural
                    value={sparesTotal}
                    one="# spare copy is identical to a file that stays, and no chapter reads from it."
                    other="# spare copies are identical to files that stay, and no chapter reads from them."
                  />
                </Text>
                <Button
                  size="xs"
                  color="var(--danger)"
                  variant="light"
                  leftSection={<IconTrash size={16} />}
                  onClick={() => setDeleteTargets({ files: spares!.items, spares: true })}
                >
                  {sparesShown < sparesTotal ? (
                    <Trans>Delete the first {sparesShown}</Trans>
                  ) : (
                    <Plural value={sparesShown} one="Delete the spare copy" other="Delete all # spare copies" />
                  )}
                </Button>
              </Panel>
            )}

            {selectedCount === 0 && moves && (
              <MissingBar
                moves={moves}
                action={action}
                onRelinkAll={() =>
                  bulkWithReport(
                    '/moves/bulk',
                    {
                      items: moves.items.map((m) => ({
                        fileId: m.fileId,
                        version: m.version,
                        targetId: m.targetId,
                        targetVersion: m.targetVersion,
                      })),
                    },
                    ({ relinked = 0 }) => plural(relinked, { one: 'Relinked # file', other: 'Relinked # files' }),
                  )
                }
                onFetch={(items) =>
                  bulkWithReport(
                    '/repairs/bulk',
                    { files: items.map(({ id: fileId, version }) => ({ fileId, version })) },
                    ({ requested = 0 }) => plural(requested, { one: 'Requested # replacement', other: 'Requested # replacements' }),
                  )
                }
                onClear={(items) => setDeleteTargets({ files: items, spares: false })}
              />
            )}

            {cases && (
              <CasesBar
                cases={cases}
                active={fileCase}
                select={(next) => refilter(() => setFileCase(next))}
                pending={action.isPending && action.variables?.path === '/imports'}
                onImportAll={() =>
                  bulkWithReport('/imports', { fileIds: cases.importable.items.map((f) => f.id) }, ({ linked = 0 }) =>
                    plural(linked, { one: 'Imported # archive', other: 'Imported # archives' }),
                  )
                }
              />
            )}

            {report && (
              <Alert
                color="var(--danger)"
                withCloseButton
                closeButtonLabel={t`Dismiss`}
                onClose={() => setReport(null)}
              >
                <Text size="sm" mb="xs">
                  {report.summary}
                </Text>
                {report.failures.map((failure) => (
                  <Text key={failure.id} size="xs" style={{ overflowWrap: 'anywhere' }}>
                    {failure.relativePath}: {failure.message}
                  </Text>
                ))}
              </Alert>
            )}

            {files.isPending ? (
              <TableSkeleton columns={5} />
            ) : (
              <>
                <Panel p={0} className="table-panel">
                  <Table.ScrollContainer minWidth={880}>
                    {/* Fixed, so the archive path takes what the other columns leave and loses its middle. */}
                    <Table highlightOnHover className="panel-table" layout="fixed">
                      <Table.Thead>
                        <Table.Tr>
                          <Table.Th w={40}>
                            <Checkbox
                              aria-label={t`Select every file on this page`}
                              checked={pageIds.length > 0 && pageIds.every((i) => selected.has(i))}
                              indeterminate={
                                pageIds.some((i) => selected.has(i)) && !pageIds.every((i) => selected.has(i))
                              }
                              onChange={(e) => toggle(pageEntries, e.currentTarget.checked)}
                            />
                          </Table.Th>
                          <Table.Th>
                            <Trans>Archive</Trans>
                          </Table.Th>
                          <Table.Th w={90}>
                            <Trans>Size</Trans>
                          </Table.Th>
                          <Table.Th w={120}>
                            <Trans>Analysis</Trans>
                          </Table.Th>
                          <Table.Th w={200}>
                            <Trans>Findings</Trans>
                          </Table.Th>
                          <Table.Th w={100}><VisuallyHidden><Trans>Actions</Trans></VisuallyHidden></Table.Th>
                        </Table.Tr>
                      </Table.Thead>
                      <Table.Tbody>
                        {files.data?.items.map((file) => {
                          const { relativePath } = file
                          const entries = rowEntries(file)
                          const rowSelected = entries.every(([id]) => selected.has(id))
                          const others = grouped ? (file.copies ?? []).filter((c) => c.id !== file.id) : []
                          const foundAt = moves?.rows[String(file.id)]
                          // Only worth a label while the list mixes cases.
                          const fileCaseOf = !fileCase ? file.detail?.case : undefined
                          return (
                          <Table.Tr key={file.id} data-selected={rowSelected || undefined}>
                            <Table.Td>
                              <Checkbox
                                aria-label={
                                  grouped ? t`Select every copy of ${relativePath}` : t`Select ${relativePath}`
                                }
                                checked={rowSelected}
                                indeterminate={!rowSelected && entries.some(([id]) => selected.has(id))}
                                onChange={(e) => toggle(entries, e.currentTarget.checked)}
                              />
                            </Table.Td>
                            <Table.Td>
                              <MonoValue value={file.relativePath} copy fz="var(--type-meta)" />
                              {grouped ? (
                                <ul className="health-copy-list">
                                  {others.length === 0 && (
                                    <li>
                                      <Text size="xs" c="var(--ink-3)">
                                        <Trans>No other copy is left. Verify this file to clear the finding.</Trans>
                                      </Text>
                                    </li>
                                  )}
                                  {others.map((copy) => {
                                    const { relativePath: copyPath } = copy
                                    return (
                                      <li key={copy.id}>
                                        <span className="health-copy-role" data-role={copyRole(copy)}>
                                          {renderLabel(COPY_ROLE[copyRole(copy)])}
                                        </span>
                                        <Anchor
                                          component="button"
                                          type="button"
                                          size="xs"
                                          className="health-copy-link"
                                          aria-label={t`Review ${copyPath}`}
                                          onClick={() => setFileId(copy.id)}
                                        >
                                          {copyPath}
                                        </Anchor>
                                      </li>
                                    )
                                  })}
                                </ul>
                              ) : (
                                <Text size="xs" c="var(--ink-3)">
                                  {file.analyzedAt ? formatDateTime(file.analyzedAt) : <Trans>Not analyzed</Trans>}
                                </Text>
                              )}
                              {foundAt && (
                                <Text size="xs" c="var(--ink-3)" className="health-row-note">
                                  <span className="health-copy-role" data-role={foundAt.evidence === 'nameOnly' ? 'spare' : 'keep'}>
                                    {foundAt.evidence === 'nameOnly' ? <Trans>Possible match</Trans> : <Trans>Found</Trans>}
                                  </span>
                                  <span className="health-row-path">{foundAt.targetPath}</span>
                                </Text>
                              )}
                              {fileCaseOf && (
                                <Text size="xs" c="var(--ink-3)" className="health-row-note">
                                  <span className="health-copy-role">{renderLabel(CASE_LABEL[fileCaseOf])}</span>
                                </Text>
                              )}
                            </Table.Td>
                            <Table.Td>{bytes(file.size, t`Missing`)}</Table.Td>
                            <Table.Td>
                              <Status value={file.status} hint={file.status === 'partial'} />
                            </Table.Td>
                            <Table.Td>
                              <Group gap="xs">
                                {file.findings.map((f) => (
                                  <StatusDot key={f.id} tone={tone(f.severity)}>
                                    {renderLabel(FINDING_LABEL[f.kind] ?? f.kind)}
                                  </StatusDot>
                                ))}
                              </Group>
                            </Table.Td>
                            <Table.Td>
                              <Button size="xs" variant="default" onClick={() => setFileId(file.id)}>
                                <Trans>Review</Trans>
                              </Button>
                            </Table.Td>
                          </Table.Tr>
                          )
                        })}
                      </Table.Tbody>
                    </Table>
                  </Table.ScrollContainer>
                </Panel>
                {files.data?.items.length === 0 && (
                  <Text c="var(--ink-3)">
                    <Trans>No files match these filters. Run a scan to inventory the library.</Trans>
                  </Text>
                )}
                <Pagination
                  value={page}
                  onChange={setPage}
                  total={Math.max(1, Math.ceil((files.data?.total ?? 0) / PAGE_SIZE))}
                />
              </>
            )}
          </Stack>
        </Tabs.Panel>

        <Tabs.Panel value="repairs">
          <Stack>
            {operations.data?.items.map((group) => (
              <OperationGroup key={group.key} group={group} onReview={setOperationId} />
            ))}
            {operations.data?.items.length === 0 && (
              <Text c="var(--ink-3)">
                <Trans>No repairs or deletions requested.</Trans>
              </Text>
            )}
            <Pagination
              value={repairPage}
              onChange={setRepairPage}
              total={Math.max(1, Math.ceil((operations.data?.total ?? 0) / PAGE_SIZE))}
            />
          </Stack>
        </Tabs.Panel>

        <Tabs.Panel value="history">
          <Stack>
            {history.isLoading && <TableSkeleton columns={2} />}
            {!!history.data?.items.length && <HistoryTimeline entries={history.data.items} />}
            {history.data?.items.length === 0 && (
              <Text c="var(--ink-3)">
                <Trans>Nothing in the history yet.</Trans>
              </Text>
            )}
            <Pagination
              value={historyPage}
              onChange={setHistoryPage}
              total={Math.max(1, Math.ceil((history.data?.total ?? 0) / PAGE_SIZE))}
            />
          </Stack>
        </Tabs.Panel>
      </Tabs>

      {fileId != null && (
        <FileReview
          key={fileId}
          id={fileId}
          close={() => setFileId(null)}
          openFile={setFileId}
          openOperation={(id) => {
            setOperationId(id)
            setFileId(null)
          }}
        />
      )}
      {operationId != null && (
        <OperationReview key={operationId} id={operationId} close={() => setOperationId(null)} open={setOperationId} />
      )}

      <BulkDeleteModal
        // A fresh dialog per batch, so a confirmation ticked for one never carries over to the next.
        key={deleteTargets ? deleteTargets.files.map((f) => f.id).join(',') : 'closed'}
        opened={deleteTargets != null}
        close={() => setDeleteTargets(null)}
        files={deleteTargets?.files ?? []}
        spares={deleteTargets?.spares ?? false}
        pending={action.isPending && action.variables?.path === '/deletions/bulk'}
        onConfirm={() =>
          action.mutate(
            {
              path: '/deletions/bulk',
              body: {
                files: (deleteTargets?.files ?? []).map(({ id: fileId, version }) => ({ fileId, version })),
                confirmed: true,
                spares: deleteTargets?.spares ?? false,
              },
            },
            {
              onSuccess: (result) => {
                const outcome = result as unknown as DeleteReport
                const { deleted } = outcome
                const deletedCount = deleted
                const failedCount = outcome.failures.length
                setSelected(new Map())
                setDeleteTargets(null)
                if (failedCount > 0) setReport({ summary: t`${deletedCount} deleted, ${failedCount} refused:`, failures: outcome.failures })
                else if (deleted > 0)
                  notifications.show({
                    color: 'var(--ok)',
                    message: plural(deleted, { one: 'Deleted # archive', other: 'Deleted # archives' }),
                  })
              },
            },
          )
        }
      />
    </SurfaceFrame>
  )
}

/**
 * The Missing filter's two answers above the table: files found again at a new path, relinked in
 * one go, and files gone for good, grouped by folder because a deleted or renamed series folder
 * takes every file in it along.
 */
function MissingBar({
  moves,
  action,
  onRelinkAll,
  onFetch,
  onClear,
}: {
  moves: MissingSummary
  action: ReturnType<typeof useHealthAction>
  onRelinkAll: () => void
  onFetch: (items: FileRef[]) => void
  onClear: (items: FileRef[]) => void
}) {
  const { total, weak, items, gone } = moves
  const shown = items.length
  if (total === 0 && weak === 0 && gone.length === 0) return null
  return (
    <Panel p="sm" className="health-summary">
      {total > 0 && (
        <div className="health-summary-row">
          <div>
            <Text size="sm" fw={600}>
              <Plural value={total} one="# file found at a new path" other="# files found at a new path" />
            </Text>
            <Text size="xs" c="var(--ink-3)">
              <Trans>
                Most likely a folder renamed or reorganised outside Maki. Relinking points the records at the files
                where they are now; nothing on disk moves.
              </Trans>
            </Text>
          </div>
          <Button
            size="xs"
            leftSection={<IconLink size={16} />}
            loading={action.isPending && action.variables?.path === '/moves/bulk'}
            disabled={action.isPending && action.variables?.path !== '/moves/bulk'}
            onClick={onRelinkAll}
          >
            {shown < total ? <Trans>Relink the first {shown}</Trans> : <Plural value={shown} one="Relink it" other="Relink all #" />}
          </Button>
        </div>
      )}
      {weak > 0 && (
        <Text size="xs" c="var(--ink-3)" className="health-summary-row">
          <Plural
            value={weak}
            one="# more matches a file by name only. Open it to compare before relinking."
            other="# more match a file by name only. Open each to compare before relinking."
          />
        </Text>
      )}
      {gone.map((group) => {
        const { folder, count } = group
        return (
          <div key={folder} className="health-summary-row">
            <div className="health-summary-label">
              <MonoValue value={folder || '/'} fz="var(--type-meta)" />
              <Text size="xs" c="var(--ink-3)">
                <Plural value={count} one="# file gone, nowhere else in the library" other="# files gone, nowhere else in the library" />
              </Text>
            </div>
            <Group gap="xs" wrap="nowrap">
              <Button
                size="xs"
                variant="default"
                leftSection={<IconDownload size={16} />}
                disabled={action.isPending}
                onClick={() => onFetch(group.items)}
              >
                <Trans>Download again</Trans>
              </Button>
              <Button size="xs" color="var(--danger)" variant="light" onClick={() => onClear(group.items)}>
                <Trans>Clear records</Trans>
              </Button>
            </Group>
          </div>
        )
      })}
    </Panel>
  )
}

/**
 * The Unlinked filter sorted by what each file needs. The ones that only need importing go in one
 * click; every other case filters the table down to itself and says what to do about it.
 */
function CasesBar({
  cases,
  active,
  select,
  pending,
  onImportAll,
}: {
  cases: UnlinkedSummary
  active: UnlinkedCase | null
  select: (next: UnlinkedCase | null) => void
  pending: boolean
  onImportAll: () => void
}) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const count = (c: UnlinkedCase) => cases.counts.find((x) => x.case === c)?.count ?? 0
  const present = CASE_ORDER.filter((c) => count(c) > 0)
  if (present.length === 0) return null
  const importShown = cases.importable.items.length
  const importTotal = cases.importable.total

  return (
    <Panel p="sm" className="health-summary">
      {present.map((c) => {
        const n = count(c)
        const label = renderLabel(CASE_LABEL[c])
        return (
          <div key={c} className="health-summary-row" data-active={active === c || undefined}>
            <div>
              <Text size="sm" fw={600}>
                <span className="tnum">{formatNumber(n)}</span> {label}
              </Text>
              <Text size="xs" c="var(--ink-3)">
                {renderLabel(CASE_HINT[c])}
              </Text>
              {c === 'outside' && (
                <ul className="health-copy-list">
                  {cases.outside.map((o) => {
                    const { folder, count: inFolder } = o
                    return (
                      <li key={folder}>
                        <MonoValue value={folder} fz="var(--type-meta)" />
                        <Text size="xs" c="var(--ink-4)" className="tnum">
                          {formatNumber(inFolder)}
                        </Text>
                      </li>
                    )
                  })}
                </ul>
              )}
            </div>
            <Group gap="xs" wrap="nowrap">
              {c === 'import' && (
                <Button size="xs" leftSection={<IconFileImport size={16} />} loading={pending} onClick={onImportAll}>
                  {importShown < importTotal ? (
                    <Trans>Import the first {importShown}</Trans>
                  ) : (
                    <Plural value={importShown} one="Import it" other="Import all #" />
                  )}
                </Button>
              )}
              {c === 'outside' && (
                <Button component={Link} to="/import" size="xs" variant="default">
                  <Trans>Import page</Trans>
                </Button>
              )}
              <Button
                size="xs"
                variant={active === c ? 'light' : 'subtle'}
                aria-pressed={active === c}
                aria-label={active === c ? t`Show all unlinked files` : t`Show only: ${label}`}
                onClick={() => select(active === c ? null : c)}
              >
                {active === c ? <Trans>Show all</Trans> : <Trans>Show</Trans>}
              </Button>
            </Group>
          </div>
        )
      })}
    </Panel>
  )
}

/** The audit trail as a timeline, one block per day, newest first as the server pages it. */
function HistoryTimeline({ entries }: { entries: HistoryEntry[] }) {
  const { i18n } = useLingui()
  const renderLabel = useLabel()
  const days = useMemo(() => {
    const startOfDay = (d: Date) => new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime()
    const today = startOfDay(new Date())
    const groups: { key: number; label: string; entries: HistoryEntry[] }[] = []
    for (const entry of entries) {
      const key = startOfDay(new Date(entry.createdAt))
      let group = groups.at(-1)
      if (group?.key !== key) {
        const ago = Math.round((today - key) / 86_400_000)
        const label = ago === 0 ? i18n._(TODAY) : ago === 1 ? i18n._(YESTERDAY) : formatWeekdayDate(entry.createdAt)
        group = { key, label, entries: [] }
        groups.push(group)
      }
      group.entries.push(entry)
    }
    return groups
  }, [entries, i18n])

  return (
    <Panel p={0} className="health-history">
      {days.map((day) => (
        <section key={day.key} className="health-history-day">
          <h3 className="health-history-date">{day.label}</h3>
          <ol className="health-history-rows">
            {day.entries.map((entry) => {
              const change = entry.kind === 'transition' && entry.status ? entry.status : null
              // A status change shows where it ended by shape as well as colour, so a fix and a
              // breakage never differ by hue alone.
              const KindIcon = change
                ? (TRANSITION_ICON[tone(change)] ?? IconStethoscope)
                : (HISTORY_KIND_ICON[entry.kind] ?? IconStethoscope)
              // A clean scan is routine; colour only what changed state or came back partial.
              const routine = !entry.status || (entry.kind === 'scan' && entry.status === 'completed')
              const entryTone = routine ? 'neutral' : tone(entry.status!)
              const previous = entry.from ? renderLabel(STATUS_LABEL[entry.from] ?? entry.from) : null
              return (
                <li key={entry.id} className="health-history-row" data-tone={entryTone}>
                  <span className="health-history-icon" aria-hidden>
                    <KindIcon size={16} />
                  </span>
                  <div className="health-history-body">
                    <Text size="sm" className="health-history-message">
                      {entry.message}
                    </Text>
                    <Text size="xs" c="var(--ink-3)" className="health-history-meta">
                      <span>{renderLabel(HISTORY_KIND_LABEL[entry.kind] ?? entry.kind)}</span>
                      {change && (
                        <>
                          <span className="health-history-status">{renderLabel(STATUS_LABEL[change] ?? change)}</span>
                          {previous && (
                            <span>
                              <Trans>from {previous}</Trans>
                            </span>
                          )}
                        </>
                      )}
                    </Text>
                  </div>
                  <time dateTime={entry.createdAt} title={formatDateTime(entry.createdAt)} className="health-history-time">
                    {formatTime(entry.createdAt)}
                  </time>
                </li>
              )
            })}
          </ol>
        </section>
      ))}
    </Panel>
  )
}

/** A selected file as it read when it was ticked; the version is what a bulk delete tells the server was reviewed. */
interface Picked {
  version: string
  relativePath: string
  spare: boolean
  /** Open finding kinds, so the bulk bar offers only what applies to the selection. */
  kinds: string[]
}

/** What the bulk endpoints answer; each fills the counts it has. */
interface BulkResult {
  deleted?: number
  relinked?: number
  requested?: number
  verifying?: number
  updated?: number
  linked?: number
  failures?: DeleteReport['failures']
}

interface DeleteReport {
  deleted: number
  failures: { id: number; relativePath: string; message: string }[]
}

/**
 * One confirmation for a whole batch of deletions.
 *
 * The single-file flow spends a review screen and a checkbox on this, and a batch has no less at
 * stake, so the count and the paths are put in front of the user before the checkbox unlocks the
 * button. The archives go; chapter records, Wanted flags and reading history do not.
 */
function BulkDeleteModal({
  opened,
  close,
  files,
  spares,
  pending,
  onConfirm,
}: {
  opened: boolean
  close: () => void
  files: FileRef[]
  /** Every file is a spare copy of one that stays, so no chapter loses anything. */
  spares: boolean
  pending: boolean
  onConfirm: () => void
}) {
  const { i18n } = useLingui()
  const [confirmed, setConfirmed] = useState(false)
  const count = files.length
  const dismiss = () => {
    setConfirmed(false)
    close()
  }

  // `plural` is the core macro, which reads the catalogue when it runs rather than subscribing.
  // `i18n.locale` in the deps is what makes these follow a language switch.
  const [title, confirmLabel] = useMemo(
    () => [
      spares
        ? plural(count, { one: 'Delete # spare copy?', other: 'Delete # spare copies?' })
        : plural(count, { one: 'Delete # archive?', other: 'Delete # archives?' }),
      plural(count, {
        one: 'I confirm permanent deletion of # archive.',
        other: 'I confirm permanent deletion of # archives.',
      }),
    ],
    [count, spares, i18n.locale],
  )

  return (
    <Modal
      opened={opened}
      onClose={dismiss}
      title={title}
      centered
      size="lg"
      attributes={{ content: spares ? undefined : { 'data-edge': 'danger' } }}
    >
      <Stack gap="md">
        {spares ? (
          <Alert color="var(--info)">
            <Trans>
              Each of these is byte for byte the same as another file that stays in the library, and no chapter
              reads from it. Deleting them frees the space and clears the duplicate findings. Nothing else changes.
            </Trans>
          </Alert>
        ) : (
          <Alert color="var(--danger)">
            <Trans>
              This permanently deletes <Plural value={count} one="the file" other="these files" /> from disk.
              Chapter records, Wanted flags and reading history are kept, so anything still wanted can be
              downloaded again. A file with an identical copy in the same series folder hands its chapters to that
              copy instead. Archives already missing from disk have nothing to delete: for those this only clears
              the record that still says their chapters are downloaded.
            </Trans>
          </Alert>
        )}
        <Panel p="sm">
          <Stack gap={4}>
            {files.map((file) => (
              <Text key={file.id} size="xs" c="var(--ink-3)" style={{ overflowWrap: 'anywhere' }}>
                {file.relativePath}
              </Text>
            ))}
          </Stack>
        </Panel>
        {!spares && (
          <Checkbox
            checked={confirmed}
            onChange={(e) => setConfirmed(e.currentTarget.checked)}
            label={confirmLabel}
          />
        )}
        <Group justify="flex-end">
          <Button variant="default" onClick={dismiss}>
            <Trans>Cancel</Trans>
          </Button>
          <Button color="var(--danger-fill)" disabled={!spares && !confirmed} loading={pending} onClick={onConfirm}>
            {spares ? (
              <Plural value={count} one="Delete spare copy" other="Delete # spare copies" />
            ) : (
              <Trans>Delete permanently</Trans>
            )}
          </Button>
        </Group>
      </Stack>
    </Modal>
  )
}

/**
 * Every check the monitor produced, grouped by the area it came from.
 *
 * Passing checks are hidden by default and not because they are uninteresting: there is one per
 * root folder, per probed service and per scheduled job, so a healthy instance shows a screenful of
 * green rows and the ones that matter are lost in them.
 */
function ChecksPanel({
  checks,
  loading,
  run,
}: {
  checks: HealthCheck[]
  loading: boolean
  run: (path: string, body?: object) => void
}) {
  const { t } = useLingui()
  const [showPassing, setShowPassing] = useState(false)
  const visible = showPassing ? checks : checks.filter((c) => ISSUE.includes(c.status))
  const categories = Array.from(new Set(visible.map((c) => c.category)))
  const totalChecks = checks.length
  // Shown only while something needs attention; a healthy system is the plain panel.
  const renderLabel = useLabel()
  const unresolved = checks.filter((c) => ISSUE.includes(c.status) && !c.acknowledged)
  const severity = unresolved.some((c) => tone(c.status) === 'danger') ? 'danger' : 'warn'

  return (
    <Stack gap="sm">
      <SectionHeader
        icon={IconStethoscope}
        title={t`System checks`}
        count={totalChecks > 0 ? totalChecks : undefined}
        action={
          <Group gap="md" wrap="nowrap">
            {unresolved.length > 0 && (
              <StatusDot tone={severity}>
                <Plural value={unresolved.length} one="# needs attention" other="# need attention" />
              </StatusDot>
            )}
            <Switch
              size="xs"
              label={t`Show passing`}
              checked={showPassing}
              onChange={(e) => setShowPassing(e.currentTarget.checked)}
            />
          </Group>
        }
      />
    <Panel p="lg">

      {loading && (
        <Stack gap="md" aria-hidden>
          <Skeleton h={8} w={96} />
          {[0, 1, 2].map((i) => (
            <Group key={i} gap="lg" wrap="nowrap" align="flex-start">
              <Skeleton h={10} w={64} />
              <Stack gap={8} style={{ flex: 1 }}>
                <Skeleton h={10} w={`${78 - i * 14}%`} />
                <Skeleton h={8} w="36%" />
              </Stack>
            </Group>
          ))}
        </Stack>
      )}
      {!loading && checks.length === 0 && (
        <Alert>
          <Trans>No checks have run yet. Use Check now.</Trans>
        </Alert>
      )}
      {checks.length > 0 && visible.length === 0 && (
        <EmptyState
          compact
          mood="pleased"
          title={t`Everything is passing`}
          description={plural(totalChecks, {
            one: 'Turn on Show passing to see the # check.',
            other: 'Turn on Show passing to see all # checks.',
          })}
        />
      )}

      {categories.map((category) => {
        const CategoryIcon = CATEGORY_ICON[category] ?? IconServer
        const rows = visible
          .filter((c) => c.category === category)
          // Live issues, then acknowledged ones, then passing: turning on Show passing must never
          // bury what still needs a decision, and neither must a row someone already settled.
          .sort((a, b) => weight(a) - weight(b))
        return (
          <div className="health-group" key={category}>
            <Group gap={7} className="health-group-label" wrap="nowrap">
              <CategoryIcon size={16} />
              <span>{CATEGORY_LABEL[category] ? renderLabel(CATEGORY_LABEL[category]) : category}</span>
              <Text span size="xs" c="var(--ink-4)" fw={500} className="tnum">
                {rows.length}
              </Text>
            </Group>
            {collapseByType(rows).map((item) =>
              Array.isArray(item) ? (
                <CheckGroup key={checkType(item[0].id)} checks={item} run={run} />
              ) : (
                <CheckRow key={item.id} check={item} run={run} />
              ),
            )}
          </div>
        )
      })}
    </Panel>
    </Stack>
  )
}

/**
 * Checks that repeat once per series or per source. Past `COLLAPSE_AT` live issues of one type they
 * fold into a single row: eleven "monitored but has no enabled sources" rows read as eleven
 * problems and took eleven clicks to acknowledge. The prefix is the check type; the rest of the id
 * is the series, mapping or source it is about.
 */
const COLLAPSIBLE = ['legacy:series:', 'legacy:mapping:', 'legacy:source:', 'suwayomi:missing:'] as const
const COLLAPSE_AT = 3

function checkType(id: string): string | null {
  return COLLAPSIBLE.find((prefix) => id.startsWith(prefix)) ?? null
}

/** The rows in order, with each collapsible type's live issues gathered where its first one stood. */
function collapseByType(rows: HealthCheck[]): (HealthCheck | HealthCheck[])[] {
  const issues = new Map<string, HealthCheck[]>()
  for (const row of rows) {
    const type = checkType(row.id)
    if (type && ISSUE.includes(row.status)) issues.set(type, [...(issues.get(type) ?? []), row])
  }
  const out: (HealthCheck | HealthCheck[])[] = []
  for (const row of rows) {
    const type = checkType(row.id)
    const group = type && ISSUE.includes(row.status) ? issues.get(type) : undefined
    if (!group || group.length < COLLAPSE_AT) out.push(row)
    else if (group[0] === row) out.push(group)
  }
  return out
}

function CheckGroup({ checks, run }: { checks: HealthCheck[]; run: (path: string, body?: object) => void }) {
  const [open, setOpen] = useState(false)
  const count = checks.length
  const type = checkType(checks[0].id)
  const unsettled = checks.filter((c) => !c.acknowledged)
  const acknowledged = count - unsettled.length
  const worst = checks.find((c) => tone(c.status) === 'danger') ?? checks[0]
  const latest = checks.reduce((a, b) => (a.checkedAt > b.checkedAt ? a : b)).checkedAt
  const headline =
    type === 'legacy:series:' ? (
      <Plural
        value={count}
        one="# monitored series has no enabled sources"
        other="# monitored series have no enabled sources"
      />
    ) : type === 'legacy:mapping:' ? (
      <Plural
        value={count}
        one="# series is failing to refresh from its source"
        other="# series are failing to refresh from their sources"
      />
    ) : type === 'legacy:source:' ? (
      <Plural value={count} one="# source looks unstable" other="# sources look unstable" />
    ) : (
      <Plural value={count} one="# source is no longer installed in Suwayomi" other="# sources are no longer installed in Suwayomi" />
    )

  return (
    <div className="health-check-group">
      <div className="health-check" data-acknowledged={unsettled.length === 0 || undefined}>
        <Status value={worst.status} />
        <div style={{ minWidth: 0 }}>
          <Text size="sm" c={unsettled.length === 0 ? 'var(--ink-3)' : 'var(--ink-2)'}>
            {headline}
          </Text>
          <Text component="div" size="xs" c="var(--ink-4)" mt={2}>
            <MetaList
              items={[
                formatDateTime(latest),
                acknowledged > 0 && <Plural value={acknowledged} one="# acknowledged" other="# acknowledged" />,
              ]}
            />
          </Text>
          <Button
            size="compact-xs"
            variant="subtle"
            mt={4}
            px={0}
            aria-expanded={open}
            rightSection={<IconChevronDown size={12} style={{ transform: open ? 'rotate(180deg)' : undefined }} />}
            onClick={() => setOpen((o) => !o)}
          >
            {open ? <Trans>Hide the list</Trans> : <Plural value={count} one="Show # check" other="Show all # checks" />}
          </Button>
        </div>
        <div className="health-check-actions">
          <Button
            size="xs"
            variant="default"
            onClick={() =>
              run('/checks/acknowledge/bulk', {
                ids: (unsettled.length > 0 ? unsettled : checks).map((c) => c.id),
                acknowledged: unsettled.length > 0,
              })
            }
          >
            {unsettled.length > 0 ? <Trans>Acknowledge all</Trans> : <Trans>Reopen all</Trans>}
          </Button>
        </div>
      </div>
      {open && (
        <div className="health-check-group-body">
          {checks.map((check) => (
            <CheckRow key={check.id} check={check} run={run} />
          ))}
        </div>
      )}
    </div>
  )
}

/** Id prefix of HealthCheckService's grouped warning for a source failing on several series at once. */
const SOURCE_CHECK = 'legacy:source:'

function CheckRow({ check, run }: { check: HealthCheck; run: (path: string, body?: object) => void }) {
  const [open, setOpen] = useState(false)
  const source =
    check.id.startsWith(SOURCE_CHECK) && ISSUE.includes(check.status) ? check.id.slice(SOURCE_CHECK.length) : null
  const failures = useHealthData<SourceFailures>(`/sources/${source}`, source !== null, (data) =>
    data?.refreshing ? 3000 : false,
  )
  const affected = failures.data?.series ?? []
  const count = affected.length
  const done = failures.data?.done ?? 0
  const total = failures.data?.total ?? 0

  return (
    <div className="health-check" data-acknowledged={check.acknowledged || undefined}>
      <Status value={check.status} />
      <div style={{ minWidth: 0 }}>
        <Text size="sm" c={check.acknowledged ? 'var(--ink-3)' : 'var(--ink-2)'}>
          {check.message}
        </Text>
        <Text component="div" size="xs" c="var(--ink-4)" mt={2}>
          <MetaList
            items={[
              formatDateTime(check.checkedAt),
              check.acknowledged && <Trans>Acknowledged, hidden from the header badge</Trans>,
            ]}
          />
        </Text>
        {source !== null && count > 0 && (
          <Button
            size="compact-xs"
            variant="subtle"
            mt={4}
            px={0}
            rightSection={
              <IconChevronDown size={12} style={{ transform: open ? 'rotate(180deg)' : undefined }} />
            }
            onClick={() => setOpen((o) => !o)}
          >
            {open ? (
              <Trans>Hide affected series</Trans>
            ) : (
              <Plural value={count} one="Show # affected series" other="Show # affected series" />
            )}
          </Button>
        )}
      </div>
      <div className="health-check-actions">
        {source !== null && (
          <Button
            size="xs"
            variant="light"
            leftSection={<IconRefresh size={16} />}
            disabled={failures.data?.refreshing || count === 0}
            onClick={() => run(`/sources/${source}/refresh`)}
          >
            {!failures.data?.refreshing ? (
              <Trans>Refresh all</Trans>
            ) : total > 0 ? (
              <Trans>
                Refreshing {done} of {total}
              </Trans>
            ) : (
              <Trans>Refreshing</Trans>
            )}
          </Button>
        )}
        {check.url && (
          <Button component={Link} to={check.url} size="xs" variant="default">
            <Trans>Open</Trans>
          </Button>
        )}
        {ISSUE.includes(check.status) && (
          <Button
            size="xs"
            variant="default"
            onClick={() => run('/checks/acknowledge', { id: check.id, acknowledged: !check.acknowledged })}
          >
            {check.acknowledged ? <Trans>Reopen</Trans> : <Trans>Acknowledge</Trans>}
          </Button>
        )}
      </div>
      {source !== null && open && (
        <div className="health-source-series">
          {affected.map((m) => (
            <div key={m.id}>
              <Anchor component={Link} to={`/series/${m.seriesId}`} size="sm">
                {m.title}
              </Anchor>
              <Text size="xs" c="var(--ink-4)" lineClamp={2}>
                {m.error}
              </Text>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

function FileReview({
  id,
  close,
  openFile,
  openOperation,
}: {
  id: number
  close: () => void
  openFile: (id: number) => void
  openOperation: (id: number) => void
}) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const { data, error } = useHealthData<FileDetail>(`/files/${id}`)
  const action = useHealthAction()
  // Automatic by default: the reviewer usually wants "get me a good copy", and picking a source by
  // hand also turns off the fallback, which is rarely what they meant.
  const [mapping, setMapping] = useState<string>(AUTOMATIC)
  // A negative size is how the scanner records "the file was not there".
  const gone = (data?.file.size ?? 0) < 0
  const pageCount = data?.analysis.pages.length ?? 0
  const sourceNames = data?.mappings.map((m) => m.sourceName).join(', ') || t`the series' sources`
  const duplicate = (data?.copies.length ?? 0) > 1
  const lead = data ? leadPanel(data) : null
  // A lead panel already answers what to do; picking a source by hand or deleting through a review
  // is the exception, so it waits behind a toggle.
  const [moreOpen, setMoreOpen] = useState(false)
  const showActions = !lead || moreOpen
  // When the file it would compete with is this file's own twin, the unlinked comparison only
  // repeats the duplicate panel.
  const counterparts = data?.match?.counterparts ?? []
  const twinOnly =
    duplicate && counterparts.length > 0 && counterparts.every((c) => c.contentHash === data?.file.contentHash)
  const span = data ? chapterSpan(data.chapters) : null

  return (
    <Modal
      opened
      onClose={close}
      title={t`Review archive`}
      size="min(1060px, 94vw)"
      centered
    >
      <Stack gap="lg">
        {(error ?? action.error) && <Alert color="var(--danger)">{(error ?? action.error)?.message}</Alert>}
        {!data ? (
          <Loader />
        ) : (
          <>
          {lead === 'duplicate' && <DuplicatePanel detail={data} openFile={openFile} close={close} />}
          {lead === 'missing' && (
            <MissingPanel detail={data} openFile={openFile} openOperation={openOperation} close={close} />
          )}
          {lead === 'damaged' && <DamagedPanel detail={data} openOperation={openOperation} />}
          {lead === 'size' && <SizePanel detail={data} />}
          {lead === 'unreadable' && <UnreadablePanel detail={data} />}
          {data.match && !twinOnly && (
            <UnlinkedPanel
              file={data.file}
              analysis={data.analysis}
              match={data.match}
              action={action}
              openFile={openFile}
            />
          )}
          <div className="health-review">
            <div className="health-review-column">
              <Panel p="md">
                <Text fw={600} component="div">
                  <MonoValue value={data.file.relativePath} copy />
                </Text>
                <Text component="div" size="sm" c="var(--ink-3)" mt={4}>
                  <MetaList
                    items={[
                      bytes(data.file.size, t`Missing`),
                      <Plural key="pages" value={pageCount} one="# page" other="# pages" />,
                      data.analysis.status,
                      data.analysis.verified ? <Trans key="read">contents read</Trans> : <Trans key="read">indexed only</Trans>,
                    ]}
                  />
                </Text>
                <Text size="xs" c="var(--ink-4)" mt={4} style={{ overflowWrap: 'anywhere' }}>
                  SHA-256: {data.file.contentHash ?? <Trans>Unavailable</Trans>}
                </Text>
                {data.analysis.problems
                  .filter((p) => p.kind === 'incomplete')
                  .map((p, i) => (
                    <Text key={i} size="xs" c="var(--info)" mt={4}>
                      {p.message}
                    </Text>
                  ))}
                <Text size="sm" c="var(--ink-3)" mt="sm">
                  <Trans>Affected chapters:</Trans>{' '}
                  {span ?? (data.chapters.map((c) => c.title ?? c.id).join(', ') || <Trans>None linked</Trans>)}
                </Text>
                <Divider my="md" color="var(--hairline)" />
                <Group gap="xs">
                  {data.file.seriesId && (
                    <Button component={Link} to={`/series/${data.file.seriesId}`} size="xs" variant="default">
                      <Trans>Open series</Trans>
                    </Button>
                  )}
                  {!data.file.chapterFileId && (
                    <Button component={Link} to="/import" size="xs" variant="default">
                      <Trans>Import archive</Trans>
                    </Button>
                  )}
                  <Button
                    size="xs"
                    variant="default"
                    onClick={() => action.mutate({ path: '/scans', body: { fileIds: [id], force: true } })}
                  >
                    <Trans>Rescan</Trans>
                  </Button>
                  {!data.analysis.verified && (
                    <Button
                      size="xs"
                      variant="default"
                      leftSection={<IconPhotoScan size={16} />}
                      onClick={() =>
                        action.mutate({ path: '/scans', body: { fileIds: [id], force: true, verify: true } })
                      }
                    >
                      <Trans>Verify this file</Trans>
                    </Button>
                  )}
                </Group>
              </Panel>

              {data.findings.map((f) => (
                <Panel key={f.id} p="md">
                  <Group gap="sm" align="flex-start" wrap="nowrap">
                    <Status value={f.severity} />
                    <Text size="sm">{f.message}</Text>
                  </Group>
                  <Group mt="sm" gap="xs">
                    <Text size="xs" c="var(--ink-4)">
                      {renderLabel(STATUS_LABEL[f.state] ?? f.state)}
                    </Text>
                    {['acknowledged', 'ignored', 'open']
                      .filter((s) => s !== f.state)
                      .map((state) => (
                        <Button
                          key={state}
                          size="xs"
                          variant="default"
                          onClick={() =>
                            action.mutate({ path: `/findings/${f.id}`, method: 'PUT', body: { version: f.version, state } })
                          }
                        >
                          {state === 'open' ? (
                            <Trans>Reopen</Trans>
                          ) : state === 'ignored' ? (
                            <Trans>Ignore this version</Trans>
                          ) : (
                            <Trans>Acknowledge</Trans>
                          )}
                        </Button>
                      ))}
                  </Group>
                </Panel>
              ))}

              {!data.analysis.verified && (
                <Alert color="var(--neutral)">
                  <Trans>Not verified yet. Replacing or deleting this file needs a verify first.</Trans>
                </Alert>
              )}
            </div>

            <div className="health-review-column">
              {lead && (
                <Panel p="md">
                  <Button
                    variant="subtle"
                    size="xs"
                    px={0}
                    aria-expanded={moreOpen}
                    rightSection={
                      <IconChevronDown size={16} style={{ transform: moreOpen ? 'rotate(180deg)' : undefined }} />
                    }
                    onClick={() => setMoreOpen((open) => !open)}
                  >
                    <Trans>Other actions</Trans>
                  </Button>
                  <Text size="xs" c="var(--ink-4)" mt={4}>
                    <Trans>
                      Pick the source a replacement comes from, or delete the file through a review.
                    </Trans>
                  </Text>
                </Panel>
              )}
              {showActions && (
              <>
              <Panel p="md">
                <Title order={3} size="h4" fz="var(--type-subhead)" mb="sm">
                  <Trans>Request replacement</Trans>
                </Title>
                <Select
                  aria-label={t`Request replacement`}
                  allowDeselect={false}
                  value={mapping}
                  onChange={(value) => setMapping(value ?? AUTOMATIC)}
                  data={[
                    { value: AUTOMATIC, label: t`Automatic (source priority)` },
                    ...data.mappings.map((m) => {
                      const { sourceName, priority } = m
                      return { value: String(m.id), label: t`${sourceName} (priority ${priority})` }
                    }),
                  ]}
                />
                <Button
                  mt="sm"
                  fullWidth
                  disabled={data.chapters.length === 0 || data.mappings.length === 0}
                  loading={action.isPending && action.variables?.path === '/repairs'}
                  onClick={() =>
                    action.mutate(
                      {
                        path: '/repairs',
                        body: {
                          fileId: id,
                          version: data.file.version,
                          sourceMappingId: mapping === AUTOMATIC ? null : Number(mapping),
                        },
                      },
                      { onSuccess: (op) => openOperation(op.id) },
                    )
                  }
                >
                  <Trans>Download candidate for review</Trans>
                </Button>
                <Text size="xs" c="var(--ink-4)" mt="sm">
                  {mapping === AUTOMATIC ? (
                    <Trans>
                      Tries {sourceNames} in priority order and takes the first that has the chapter. Naming a
                      source instead pins it: no fallback, so you only ever get a candidate from where you
                      chose.
                    </Trans>
                  ) : (
                    <Trans>
                      Only this source is tried. Nothing falls back to another one, so the request fails rather
                      than fetching from somewhere you did not pick.
                    </Trans>
                  )}
                </Text>
                <Text size="xs" c="var(--ink-4)" mt="xs">
                  <Trans>
                    All chapters sharing this archive must have a candidate. The original stays in place until
                    you approve application.
                  </Trans>
                </Text>
              </Panel>

              <Panel p="md">
                <Title order={3} size="h4" fz="var(--type-subhead)" mb="sm">
                  {gone ? <Trans>Clear the record</Trans> : <Trans>Remove archive</Trans>}
                </Title>
                <Text size="xs" c="var(--ink-4)" mb="sm">
                  {gone ? (
                    <Trans>
                      The file is already off the disk, so nothing is deleted. This drops the record that still
                      says the chapters are downloaded, which is why they read as available on the series page.
                    </Trans>
                  ) : (
                    <Trans>Opens a deletion review. Nothing is removed until you confirm it there.</Trans>
                  )}
                </Text>
                <Button
                  color="var(--danger)"
                  variant="light"
                  fullWidth
                  leftSection={<IconTrash size={16} />}
                  loading={action.isPending && action.variables?.path === '/deletions/preview'}
                  onClick={() =>
                    action.mutate(
                      { path: '/deletions/preview', body: { fileId: id, version: data.file.version } },
                      { onSuccess: (op) => openOperation(op.id) },
                    )
                  }
                >
                  {gone ? <Trans>Review record removal</Trans> : <Trans>Review permanent deletion</Trans>}
                </Button>
              </Panel>
              </>
              )}
            </div>
          </div>
          </>
        )}
      </Stack>
    </Modal>
  )
}

/** Finding kinds a new copy fixes: the archive's own bytes are wrong. Mirrors HealthController.Damage. */
const DAMAGE = ['corrupt', 'damagedImage', 'empty', 'noPages']

type Lead = 'duplicate' | 'missing' | 'damaged' | 'size' | 'unreadable'

/** Which answer the review opens with: the most pressing finding that has one. */
function leadPanel(detail: FileDetail): Lead | null {
  const open = new Set(detail.findings.filter((f) => f.state !== 'resolved').map((f) => f.kind))
  if (detail.copies.length > 1) return 'duplicate'
  if (detail.file.size < 0 && detail.file.chapterFileId) return 'missing'
  if (DAMAGE.some((k) => open.has(k))) return 'damaged'
  if (open.has('sizeMismatch')) return 'size'
  if (open.has('unreadable')) return 'unreadable'
  return null
}

const isPdf = (path: string) => path.toLowerCase().endsWith('.pdf')

/** The confirmation that takes a button's place, so nothing needs a second dialog. */
function InlineConfirm({
  children,
  confirmLabel,
  danger = true,
  pending,
  onConfirm,
  onCancel,
}: {
  children: ReactNode
  confirmLabel: string
  danger?: boolean
  pending: boolean
  onConfirm: () => void
  onCancel: () => void
}) {
  return (
    <div className="health-confirm" data-tone={danger ? undefined : 'neutral'}>
      <Text size="sm">{children}</Text>
      <Group gap="xs">
        <Button
          color={danger ? 'var(--danger-fill)' : undefined}
          size="xs"
          leftSection={danger ? <IconTrash size={16} /> : undefined}
          loading={pending}
          onClick={onConfirm}
        >
          {confirmLabel}
        </Button>
        <Button variant="default" size="xs" disabled={pending} onClick={onCancel}>
          <Trans>Cancel</Trans>
        </Button>
      </Group>
    </div>
  )
}

const MOVE_EVIDENCE: Record<MoveEvidence, MessageDescriptor> = {
  identical: msg`Same bytes as before`,
  samePages: msg`Same pages; only the metadata changed`,
  likely: msg`Same page count and nearly the same size`,
  nameOnly: msg`Only the file name matches`,
}

/**
 * A file chapters read from is gone. First: did it just move? A renamed or reorganised folder
 * leaves the same file under a new path, and relinking keeps everything. Then the two ways to let
 * it go: fetch it again (a replacement, so the chapters keep their record until the new copy is in)
 * or clear the record, saying plainly what that does to Wanted chapters.
 */
function MissingPanel({
  detail,
  openFile,
  openOperation,
  close,
}: {
  detail: FileDetail
  openFile: (id: number) => void
  openOperation: (id: number) => void
  close: () => void
}) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const action = useHealthAction()
  const [confirming, setConfirming] = useState<'relink' | 'clear' | null>(null)
  const { file, moved, chapters, mappings } = detail
  const span = chapterSpan(chapters) ?? plural(chapters.length, { one: '# chapter', other: '# chapters' })
  const wanted = chapters.some((c) => c.wanted)
  const targetPath = moved?.targetPath ?? ''
  const weak = moved?.evidence === 'nameOnly'
  const canFetch = mappings.length > 0 && chapters.length > 0 && !isPdf(file.relativePath)

  const relink = () =>
    moved &&
    action.mutate(
      {
        path: '/moves',
        body: { fileId: file.id, version: file.version, targetId: moved.targetId, targetVersion: moved.targetVersion, allowWeak: weak },
      },
      {
        onSuccess: () => {
          notifications.show({ color: 'var(--ok)', message: t`Relinked. ${span} read from ${targetPath} again.` })
          close()
        },
      },
    )
  const clear = () =>
    action.mutate(
      { path: '/deletions', body: { fileId: file.id, version: file.version, confirmed: true } },
      {
        onSuccess: () => {
          notifications.show({ color: 'var(--ok)', message: t`Cleared the record` })
          close()
        },
      },
    )

  return (
    <Panel p="md">
      <Group justify="space-between" align="center" wrap="nowrap" mb="sm">
        <Title order={3} size="h4" fz="var(--type-subhead)">
          <Trans>File is gone from disk</Trans>
        </Title>
        <Badge variant="light" color="var(--neutral)">
          {span}
        </Badge>
      </Group>
      <Text size="sm" c="var(--ink-3)" mb="md">
        <Trans>
          {span} still read from this file, so they show as downloaded but cannot be opened.
        </Trans>
      </Text>

      {moved && (
        <div className="health-copy" data-this>
          <div className="health-copy-head">
            <span className="health-copy-role" data-role={weak ? 'spare' : 'keep'}>
              {weak ? <Trans>Possible match</Trans> : <Trans>Found</Trans>}
            </span>
            <MonoValue value={moved.targetPath} copy fz="var(--type-meta)" />
            <Button size="compact-xs" variant="subtle" onClick={() => openFile(moved.targetId)}>
              <Trans>Open</Trans>
            </Button>
          </div>
          <Text component="div" size="xs" c="var(--ink-3)">
            <MetaList
              items={[
                renderLabel(MOVE_EVIDENCE[moved.evidence]),
                <Plural key="pages" value={moved.targetPages} one="# page" other="# pages" />,
                bytes(moved.targetSize, t`Missing`),
              ]}
            />
          </Text>
        </div>
      )}

      <div className="health-copy-actions">
        {confirming === 'relink' ? (
          <InlineConfirm
            danger={false}
            confirmLabel={t`Relink`}
            pending={action.isPending}
            onConfirm={relink}
            onCancel={() => setConfirming(null)}
          >
            <Trans>
              Only the name matches, so open the file and check it is the same chapter first. Relinking points{' '}
              {span} at {targetPath}.
            </Trans>
          </InlineConfirm>
        ) : confirming === 'clear' ? (
          <InlineConfirm
            confirmLabel={t`Clear the record`}
            pending={action.isPending}
            onConfirm={clear}
            onCancel={() => setConfirming(null)}
          >
            {wanted ? (
              <Trans>
                {span} will show as not downloaded. They are still wanted, so automation may download them again.
              </Trans>
            ) : (
              <Trans>{span} will show as not downloaded. Nothing downloads them again on its own.</Trans>
            )}
          </InlineConfirm>
        ) : (
          <Group gap="xs">
            {moved && (
              <Button
                leftSection={<IconLink size={16} />}
                loading={action.isPending && action.variables?.path === '/moves'}
                onClick={() => (weak ? setConfirming('relink') : relink())}
              >
                <Trans>Relink to this file</Trans>
              </Button>
            )}
            <Button
              variant={moved ? 'default' : 'filled'}
              leftSection={<IconDownload size={16} />}
              disabled={!canFetch}
              loading={action.isPending && action.variables?.path === '/repairs'}
              onClick={() =>
                action.mutate(
                  { path: '/repairs', body: { fileId: file.id, version: file.version, sourceMappingId: null } },
                  { onSuccess: (op) => openOperation(op.id) },
                )
              }
            >
              <Trans>Download it again</Trans>
            </Button>
            <Button color="var(--danger)" variant="light" onClick={() => setConfirming('clear')}>
              <Trans>Clear the record</Trans>
            </Button>
          </Group>
        )}
      </div>
      {!canFetch && !confirming && (
        <Text size="xs" c="var(--ink-4)" mt="xs">
          {isPdf(file.relativePath) ? (
            <Trans>A PDF cannot be fetched from a source again.</Trans>
          ) : (
            <Trans>No source is enabled for this series, so there is nowhere to download it from.</Trans>
          )}
        </Text>
      )}
      {action.error && (
        <Alert color="var(--danger)" mt="md">
          {action.error.message}
        </Alert>
      )}
    </Panel>
  )
}

/**
 * The archive's own bytes are wrong, and a fresh copy is the fix. A file only indexed so far is
 * read in full first (the server needs its hash to replace it), and if that read finds nothing
 * wrong after all, no download is started.
 */
function DamagedPanel({ detail, openOperation }: { detail: FileDetail; openOperation: (id: number) => void }) {
  const { t } = useLingui()
  const action = useHealthAction()
  const { file, findings, chapters, mappings, analysis } = detail
  const span = chapterSpan(chapters)
  const problems = findings.filter((f) => f.state !== 'resolved' && DAMAGE.includes(f.kind))
  const pdf = isPdf(file.relativePath)
  const canFetch = mappings.length > 0 && chapters.length > 0 && !pdf

  return (
    <Panel p="md">
      <Group justify="space-between" align="center" wrap="nowrap" mb="sm">
        <Title order={3} size="h4" fz="var(--type-subhead)">
          <Trans>Archive is damaged</Trans>
        </Title>
        {span && (
          <Badge variant="light" color="var(--neutral)">
            {span}
          </Badge>
        )}
      </Group>
      <Stack gap={4} mb="md">
        {problems.map((p) => (
          <Group key={p.id} gap="sm" wrap="nowrap" align="flex-start">
            <Status value={p.severity} />
            <Text size="sm">{p.message}</Text>
          </Group>
        ))}
      </Stack>
      {pdf ? (
        <Text size="sm" c="var(--ink-3)">
          <Trans>
            A PDF was placed as it is, so there is no source to fetch it from again. Replace the file by hand, or
            delete it under Other actions.
          </Trans>
        </Text>
      ) : chapters.length === 0 ? (
        <Text size="sm" c="var(--ink-3)">
          <Trans>No chapter reads from this file, so there is nothing to download in its place.</Trans>
        </Text>
      ) : (
        <>
          <Text size="sm" c="var(--ink-3)" mb="sm">
            {analysis.verified ? (
              <Trans>
                A new copy is downloaded from the series' sources and shown to you before it replaces this one.
              </Trans>
            ) : (
              <Trans>
                This file has only been indexed. It is read in full first, and a new copy is only downloaded if that
                read confirms the damage.
              </Trans>
            )}
          </Text>
          <Button
            leftSection={<IconDownload size={16} />}
            disabled={!canFetch}
            loading={action.isPending}
            onClick={() =>
              action.mutate(
                { path: '/repairs/auto', body: { fileId: file.id, version: file.version } },
                {
                  onSuccess: (result) => {
                    const answer = result as unknown as { state: 'clean' | 'requested'; operation: HealthOperation | null }
                    if (answer.operation) openOperation(answer.operation.id)
                    else
                      notifications.show({
                        color: 'var(--ok)',
                        message: t`Read every page: nothing is wrong with this file after all.`,
                      })
                  },
                },
              )
            }
          >
            <Trans>Get a new copy</Trans>
          </Button>
          {mappings.length === 0 && (
            <Text size="xs" c="var(--ink-4)" mt="xs">
              <Trans>No source is enabled for this series, so there is nowhere to download it from.</Trans>
            </Text>
          )}
        </>
      )}
      {action.error && (
        <Alert color="var(--danger)" mt="md">
          {action.error.message}
        </Alert>
      )}
    </Panel>
  )
}

/** The record's size disagrees with the file: something outside Maki rewrote it. The fix is the record. */
function SizePanel({ detail }: { detail: FileDetail }) {
  const { t } = useLingui()
  const action = useHealthAction()
  const { file, recordSize } = detail
  const recorded = recordSize == null ? t`Unknown` : formatBytes(recordSize)
  const onDisk = bytes(file.size, t`Missing`)

  return (
    <Panel p="md">
      <Title order={3} size="h4" fz="var(--type-subhead)" mb="sm">
        <Trans>File changed outside Maki</Trans>
      </Title>
      <Text size="sm" c="var(--ink-3)" mb="md">
        <Trans>
          Maki's record says {recorded}, the file on disk is {onDisk}. Usually a tagger or another library tool
          rewrote its metadata. Updating the record makes Maki measure the file again; the file itself is not
          touched.
        </Trans>
      </Text>
      <Button
        leftSection={<IconRefresh size={16} />}
        loading={action.isPending}
        onClick={() =>
          action.mutate(
            { path: '/records/refresh', body: { fileIds: [file.id] } },
            { onSuccess: () => notifications.show({ color: 'var(--ok)', message: t`Maki's record now matches the file on disk.` }) },
          )
        }
      >
        <Trans>Update the record</Trans>
      </Button>
      {action.error && (
        <Alert color="var(--danger)" mt="md">
          {action.error.message}
        </Alert>
      )}
    </Panel>
  )
}

/** Maki could not open the file at all. Nothing here can fix permissions or a mount, so it says where to look. */
function UnreadablePanel({ detail }: { detail: FileDetail }) {
  const action = useHealthAction()
  const { file, findings } = detail
  const message = findings.find((f) => f.kind === 'unreadable' && f.state !== 'resolved')?.message

  return (
    <Panel p="md">
      <Title order={3} size="h4" fz="var(--type-subhead)" mb="sm">
        <Trans>Maki cannot open this file</Trans>
      </Title>
      {message && (
        <Text size="sm" mb="xs">
          {message}
        </Text>
      )}
      <Text size="sm" c="var(--ink-3)" mb="md">
        <Trans>
          The file is there but could not be read. Check that the account Maki runs as can read it, and that the
          drive or share it is on is mounted, then rescan.
        </Trans>
      </Text>
      <Button
        variant="default"
        leftSection={<IconRefresh size={16} />}
        loading={action.isPending}
        onClick={() => action.mutate({ path: '/scans', body: { fileIds: [file.id], force: true } })}
      >
        <Trans>Rescan</Trans>
      </Button>
      {action.error && (
        <Alert color="var(--danger)" mt="md">
          {action.error.message}
        </Alert>
      )}
    </Panel>
  )
}

/**
 * Byte-identical copies of this archive, and the one action they usually need.
 *
 * The finding sits on every copy, so the panel first says which copy stays (the one chapters read
 * from) and which are spare. Deleting a spare loses nothing, so it is confirmed inline rather than
 * through a review screen. Deleting the kept copy instead is offered only when its chapters have
 * somewhere to go: an identical copy in the same series folder takes them over.
 */
function DuplicatePanel({ detail, openFile, close }: { detail: FileDetail; openFile: (id: number) => void; close: () => void }) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const action = useHealthAction()
  const [confirming, setConfirming] = useState<'spares' | 'self' | null>(null)
  const [failures, setFailures] = useState<DeleteReport['failures']>([])
  const { file, copies, relinkTo, chapters } = detail
  const self = copies.find((c) => c.id === file.id)
  const selfSpare = self?.spare ?? false
  const spares = selfSpare ? copies.filter((c) => c.id === file.id) : copies.filter((c) => c.spare)
  const spareCount = spares.length
  const copyCount = copies.length
  const allKept = copies.every((c) => c.kept)
  const relinkPath = relinkTo?.relativePath ?? ''
  const span = chapterSpan(chapters) ?? plural(chapters.length, { one: '# chapter', other: '# chapters' })

  const finish = (message: string, deletedSelf: boolean) => {
    notifications.show({ color: 'var(--ok)', message })
    setConfirming(null)
    if (deletedSelf) close()
  }
  const deleteSpares = () => {
    if (spares.length === 1) {
      const [only] = spares
      action.mutate(
        { path: '/deletions', body: { fileId: only.id, version: only.version, confirmed: true, spare: true } },
        { onSuccess: () => finish(t`Deleted the spare copy`, selfSpare) },
      )
      return
    }
    action.mutate(
      {
        path: '/deletions/bulk',
        body: { files: spares.map((c) => ({ fileId: c.id, version: c.version })), confirmed: true, spares: true },
      },
      {
        onSuccess: (result) => {
          const report = result as unknown as DeleteReport
          const { deleted } = report
          setFailures(report.failures)
          if (report.failures.length === 0)
            finish(plural(deleted, { one: 'Deleted # spare copy', other: 'Deleted # spare copies' }), false)
          else setConfirming(null)
        },
      },
    )
  }
  const deleteSelf = () =>
    action.mutate(
      { path: '/deletions', body: { fileId: file.id, version: file.version, confirmed: true } },
      { onSuccess: () => finish(t`Deleted this copy. Its chapters now read from ${relinkPath}.`, true) },
    )

  return (
    <Panel p="md">
      <Group justify="space-between" align="center" wrap="nowrap" mb="sm">
        <Title order={3} size="h4" fz="var(--type-subhead)">
          <Trans>Identical copies</Trans>
        </Title>
        <Badge variant="light" color="var(--neutral)">
          <Plural value={copyCount} one="# copy" other="# copies" />
        </Badge>
      </Group>

      <Text size="sm" c="var(--ink-3)" mb="md">
        {selfSpare ? (
          <Trans>
            This file is byte for byte the same as the copy marked Keep, and no chapter reads from it. Deleting it
            loses nothing.
          </Trans>
        ) : spareCount > 0 ? (
          <Plural
            value={spareCount}
            one="One other file is byte for byte the same as this one, and no chapter reads from it. Deleting it loses nothing."
            other="# other files are byte for byte the same as this one, and no chapter reads from them. Deleting them loses nothing."
          />
        ) : allKept ? (
          <Trans>
            Every copy backs chapters of its own, so deleting any of them takes the file away from those chapters.
            Open a copy to compare it before deciding.
          </Trans>
        ) : (
          <Trans>The other copy is no longer on disk. Open it to clear its record.</Trans>
        )}
      </Text>

      <ul className="health-copies">
        {copies.map((copy) => {
          const role = copyRole(copy)
          const isThis = copy.id === file.id
          const reads = chapterSpan(copy.chapters) ?? String(copy.chapters.length)
          return (
            <li key={copy.id} className="health-copy" data-this={isThis || undefined}>
              <div className="health-copy-head">
                <span className="health-copy-role" data-role={role}>
                  {renderLabel(COPY_ROLE[role])}
                </span>
                <MonoValue value={copy.relativePath} copy fz="var(--type-meta)" />
                {isThis ? (
                  <Text size="xs" c="var(--ink-4)">
                    <Trans>This file</Trans>
                  </Text>
                ) : (
                  <Button size="compact-xs" variant="subtle" onClick={() => openFile(copy.id)}>
                    <Trans>Open</Trans>
                  </Button>
                )}
              </div>
              <Text component="div" size="xs" c="var(--ink-3)">
                <MetaList
                  items={[
                    copy.missing ? (
                      <Trans key="disk">Gone from disk</Trans>
                    ) : copy.chapters.length > 0 ? (
                      <Trans key="reads">Chapters read from it: {reads}</Trans>
                    ) : (
                      <Trans key="reads">No chapter reads from it</Trans>
                    ),
                    copy.rootPath && copy.rootPath !== self?.rootPath ? copy.rootPath : null,
                    copy.missing ? null : bytes(copy.size, t`Missing`),
                  ]}
                />
              </Text>
            </li>
          )
        })}
      </ul>

      {failures.length > 0 && (
        <Alert color="var(--danger)" mt="md">
          {failures.map((failure) => (
            <Text key={failure.id} size="xs" style={{ overflowWrap: 'anywhere' }}>
              {failure.relativePath}: {failure.message}
            </Text>
          ))}
        </Alert>
      )}

      {spareCount > 0 && (
        <div className="health-copy-actions">
          {confirming === 'spares' ? (
            <div className="health-confirm">
              <Text size="sm">
                {selfSpare ? (
                  <Trans>Delete this file from disk? The kept copy stays and no chapter changes.</Trans>
                ) : (
                  <Plural
                    value={spareCount}
                    one="Delete the spare copy from disk? This file stays and no chapter changes."
                    other="Delete # spare copies from disk? This file stays and no chapter changes."
                  />
                )}
              </Text>
              <Group gap="xs">
                <Button
                  color="var(--danger-fill)"
                  size="xs"
                  leftSection={<IconTrash size={16} />}
                  loading={action.isPending}
                  onClick={deleteSpares}
                >
                  <Trans>Delete</Trans>
                </Button>
                <Button variant="default" size="xs" disabled={action.isPending} onClick={() => setConfirming(null)}>
                  <Trans>Cancel</Trans>
                </Button>
              </Group>
            </div>
          ) : (
            <Button
              color="var(--danger)"
              variant="light"
              leftSection={<IconTrash size={16} />}
              onClick={() => setConfirming('spares')}
            >
              {selfSpare ? (
                <Trans>Delete this copy</Trans>
              ) : (
                <Plural value={spareCount} one="Delete the spare copy" other="Delete # spare copies" />
              )}
            </Button>
          )}
        </div>
      )}

      {self?.kept && relinkTo && confirming !== 'spares' && (
        <div className="health-copy-actions">
          {confirming === 'self' ? (
            <div className="health-confirm">
              <Text size="sm">
                <Trans>
                  Delete this file from disk? {span} then read from {relinkPath}, so nothing becomes unavailable.
                </Trans>
              </Text>
              <Group gap="xs">
                <Button
                  color="var(--danger-fill)"
                  size="xs"
                  leftSection={<IconTrash size={16} />}
                  loading={action.isPending}
                  onClick={deleteSelf}
                >
                  <Trans>Delete</Trans>
                </Button>
                <Button variant="default" size="xs" disabled={action.isPending} onClick={() => setConfirming(null)}>
                  <Trans>Cancel</Trans>
                </Button>
              </Group>
            </div>
          ) : (
            <Button variant="subtle" size="xs" px={0} onClick={() => setConfirming('self')}>
              <Trans>Keep the other copy and delete this one instead</Trans>
            </Button>
          )}
        </div>
      )}

      {action.error && (
        <Alert color="var(--danger)" mt="md">
          {action.error.message}
        </Alert>
      )}
    </Panel>
  )
}

/**
 * What to do about an archive that backs no chapter.
 *
 * The finding alone cannot tell the two cases apart, and they want opposite actions: when a rival
 * file already holds the chapter this is a duplicate to judge, and when nothing holds it the
 * archive is one click from being part of the library. So the panel answers that first, and only
 * then offers the tool for whichever case it is.
 */
function UnlinkedPanel({
  file,
  analysis,
  match,
  action,
  openFile,
}: {
  file: HealthFile
  analysis: Analysis
  match: UnlinkedMatch
  action: ReturnType<typeof useHealthAction>
  openFile: (id: number) => void
}) {
  const { t } = useLingui()
  const { seriesId, seriesTitle, chapters, counterparts, labelKind, number, volumeEnd, recognized } = match
  const chapterCount = chapters.length
  // Its own inline wording rather than `label.toLowerCase()`: case is not a transformation that
  // survives translation, and a language that capitalizes the noun regardless of position needs its
  // own lower-case message.
  const label =
    labelKind === 'chapter'
      ? t`Chapter ${number}`
      : labelKind === 'volumes'
        ? t`Volumes ${number}-${volumeEnd}`
        : labelKind === 'volume'
          ? t`Volume ${number}`
          : t`Unrecognized name`
  const lowerLabel =
    labelKind === 'chapter'
      ? t`chapter ${number}`
      : labelKind === 'volumes'
        ? t`volumes ${number}-${volumeEnd}`
        : labelKind === 'volume'
          ? t`volume ${number}`
          : t`unrecognized name`
  const importable = seriesId != null && chapterCount > 0 && counterparts.length === 0

  return (
    <Panel p="md">
      <Group justify="space-between" align="center" wrap="nowrap" mb="sm">
        <Title order={3} size="h4" fz="var(--type-subhead)">
          <Trans>Not linked to any chapter</Trans>
        </Title>
        <Badge variant="light" color="var(--neutral)">
          {label}
        </Badge>
      </Group>

      {seriesId != null && (
        <Text size="sm" c="var(--ink-3)" mb="sm">
          {chapterCount > 0 ? (
            <Trans>
              Sits in{' '}
              <Anchor component={Link} to={`/series/${seriesId}`}>
                {seriesTitle}
              </Anchor>{' '}
              and parses to <Plural value={chapterCount} one="1 chapter" other="# chapters" /> in it.
            </Trans>
          ) : (
            <Trans>
              Sits in{' '}
              <Anchor component={Link} to={`/series/${seriesId}`}>
                {seriesTitle}
              </Anchor>
              .
            </Trans>
          )}
        </Text>
      )}

      {!recognized && (
        <Alert color="var(--warn)">
          <Trans>
            The file name carries no chapter or volume number, so nothing can be matched to it. Rename it to
            the library's naming format and rescan, or link it by hand from the series' Files tab.
          </Trans>
        </Alert>
      )}

      {recognized && seriesId == null && (
        <Alert color="var(--warn)">
          <Trans>
            This archive is not inside any series folder in its root, so there is no series to import it into.
            Move it into the right folder and rescan, or use the Import page to bring in the folder it lives in.
          </Trans>
        </Alert>
      )}

      {recognized && seriesId != null && chapterCount === 0 && (
        <Alert color="var(--warn)">
          <Trans>
            {seriesTitle} has no {lowerLabel}. Refresh the series so the chapter exists, then import this
            archive.
          </Trans>
        </Alert>
      )}

      {importable && (
        <Stack gap="sm">
          <Alert color="var(--info)">
            <Trans>
              No other file backs {lowerLabel}. Nothing has to be compared: importing adopts this archive and
              links it to the chapter.
            </Trans>
          </Alert>
          <Group gap="xs">
            <Button
              leftSection={<IconFileImport size={16} />}
              loading={action.isPending && action.variables?.path === '/imports'}
              onClick={() => action.mutate({ path: '/imports', body: { fileIds: [file.id] } })}
            >
              <Trans>Import this archive</Trans>
            </Button>
            <Button component={Link} to="/import" variant="default">
              <Trans>Import page</Trans>
            </Button>
          </Group>
          <Text size="xs" c="var(--ink-4)">
            <Trans>
              Import runs the ordinary rescan on {seriesTitle}, so any other new archive in that folder is
              adopted at the same time.
            </Trans>
          </Text>
        </Stack>
      )}

      {counterparts.length > 0 && (
        <Stack gap="md">
          <Alert color="var(--warn)">
            <Trans>
              {label} already has a file. Compare the two before deciding: importing this archive links it
              alongside the existing one, it does not replace it. To swap them, delete the file you do not want
              first.
            </Trans>
          </Alert>
          {counterparts.map((counterpart) => (
            <CompareArchives
              key={counterpart.chapterFileId}
              file={file}
              analysis={analysis}
              counterpart={counterpart}
              openFile={openFile}
            />
          ))}
        </Stack>
      )}
    </Panel>
  )
}

/** This archive against the one already linked, same rows, plus a page-by-page flip through both. */
function CompareArchives({
  file,
  analysis,
  counterpart,
  openFile,
}: {
  file: HealthFile
  analysis: Analysis
  counterpart: MatchCounterpart
  openFile: (id: number) => void
}) {
  const { t } = useLingui()
  const [page, setPage] = useState(1)
  const pages = Math.max(analysis.pages.length, counterpart.pages)
  // Page previews are served per inventoried archive and verified against its recorded hash, so a
  // rival file that was never scanned can be described but not shown.
  const comparable = counterpart.healthFileId != null && counterpart.version != null && pages > 0

  const height = analysis.pages.reduce((sum, p) => sum + p.height, 0)
  // Two releases of the same long-strip chapter agree on how tall the strip is and disagree on how
  // many slices it was cut into, so a page-count mismatch alone means nothing there.
  const strip = analysis.pages.some((p) => p.height > p.width * 2)
  const heights = height > 0 && counterpart.pixelHeight > 0
  const drift = heights ? Math.abs(height - counterpart.pixelHeight) / Math.max(height, counterpart.pixelHeight) : 1
  const driftPercent = Math.round(drift * 100)

  // `id` is what React keys off. The label beside it is translated, and a language switch would
  // otherwise change every key and remount the whole table.
  const rows: { id: string; label: string; mine: string; theirs: string }[] = [
    { id: 'path', label: t`Path`, mine: file.relativePath, theirs: counterpart.relativePath },
    {
      id: 'size',
      label: t`Size`,
      mine: bytes(file.size, t`Missing`),
      theirs: bytes(counterpart.size, t`Missing`),
    },
    {
      id: 'pages',
      label: t`Pages`,
      mine: String(analysis.pages.length),
      theirs: counterpart.healthFileId == null ? t`Not scanned` : String(counterpart.pages),
    },
    {
      id: 'height',
      label: t`Stacked height`,
      mine: height > 0 ? `${formatNumber(height)} px` : t`Unknown`,
      theirs: counterpart.pixelHeight > 0 ? `${formatNumber(counterpart.pixelHeight)} px` : t`Unknown`,
    },
    { id: 'analysis', label: t`Analysis`, mine: analysis.status, theirs: counterpart.status ?? t`Not scanned` },
    { id: 'source', label: t`Source`, mine: t`Not imported`, theirs: counterpart.sourceName || t`Unknown` },
    {
      id: 'hash',
      label: 'SHA-256',
      mine: file.contentHash?.slice(0, 16) ?? t`Unavailable`,
      theirs: counterpart.contentHash?.slice(0, 16) ?? t`Unavailable`,
    },
  ]

  return (
    <Panel p="md">
      <div className="health-compare">
        <div />
        <Text size="xs" fw={600} c="var(--ink-4)">
          <Trans>This archive</Trans>
        </Text>
        <Text size="xs" fw={600} c="var(--ink-4)">
          <Trans>Linked file</Trans>
        </Text>
        {rows.map(({ id, label, mine, theirs }) => (
          <Fragment key={id}>
            <Text size="xs" c="var(--ink-4)">
              {label}
            </Text>
            <Text size="sm" c="var(--ink-2)" style={{ overflowWrap: 'anywhere' }}>
              {mine}
            </Text>
            <Text size="sm" c="var(--ink-2)" style={{ overflowWrap: 'anywhere' }}>
              {theirs}
            </Text>
          </Fragment>
        ))}
      </div>

      {counterpart.contentHash != null && counterpart.contentHash === file.contentHash && (
        <Alert color="var(--warn)" mt="md">
          <Trans>Byte-identical to the linked file. Importing gains nothing; delete one of them.</Trans>
        </Alert>
      )}

      {strip && analysis.pages.length !== counterpart.pages && (
        <Alert color="var(--neutral)" mt="md">
          {heights ? (
            drift <= 0.02 ? (
              <Trans>
                These are long-strip pages and the two releases cut them at different points, so page {page} on
                the left is not page {page} on the right. Stacked height is the comparable number: the two are
                within 2% of each other, so they almost certainly hold the same content.
              </Trans>
            ) : (
              <Trans>
                These are long-strip pages and the two releases cut them at different points, so page {page} on
                the left is not page {page} on the right. Stacked height is the comparable number: they differ
                by {driftPercent}%, so one of them is missing or gaining content.
              </Trans>
            )
          ) : (
            <Trans>
              These are long-strip pages and the two releases cut them at different points, so page {page} on
              the left is not page {page} on the right. Stacked height is the comparable number: one of them
              has not been scanned, so it cannot be measured yet.
            </Trans>
          )}
        </Alert>
      )}

      {comparable ? (
        <>
          <SimpleGrid cols={2} mt="md">
            {[
              { id: file.id, version: file.version, total: analysis.pages.length, alt: t`This archive` },
              {
                id: counterpart.healthFileId!,
                version: counterpart.version!,
                total: counterpart.pages,
                alt: t`Linked file`,
              },
            ].map((side) => {
              const { id: sideId, version: sideVersion, total, alt } = side
              return page <= total ? (
                <Image
                  key={sideId}
                  src={`/api/v1/health/files/${sideId}/pages/${page - 1}?version=${sideVersion}`}
                  alt={t`${alt}, page ${page}`}
                  h={300}
                  fit="contain"
                />
              ) : (
                <Text key={sideId} size="sm" c="var(--ink-4)" ta="center">
                  <Trans>
                    {alt} has no page {page}
                  </Trans>
                </Text>
              )
            })}
          </SimpleGrid>
          <Group justify="space-between" mt="md">
            <Pagination value={page} onChange={setPage} total={pages} siblings={1} size="sm" />
            {counterpart.healthFileId != null && (
              <Button size="xs" variant="subtle" onClick={() => openFile(counterpart.healthFileId!)}>
                <Trans>Review linked file</Trans>
              </Button>
            )}
          </Group>
        </>
      ) : (
        <Alert color="var(--neutral)" mt="md">
          <Trans>
            The linked file has not been inventoried yet, so its pages cannot be shown. Run Scan files, then
            come back to compare them.
          </Trans>
        </Alert>
      )}
    </Panel>
  )
}

/** The most rows HealthController serves for one group. */
const GROUP_ROWS_MAX = 500

/**
 * Operations of one kind that ended the same way, grouped by the server so the count is the whole
 * group, not what fits on a page. A bulk delete the filesystem refused reads as the single
 * problem it is. The newest rows come with the group; Show all loads the rest.
 */
function OperationGroup({ group, onReview }: { group: OperationGroupData; onReview: (id: number) => void }) {
  const [expanded, setExpanded] = useState(false)
  const all = useHealthData<{ items: HealthOperation[]; total: number }>(
    `/operations/group?key=${encodeURIComponent(group.key)}&take=${Math.min(group.count, GROUP_ROWS_MAX)}`,
    expanded,
  )
  const dismiss = useHealthAction()
  const count = group.count
  const max = GROUP_ROWS_MAX
  const shown = expanded && all.data ? all.data.items : group.rows
  const finished = FINISHED_STATES.includes(group.status)
  return (
    <Panel p={0} className="health-op-group">
      <div className="health-op-head">
        <Group justify="space-between" wrap="nowrap">
          <Group gap="sm">
            <Text fw={650}>
              {group.kind === 'delete' ? (
                count === 1 ? (
                  <Trans>Deletion</Trans>
                ) : (
                  <Plural value={count} one="# deletion" other="# deletions" />
                )
              ) : count === 1 ? (
                <Trans>Replacement</Trans>
              ) : (
                <Plural value={count} one="# replacement" other="# replacements" />
              )}
            </Text>
            <Status value={group.status} />
          </Group>
          {finished && (
            <Button
              size="compact-sm"
              variant="subtle"
              color="var(--neutral)"
              loading={dismiss.isPending}
              onClick={() => dismiss.mutate({ path: '/operations/group/dismiss', body: { key: group.key } })}
            >
              {count === 1 ? <Trans>Dismiss</Trans> : <Trans>Dismiss all</Trans>}
            </Button>
          )}
        </Group>
        {group.error && (
          <Text size="sm" c="var(--ink-3)">
            {group.error}
          </Text>
        )}
        {dismiss.error && (
          <Text size="sm" c="var(--danger)">
            {dismiss.error.message}
          </Text>
        )}
      </div>
      <ul className="health-op-rows">
        {shown.map((op) => {
          const fileName = op.relativePath?.split('/').pop() ?? null
          return (
            <li key={op.id} className="health-op-row">
              <SeriesCell
                seriesId={op.seriesId ?? null}
                title={op.seriesTitle ?? fileName ?? `#${op.fileId}`}
                coverUrl={op.coverUrl ?? null}
                detail={
                  op.seriesTitle && fileName && op.relativePath ? (
                    <MonoValue value={fileName} full={op.relativePath} focusable={false} fz="var(--type-meta)" c="var(--ink-3)" />
                  ) : undefined
                }
                small
              />
              <Text size="xs" c="var(--ink-3)" className="health-op-when">
                {formatDateTime(op.createdAt)}
              </Text>
              <Button size="compact-sm" variant="default" onClick={() => onReview(op.id)}>
                {op.status === 'review' ? <Trans>Review</Trans> : <Trans>Details</Trans>}
              </Button>
            </li>
          )
        })}
      </ul>
      {count > group.rows.length && (
        <div className="health-op-more">
          <Button size="compact-sm" variant="subtle" color="var(--neutral)" onClick={() => setExpanded((e) => !e)}>
            {expanded ? (
              <Trans>Show fewer</Trans>
            ) : count > GROUP_ROWS_MAX ? (
              <Trans>
                Show newest {max} of {count}
              </Trans>
            ) : (
              <Plural value={count} one="Show all # operation" other="Show all # operations" />
            )}
          </Button>
          {expanded && all.isFetching && !all.data && <Loader size="xs" ml="sm" />}
          {expanded && all.error && (
            <Text size="sm" c="var(--danger)" mt="xs">
              {all.error.message}
            </Text>
          )}
        </div>
      )}
    </Panel>
  )
}

function OperationReview({ id, close, open }: { id: number; close: () => void; open: (id: number) => void }) {
  const { t } = useLingui()
  const { data, error } = useHealthData<OperationDetail>(`/operations/${id}`, true, (detail) =>
    detail && RUNNING_STATES.includes(detail.operation.status) ? 3000 : false,
  )
  const action = useHealthAction()
  const [confirm, setConfirm] = useState(false)
  const [reset, setReset] = useState(false)
  const chaptersCount = data?.chapters.length ?? 0
  const finished = !!data && FINISHED_STATES.includes(data.operation.status)
  const chapterLabel =
    (data && chapterSpan(data.chapters)) ?? plural(chaptersCount, { one: '# chapter', other: '# chapters' })
  const relinkPath = data?.relinkTo?.relativePath ?? ''

  return (
    <Modal
      opened
      onClose={close}
      title={finished ? t`Operation details` : t`Review operation`}
      size="min(1060px, 94vw)"
      centered
    >
      <Stack gap="lg">
        {(error ?? action.error) && <Alert color="var(--danger)">{(error ?? action.error)?.message}</Alert>}
        {!data ? (
          <Loader />
        ) : (
          <div className="health-review">
            <div className="health-review-column">
              {data.operation.error && <Alert color="var(--danger)">{data.operation.error}</Alert>}
              {finished ? (
                <OperationOutcome detail={data} />
              ) : data.operation.kind === 'delete' ? (
                <Alert color={data.file.size < 0 || data.relinkTo || chaptersCount === 0 ? 'var(--warn)' : 'var(--danger)'}>
                  {data.file.size < 0 ? (
                    <Trans>
                      This file is already gone from disk, so nothing is deleted. It drops the record that
                      still links these chapters to it, so they stop reading as downloaded.
                    </Trans>
                  ) : data.relinkTo ? (
                    <Trans>
                      This permanently deletes the archive. {chapterLabel} then read from the identical copy at{' '}
                      {relinkPath}, so nothing becomes unavailable.
                    </Trans>
                  ) : chaptersCount > 0 ? (
                    <Trans>
                      This permanently deletes the archive, and {chapterLabel} read from it. They will show as not
                      downloaded. Chapter records and reading history remain.
                    </Trans>
                  ) : (
                    <Trans>This permanently deletes the archive. No chapter reads from it.</Trans>
                  )}{' '}
                  {!data.relinkTo && data.chapters.some((c) => c.wanted) && (
                    <Trans>Wanted chapters may be downloaded again by existing automation.</Trans>
                  )}
                </Alert>
              ) : (
                <>
                  <Text size="sm" c="var(--ink-3)">
                    <Trans>Review every candidate before applying. Originals remain unchanged until approval.</Trans>
                  </Text>
                  {data.candidates.map((candidate) => {
                    const { chapterId } = candidate
                    const pageCount = candidate.analysis.pages.length
                    return (
                      <Panel key={candidate.chapterId} p="md">
                        <Group justify="space-between" wrap="nowrap" mb="sm">
                          <Text fw={600}>
                            <Trans>
                              Chapter {chapterId}: <Plural value={pageCount} one="# page" other="# pages" />
                            </Trans>
                          </Text>
                          <Status value={candidate.analysis.status} />
                        </Group>
                        {candidate.analysis.problems.map((p, i) => (
                          <Text key={i} c={color(p.severity)} size="sm">
                            {p.message}
                          </Text>
                        ))}
                        <SimpleGrid cols={2}>
                          {candidate.analysis.pages.slice(0, 4).map((p, index) => {
                            const pageNumber = index + 1
                            return (
                              <Image
                                key={p.name}
                                h={200}
                                fit="contain"
                                src={`/api/v1/health/operations/${id}/candidates/${candidate.chapterId}/pages/${index}`}
                                alt={t`Candidate page ${pageNumber}`}
                              />
                            )
                          })}
                        </SimpleGrid>
                      </Panel>
                    )
                  })}
                </>
              )}
            </div>

            <div className="health-review-column">
              <Panel p="md">
                <Status value={data.operation.status} />
                <Text size="xs" c="var(--ink-3)" mt="xs" component="div">
                  <Trans>Operation</Trans> <MonoValue value={String(id)} copy />
                </Text>
                <Text fw={600} mt="sm" component="div">
                  <MonoValue value={data.file.relativePath} copy />
                </Text>
                <Text component="div" size="sm" c="var(--ink-3)" mt={4}>
                  <MetaList
                    items={[
                      bytes(data.file.size, t`Missing`),
                      <Plural key="chapters" value={chaptersCount} one="# affected chapter" other="# affected chapters" />,
                    ]}
                  />
                </Text>
              </Panel>

              <Panel p="md">
                {finished ? (
                  <Group gap="sm">
                    {data.retryable && (
                      <Button
                        loading={action.isPending && action.variables?.path.endsWith('/retry')}
                        onClick={() =>
                          action.mutate({ path: `/operations/${id}/retry` }, { onSuccess: (next) => open(next.id) })
                        }
                      >
                        <Trans>Retry</Trans>
                      </Button>
                    )}
                    <Button
                      variant="default"
                      loading={action.isPending && action.variables?.path.endsWith('/dismiss')}
                      onClick={() => action.mutate({ path: `/operations/${id}/dismiss` }, { onSuccess: close })}
                    >
                      <Trans>Dismiss</Trans>
                    </Button>
                  </Group>
                ) : (
                  <Stack gap="sm">
                    {data.operation.kind === 'repair' && data.requiresReset && (
                      <Checkbox
                        checked={reset}
                        onChange={(e) => setReset(e.currentTarget.checked)}
                        label={t`Reset bookmarks and resume positions for affected chapters across all users. Completed status and reading history are preserved.`}
                      />
                    )}
                    {data.operation.status === 'review' && (
                      <>
                        <Checkbox
                          checked={confirm}
                          onChange={(e) => setConfirm(e.currentTarget.checked)}
                          label={
                            data.operation.kind !== 'delete'
                              ? t`I approve applying these replacement files.`
                              : data.file.size < 0
                                ? t`I confirm removing the file links for this missing archive.`
                                : t`I confirm permanent deletion of this archive and all its file links.`
                          }
                        />
                        <Button
                          color={data.operation.kind === 'delete' ? 'var(--danger)' : 'brand'}
                          loading={action.isPending && action.variables?.path.endsWith('/apply')}
                          disabled={!confirm || (data.operation.kind === 'repair' && data.requiresReset && !reset)}
                          onClick={() =>
                            action.mutate(
                              {
                                path: `/operations/${id}/apply`,
                                body: { version: data.operation.version, confirmed: confirm, resetPositions: reset },
                              },
                              { onSuccess: close },
                            )
                          }
                        >
                          {data.operation.kind !== 'delete' ? (
                            <Trans>Apply replacement</Trans>
                          ) : data.file.size < 0 ? (
                            <Trans>Remove the record</Trans>
                          ) : (
                            <Trans>Permanently delete</Trans>
                          )}
                        </Button>
                      </>
                    )}
                    <Button
                      variant="default"
                      onClick={() => action.mutate({ path: `/operations/${id}/cancel` }, { onSuccess: close })}
                    >
                      <Trans>Cancel operation</Trans>
                    </Button>
                  </Stack>
                )}
              </Panel>
            </div>
          </div>
        )}
      </Stack>
    </Modal>
  )
}

/** What a finished operation did, in place of the review it no longer needs. */
function OperationOutcome({ detail }: { detail: OperationDetail }) {
  const { operation, file, retryable } = detail
  const requested = formatDateTime(operation.createdAt)
  const finishedAt = operation.finishedAt ? formatDateTime(operation.finishedAt) : null
  return (
    <Stack gap="xs">
      <Text>
        {operation.status === 'completed' ? (
          operation.kind !== 'delete' ? (
            <Trans>The replacement was applied.</Trans>
          ) : file.size < 0 ? (
            <Trans>The record of the missing archive was removed.</Trans>
          ) : (
            <Trans>The archive was deleted.</Trans>
          )
        ) : operation.status === 'cancelled' ? (
          <Trans>Cancelled. Nothing in the library was changed.</Trans>
        ) : (
          <Trans>Nothing in the library was changed.</Trans>
        )}
      </Text>
      {operation.status === 'failed' && (
        <Text size="sm" c="var(--ink-3)">
          {retryable ? (
            <Trans>The file has not changed since, so you can run this again.</Trans>
          ) : (
            <Trans>The file has changed or is gone since, so this cannot run again.</Trans>
          )}
        </Text>
      )}
      <Text size="sm" c="var(--ink-3)">
        {finishedAt ? (
          <Trans>
            Requested {requested}, finished {finishedAt}
          </Trans>
        ) : (
          <Trans>Requested {requested}</Trans>
        )}
      </Text>
    </Stack>
  )
}

function CachePanel() {
  const { t } = useLingui()
  const cache = useImageCache()
  const rebuild = useRebuildImageCache()

  return (
    <Stack gap="sm">
      <SectionHeader icon={IconPhotoScan} title={t`Image cache and backups`} />
      <Panel p="lg">
      <Group justify="space-between" align="center" gap="lg">
        {cache.data && (
          <div className="health-facts">
            {(
              [
                [t`Posters`, bytes(cache.data.usage.coverBytes, t`Missing`)],
                [t`Thumbnails`, bytes(cache.data.usage.thumbnailBytes, t`Missing`)],
                [t`Missing posters`, cache.data.usage.coversMissing],
              ] as const
            ).map(([label, fact]) => (
              <div key={label}>
                <Text size="xs" c="var(--ink-4)">
                  {label}
                </Text>
                <Text size="sm" c="var(--ink-2)" fw={600} className="tnum" mt={2}>
                  {fact}
                </Text>
              </div>
            ))}
          </div>
        )}
        <Group gap="xs">
          <Button
            variant="default"
            loading={rebuild.isPending || cache.data?.status.running}
            onClick={() => rebuild.mutate(false)}
          >
            <Trans>Rebuild missing images</Trans>
          </Button>
          <Button component={Link} to="/settings?tab=system" variant="default">
            <Trans>Backup and cache tools</Trans>
          </Button>
        </Group>
      </Group>
      {(cache.error ?? rebuild.error) && (
        <Alert color="var(--danger)" mt="md">
          {(cache.error ?? rebuild.error)?.message}
        </Alert>
      )}
      </Panel>
    </Stack>
  )
}
