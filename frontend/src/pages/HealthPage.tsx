import {
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
  NumberInput,
  Pagination,
  Paper,
  ScrollArea,
  Select,
  SimpleGrid,
  Stack,
  Switch,
  Table,
  Tabs,
  Text,
  TextInput,
  Title,
} from '@mantine/core'
import {
  IconAlertTriangle,
  IconArchive,
  IconBooks,
  IconChevronDown,
  IconClockPlay,
  IconDatabase,
  IconDownload,
  IconFileAlert,
  IconFileImport,
  IconPlugConnected,
  IconPhotoScan,
  IconRefresh,
  IconScan,
  IconServer,
  IconTrash,
  type Icon,
} from '@tabler/icons-react'
import { Fragment, useMemo, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { useImageCache, useRebuildImageCache } from '../api/hooks'
import {
  useHealthAction,
  useHealthData,
  type Analysis,
  type FileDetail,
  type HealthCheck,
  type HealthFile,
  type HealthOperation,
  type HealthOptions,
  type HealthOverview,
  type MatchCounterpart,
  type OperationDetail,
  type UnlinkedMatch,
} from '../api/health'
import { PageHeader } from '../components/ui/PageHeader'
import { StatTile } from '../components/ui/StatTile'
import { formatDateTime, formatNumber } from '../format'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'
import { SurfaceFrame } from '../components/ui/SurfaceFrame'

/** Select value standing for "no pinned source": let the series' priority order decide. */
const AUTOMATIC = 'automatic'

/** Statuses that mean somebody has to look at this. Everything else is passing or informational. */
const ISSUE = ['error', 'warning', 'unavailable']

/** Sort key for a check: unresolved first, then acknowledged, then passing. */
const weight = (check: HealthCheck) =>
  !ISSUE.includes(check.status) ? 2 : check.acknowledged ? 1 : 0

/** HealthMonitor's category strings. Anything unknown falls back to the generic system icon. */
const CATEGORY_ICON: Record<string, Icon> = {
  library: IconBooks,
  storage: IconDatabase,
  connections: IconPlugConnected,
  system: IconServer,
  downloads: IconDownload,
  job: IconClockPlay,
}

const color = (status: string) =>
  status === 'error' || status === 'failed'
    ? 'red'
    : ['warning', 'partial', 'open'].includes(status)
      ? 'yellow'
      : ['healthy', 'complete', 'completed'].includes(status)
        ? 'green'
        : 'gray'

const bytes = (size: number, missing: string) =>
  size < 0 ? missing : `${(size / 1024 / 1024).toFixed(1)} MiB`

function Status({ value }: { value: string }) {
  return (
    <Badge color={color(value)} variant="light">
      {value}
    </Badge>
  )
}

export default function HealthPage() {
  const { t } = useLingui()
  const [params, setParams] = useSearchParams()
  const tab = params.get('tab') ?? 'overview'
  const overview = useHealthData<HealthOverview>()
  const [fileId, setFileId] = useState<number | null>(null)
  const [operationId, setOperationId] = useState<number | null>(null)
  const [search, setSearch] = useState('')
  const [root, setRoot] = useState<string | null>(null)
  const [kind, setKind] = useState<string | null>(null)
  const [state, setState] = useState<string | null>('open')
  const [page, setPage] = useState(1)
  const [selected, setSelected] = useState<Set<number>>(new Set())
  const [deleteOpen, setDeleteOpen] = useState(false)
  const [deleteReport, setDeleteReport] = useState<DeleteReport | null>(null)
  const [historyPage, setHistoryPage] = useState(1)
  const [repairPage, setRepairPage] = useState(1)

  const query = new URLSearchParams({ page: String(page), search })
  if (root) query.set('rootId', root)
  if (kind) query.set('kind', kind)
  if (state) query.set('state', state)

  const files = useHealthData<{ items: HealthFile[]; total: number }>(`/files?${query}`, tab === 'files')
  const operations = useHealthData<{ items: HealthOperation[]; total: number }>(
    `/operations?page=${repairPage}`,
    tab === 'repairs',
  )
  const history = useHealthData<{
    items: { id: number; createdAt: string; kind: string; message: string }[]
    total: number
  }>(`/history?page=${historyPage}`, tab === 'history')

  const action = useHealthAction()
  const run = (path: string, body = {}) => action.mutate({ path, body })
  // Every filter change re-queries a different set of rows, so a selection made against the old
  // one would silently act on files the user can no longer see.
  const refilter = (apply: () => void) => {
    apply()
    setPage(1)
    setSelected(new Set())
  }
  const bulk = (path: string, body: object) =>
    action.mutate({ path, body }, { onSuccess: () => setSelected(new Set()) })
  const ids = [...selected]
  const pageIds = files.data?.items.map((f) => f.id) ?? []
  const error = overview.error ?? files.error ?? operations.error ?? history.error ?? action.error
  // Acknowledged checks are still issues, but they are issues someone has already decided about,
  // so they do not belong in a number whose job is to say "something needs you".
  const issues = overview.data?.checks.filter((c) => ISSUE.includes(c.status) && !c.acknowledged).length ?? 0
  const partialScanError = overview.data?.scans.find((s) => s.error)?.error
  const selectedCount = selected.size
  const deletedCount = deleteReport?.deleted ?? 0
  const failedCount = deleteReport?.failures.length ?? 0

  return (
    <SurfaceFrame pageStyle="operational" className="health-surface">
      <PageHeader
        title={t`Health`}
        description={t`System checks and reviewed library maintenance.`}
        actions={
          <>
            <Button
              variant="default"
              leftSection={<IconRefresh size={16} />}
              loading={action.isPending}
              onClick={() => run('/refresh')}
            >
              <Trans>Check now</Trans>
            </Button>
            <Menu position="bottom-end" withinPortal width={320}>
              <Menu.Target>
                <Button leftSection={<IconScan size={16} />} rightSection={<IconChevronDown size={14} />}>
                  <Trans>Scan files</Trans>
                </Button>
              </Menu.Target>
              <Menu.Dropdown>
                <Menu.Item onClick={() => run('/scans', { rootFolderId: root ? Number(root) : null })}>
                  <Text size="sm" fw={600}>
                    <Trans>Index</Trans>
                  </Text>
                  <Text size="xs" c="var(--ink-4)">
                    <Trans>
                      Reads each archive's table of contents, not its contents. Seconds for a whole library.
                      Finds files that went missing, arrived on their own, changed size, or stopped being a
                      readable archive.
                    </Trans>
                  </Text>
                </Menu.Item>
                <Menu.Item onClick={() => run('/scans', { rootFolderId: root ? Number(root) : null, verify: true })}>
                  <Text size="sm" fw={600}>
                    <Trans>Verify</Trans>
                  </Text>
                  <Text size="xs" c="var(--ink-4)">
                    <Trans>
                      Reads every byte and checks it against the archive's own checksums, which is what catches
                      a file that has rotted on disk. A full read of the library: around half an hour per 100 GB
                      on a hard disk. Files are verified as they arrive, so this is for re-checking what is
                      already there.
                    </Trans>
                  </Text>
                </Menu.Item>
              </Menu.Dropdown>
            </Menu>
          </>
        }
      />

      {error && (
        <Alert color="red" mb="lg">
          {error.message}
        </Alert>
      )}
      {overview.isPending && <Loader mb="lg" />}

      <SimpleGrid cols={{ base: 1, sm: 3 }} spacing="sm" mb="lg">
        <StatTile
          label={t`System issues`}
          value={issues}
          icon={IconAlertTriangle}
          accent={issues > 0 ? 'danger' : 'ok'}
        />
        <StatTile
          label={t`Open file findings`}
          value={overview.data?.openFindings ?? 0}
          icon={IconFileAlert}
          accent={(overview.data?.openFindings ?? 0) > 0 ? 'warn' : 'ok'}
        />
        <StatTile label={t`Archives inventoried`} value={overview.data?.files ?? 0} icon={IconArchive} accent="info" />
      </SimpleGrid>

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
                  <Trans>
                    {completed} / {total} files inspected
                  </Trans>
                </Text>
                <Button size="xs" variant="default" onClick={() => run(`/scans/${scan.id}/cancel`)}>
                  <Trans>Cancel scan</Trans>
                </Button>
              </Group>
            </Alert>
          )
        })}
      {partialScanError && (
        <Alert color="yellow" mb="lg">
          <Trans>Last partial scan: {partialScanError}</Trans>
        </Alert>
      )}

      <Tabs className="panel-tabs" value={tab} onChange={(value) => setParams({ tab: value ?? 'overview' })}>
        <Tabs.List className="panel-tab-list">
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

        <Tabs.Panel value="overview" pt="lg">
          <div className="health-overview">
            <ChecksPanel checks={overview.data?.checks ?? []} run={run} />
            <CachePanel />
            <OptionsPanel />
          </div>
        </Tabs.Panel>

        <Tabs.Panel value="files" pt="lg">
          <Stack>
            <Group className="health-filter-rail">
              <TextInput
                placeholder={t`Search file paths`}
                aria-label={t`Search file paths`}
                value={search}
                onChange={(e) => refilter(() => setSearch(e.currentTarget.value))}
              />
              <Select
                placeholder={t`All roots`}
                clearable
                value={root}
                onChange={(v) => refilter(() => setRoot(v))}
                data={overview.data?.roots.map((r) => ({ value: String(r.id), label: r.path })) ?? []}
              />
              <Select
                placeholder={t`All findings`}
                clearable
                value={kind}
                onChange={(v) => refilter(() => setKind(v))}
                data={[
                  'missing',
                  'empty',
                  'corrupt',
                  'noPages',
                  'damagedImage',
                  'duplicate',
                  'unlinked',
                  'sizeMismatch',
                  'incomplete',
                ]}
              />
              <Select
                placeholder={t`All states`}
                clearable
                value={state}
                onChange={(v) => refilter(() => setState(v))}
                data={['open', 'acknowledged', 'ignored', 'resolved']}
              />
            </Group>

            {selectedCount > 0 && (
              <Paper withBorder radius="md" p="xs" className="health-bulk-bar">
                <Text size="sm" fw={600} className="tnum">
                  <Trans>{selectedCount} selected</Trans>
                </Text>
                <Group gap="xs" wrap="wrap">
                  <Button
                    size="xs"
                    variant="default"
                    loading={action.isPending}
                    onClick={() => bulk('/scans', { fileIds: ids, force: true })}
                  >
                    <Trans>Rescan</Trans>
                  </Button>
                  <Button
                    size="xs"
                    variant="default"
                    loading={action.isPending}
                    onClick={() => bulk('/scans', { fileIds: ids, force: true, verify: true })}
                  >
                    <Trans>Verify</Trans>
                  </Button>
                  <Button
                    size="xs"
                    variant="default"
                    loading={action.isPending}
                    onClick={() => bulk('/imports', { fileIds: ids })}
                  >
                    <Trans>Import unlinked</Trans>
                  </Button>
                  {(['acknowledged', 'ignored', 'open'] as const).map((next) => (
                    <Button
                      key={next}
                      size="xs"
                      variant="default"
                      loading={action.isPending}
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
                  <Button
                    size="xs"
                    color="red"
                    variant="light"
                    leftSection={<IconTrash size={14} />}
                    onClick={() => setDeleteOpen(true)}
                  >
                    <Trans>Delete</Trans>
                  </Button>
                  <Button size="xs" variant="subtle" onClick={() => setSelected(new Set())}>
                    <Trans>Clear</Trans>
                  </Button>
                </Group>
              </Paper>
            )}

            {deleteReport && failedCount > 0 && (
              <Alert color="red" withCloseButton onClose={() => setDeleteReport(null)}>
                <Text size="sm" mb="xs">
                  <Trans>
                    {deletedCount} deleted, {failedCount} refused:
                  </Trans>
                </Text>
                {deleteReport.failures.map((failure) => (
                  <Text key={failure.id} size="xs" style={{ overflowWrap: 'anywhere' }}>
                    {failure.relativePath}: {failure.message}
                  </Text>
                ))}
              </Alert>
            )}

            {files.isPending ? (
              <Loader />
            ) : (
              <>
                <Table.ScrollContainer minWidth={720}>
                  <Table striped highlightOnHover className="panel-table">
                    <Table.Thead>
                      <Table.Tr>
                        <Table.Th w={40}>
                          <Checkbox
                            aria-label={t`Select every file on this page`}
                            checked={pageIds.length > 0 && pageIds.every((i) => selected.has(i))}
                            indeterminate={
                              pageIds.some((i) => selected.has(i)) && !pageIds.every((i) => selected.has(i))
                            }
                            onChange={(e) => {
                              const checked = e.currentTarget.checked
                              const next = new Set(selected)
                              for (const i of pageIds) {
                                if (checked) next.add(i)
                                else next.delete(i)
                              }
                              setSelected(next)
                            }}
                          />
                        </Table.Th>
                        <Table.Th>
                          <Trans>Archive</Trans>
                        </Table.Th>
                        <Table.Th>
                          <Trans>Size</Trans>
                        </Table.Th>
                        <Table.Th>
                          <Trans>Analysis</Trans>
                        </Table.Th>
                        <Table.Th>
                          <Trans>Findings</Trans>
                        </Table.Th>
                        <Table.Th />
                      </Table.Tr>
                    </Table.Thead>
                    <Table.Tbody>
                      {files.data?.items.map((file) => {
                        const { relativePath } = file
                        return (
                        <Table.Tr key={file.id} data-selected={selected.has(file.id) || undefined}>
                          <Table.Td>
                            <Checkbox
                              aria-label={t`Select ${relativePath}`}
                              checked={selected.has(file.id)}
                              onChange={(e) => {
                                const checked = e.currentTarget.checked
                                const next = new Set(selected)
                                if (checked) next.add(file.id)
                                else next.delete(file.id)
                                setSelected(next)
                              }}
                            />
                          </Table.Td>
                          <Table.Td>
                            <Text size="sm" style={{ overflowWrap: 'anywhere' }}>
                              {file.relativePath}
                            </Text>
                            <Text size="xs" c="dimmed">
                              {file.analyzedAt ? formatDateTime(file.analyzedAt) : <Trans>Not analyzed</Trans>}
                            </Text>
                          </Table.Td>
                          <Table.Td>{bytes(file.size, t`Missing`)}</Table.Td>
                          <Table.Td>
                            <Status value={file.status} />
                          </Table.Td>
                          <Table.Td>
                            <Group gap={4}>
                              {file.findings.map((f) => (
                                <Badge key={f.id} color={color(f.severity)} variant="light">
                                  {f.kind}
                                </Badge>
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
                {files.data?.items.length === 0 && (
                  <Text c="dimmed">
                    <Trans>No files match these filters. Run a scan to inventory the library.</Trans>
                  </Text>
                )}
                <Pagination
                  value={page}
                  onChange={setPage}
                  total={Math.max(1, Math.ceil((files.data?.total ?? 0) / 30))}
                />
              </>
            )}
          </Stack>
        </Tabs.Panel>

        <Tabs.Panel value="repairs" pt="lg">
          <Stack>
            {operations.data?.items.map((op) => {
              const { id: opId, kind: opKind } = op
              return (
                <Paper withBorder radius="lg" p="md" key={op.id}>
                  <Group justify="space-between">
                    <div>
                      <Group>
                        <Text fw={600}>
                          {opKind === 'delete' ? (
                            <Trans>Deletion review #{opId}</Trans>
                          ) : (
                            <Trans>Replacement #{opId}</Trans>
                          )}
                        </Text>
                        <Status value={op.status} />
                      </Group>
                      <Text size="sm" c="dimmed">
                        {op.error ?? formatDateTime(op.createdAt)}
                      </Text>
                    </div>
                    <Button variant="default" onClick={() => setOperationId(op.id)}>
                      <Trans>Review</Trans>
                    </Button>
                  </Group>
                </Paper>
              )
            })}
            {operations.data?.items.length === 0 && (
              <Text c="dimmed">
                <Trans>No repairs or deletions requested.</Trans>
              </Text>
            )}
            <Pagination
              value={repairPage}
              onChange={setRepairPage}
              total={Math.max(1, Math.ceil((operations.data?.total ?? 0) / 30))}
            />
          </Stack>
        </Tabs.Panel>

        <Tabs.Panel value="history" pt="lg">
          <Stack>
            {history.data?.items.map((entry) => (
              <Group key={entry.id} align="flex-start">
                <Badge variant="light">{entry.kind}</Badge>
                <div>
                  <Text size="sm">{entry.message}</Text>
                  <Text size="xs" c="dimmed">
                    {formatDateTime(entry.createdAt)}
                  </Text>
                </div>
              </Group>
            ))}
            <Pagination
              value={historyPage}
              onChange={setHistoryPage}
              total={Math.max(1, Math.ceil((history.data?.total ?? 0) / 30))}
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
        <OperationReview key={operationId} id={operationId} close={() => setOperationId(null)} />
      )}

      <BulkDeleteModal
        opened={deleteOpen}
        close={() => setDeleteOpen(false)}
        ids={ids}
        // Only the current page's rows are loaded, so a selection carried across pages can name
        // fewer paths than it deletes. Say so rather than showing a list that looks complete.
        named={files.data?.items.filter((f) => selected.has(f.id)).map((f) => f.relativePath) ?? []}
        pending={action.isPending}
        onConfirm={() =>
          action.mutate(
            { path: '/deletions/bulk', body: { fileIds: ids, confirmed: true } },
            {
              onSuccess: (result) => {
                setSelected(new Set())
                setDeleteOpen(false)
                setDeleteReport(result as unknown as DeleteReport)
              },
            },
          )
        }
      />
    </SurfaceFrame>
  )
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
  ids,
  named,
  pending,
  onConfirm,
}: {
  opened: boolean
  close: () => void
  ids: number[]
  named: string[]
  pending: boolean
  onConfirm: () => void
}) {
  const { i18n } = useLingui()
  const [confirmed, setConfirmed] = useState(false)
  const count = ids.length
  const remaining = ids.length - named.length

  // `plural` is the core macro, which reads the catalogue when it runs rather than subscribing.
  // `i18n.locale` in the deps is what makes these follow a language switch.
  const [title, confirmLabel] = useMemo(
    () => [
      plural(count, { one: 'Delete # archive?', other: 'Delete # archives?' }),
      plural(count, {
        one: 'I confirm permanent deletion of # archive.',
        other: 'I confirm permanent deletion of # archives.',
      }),
    ],
    [count, i18n.locale],
  )

  return (
    <Modal
      opened={opened}
      onClose={close}
      title={title}
      centered
      size="lg"
      scrollAreaComponent={ScrollArea.Autosize}
    >
      <Stack gap="md">
        <Alert color="red">
          <Trans>
            This permanently deletes <Plural value={count} one="the file" other="these files" /> from disk.
            Chapter records, Wanted flags and reading history are kept, so anything still wanted can be
            downloaded again. Archives already missing from disk have nothing to delete: for those this only
            clears the record that still says their chapters are downloaded.
          </Trans>
        </Alert>
        <Paper withBorder radius="md" p="sm">
          <Stack gap={4}>
            {named.map((path) => (
              <Text key={path} size="xs" c="var(--ink-3)" style={{ overflowWrap: 'anywhere' }}>
                {path}
              </Text>
            ))}
            {remaining > 0 && (
              <Text size="xs" c="var(--ink-4)">
                <Trans>and {remaining} more selected on other pages</Trans>
              </Text>
            )}
          </Stack>
        </Paper>
        <Checkbox
          checked={confirmed}
          onChange={(e) => setConfirmed(e.currentTarget.checked)}
          label={confirmLabel}
        />
        <Group justify="flex-end">
          <Button variant="default" onClick={close}>
            <Trans>Cancel</Trans>
          </Button>
          <Button color="red" disabled={!confirmed} loading={pending} onClick={onConfirm}>
            <Trans>Delete permanently</Trans>
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
 * source cooldown and one per root folder, so a healthy instance shows around thirty green rows
 * and the two that matter are lost in them.
 */
function ChecksPanel({ checks, run }: { checks: HealthCheck[]; run: (path: string, body?: object) => void }) {
  const { t } = useLingui()
  const [showPassing, setShowPassing] = useState(false)
  const visible = showPassing ? checks : checks.filter((c) => ISSUE.includes(c.status))
  const categories = Array.from(new Set(visible.map((c) => c.category)))
  const totalChecks = checks.length

  return (
    <Paper className="health-area-checks" withBorder radius="lg" p="lg">
      <Group justify="space-between" align="center" wrap="nowrap" mb="md">
        <Title order={3} fz={17}>
          <Trans>System checks</Trans>
        </Title>
        <Switch
          size="xs"
          label={t`Show passing`}
          checked={showPassing}
          onChange={(e) => setShowPassing(e.currentTarget.checked)}
        />
      </Group>

      {checks.length === 0 && (
        <Alert>
          <Trans>No checks have run yet. Use Check now.</Trans>
        </Alert>
      )}
      {checks.length > 0 && visible.length === 0 && (
        <Text size="sm" c="dimmed">
          <Trans>
            Everything is passing. Turn on Show passing to see all{' '}
            <Plural value={totalChecks} one="# check" other="# checks" />.
          </Trans>
        </Text>
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
              <CategoryIcon size={14} stroke={1.8} />
              <span>{category}</span>
              <Text span size="xs" c="var(--ink-4)" fw={500} className="tnum">
                {rows.length}
              </Text>
            </Group>
            {rows.map((check) => (
              <div className="health-check" key={check.id} data-acknowledged={check.acknowledged || undefined}>
                <Status value={check.status} />
                <div style={{ minWidth: 0 }}>
                  <Text size="sm" c="var(--ink-2)">
                    {check.message}
                  </Text>
                  <Text size="xs" c="var(--ink-4)" mt={2}>
                    {formatDateTime(check.checkedAt)}
                    {check.acknowledged && <Trans> · Acknowledged, hidden from the header badge</Trans>}
                  </Text>
                </div>
                <div className="health-check-actions">
                  {check.url && (
                    <Button component={Link} to={check.url} size="xs" variant="subtle">
                      <Trans>Open</Trans>
                    </Button>
                  )}
                  {ISSUE.includes(check.status) && (
                    <Button
                      size="xs"
                      variant="subtle"
                      onClick={() => run('/checks/acknowledge', { id: check.id, acknowledged: !check.acknowledged })}
                    >
                      {check.acknowledged ? <Trans>Reopen</Trans> : <Trans>Acknowledge</Trans>}
                    </Button>
                  )}
                </div>
              </div>
            ))}
          </div>
        )
      })}
    </Paper>
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
  const { data, error } = useHealthData<FileDetail>(`/files/${id}`)
  const action = useHealthAction()
  // Automatic by default: the reviewer usually wants "get me a good copy", and picking a source by
  // hand also turns off the fallback, which is rarely what they meant.
  const [mapping, setMapping] = useState<string>(AUTOMATIC)
  // A negative size is how the scanner records "the file was not there".
  const gone = (data?.file.size ?? 0) < 0
  const pageCount = data?.analysis.pages.length ?? 0
  const sourceNames = data?.mappings.map((m) => m.sourceName).join(', ') || t`the series' sources`

  return (
    <Modal
      opened
      onClose={close}
      title={t`Review archive`}
      size="min(1060px, 94vw)"
      centered
      scrollAreaComponent={ScrollArea.Autosize}
    >
      <Stack gap="lg">
        {(error ?? action.error) && <Alert color="red">{(error ?? action.error)?.message}</Alert>}
        {!data ? (
          <Loader />
        ) : (
          <>
          {data.match && (
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
              <Paper withBorder radius="md" p="md">
                <Text fw={600} style={{ overflowWrap: 'anywhere' }}>
                  {data.file.relativePath}
                </Text>
                <Text size="sm" c="var(--ink-3)" mt={4}>
                  {bytes(data.file.size, t`Missing`)} · <Plural value={pageCount} one="# page" other="# pages" /> ·{' '}
                  {data.analysis.status} ·{' '}
                  {data.analysis.verified ? <Trans>contents read</Trans> : <Trans>indexed only</Trans>}
                </Text>
                <Text size="xs" c="var(--ink-4)" mt={4} style={{ overflowWrap: 'anywhere' }}>
                  SHA-256: {data.file.contentHash ?? <Trans>Unavailable</Trans>}
                </Text>
                <Text size="sm" c="var(--ink-3)" mt="sm">
                  <Trans>Affected chapters:</Trans>{' '}
                  {data.chapters.map((c) => c.number ?? c.title ?? c.id).join(', ') || <Trans>None linked</Trans>}
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
                      leftSection={<IconPhotoScan size={14} />}
                      onClick={() =>
                        action.mutate({ path: '/scans', body: { fileIds: [id], force: true, verify: true } })
                      }
                    >
                      <Trans>Verify this file</Trans>
                    </Button>
                  )}
                </Group>
              </Paper>

              {data.findings.map((f) => (
                <Paper key={f.id} withBorder radius="md" p="md">
                  <Group gap="sm" align="flex-start" wrap="nowrap">
                    <Status value={f.severity} />
                    <Text size="sm">{f.message}</Text>
                  </Group>
                  <Group mt="sm" gap="xs">
                    <Text size="xs" c="var(--ink-4)">
                      {f.state}
                    </Text>
                    {['acknowledged', 'ignored', 'open']
                      .filter((s) => s !== f.state)
                      .map((state) => (
                        <Button
                          key={state}
                          size="xs"
                          variant="subtle"
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
                </Paper>
              ))}

              {!data.analysis.verified && (
                <Alert color="gray">
                  <Trans>
                    This archive has only been indexed: what it says it holds is known, whether it still holds
                    it is not. Verifying reads every byte and checks it against the archive's own checksums. It
                    is also what produces the content hash that replacing or deleting this file checks against,
                    so those need it first.
                  </Trans>
                </Alert>
              )}
            </div>

            <div className="health-review-column">
              <Paper withBorder radius="md" p="md">
                <Title order={4} fz={15} mb="sm">
                  <Trans>Request replacement</Trans>
                </Title>
                <Select
                  allowDeselect={false}
                  value={mapping}
                  onChange={(value) => setMapping(value ?? AUTOMATIC)}
                  data={[
                    { value: AUTOMATIC, label: t`Automatic (source priority)` },
                    ...data.mappings.map((m) => {
                      const { sourceName, priority } = m
                      return { value: String(m.id), label: t`${sourceName} · priority ${priority}` }
                    }),
                  ]}
                />
                <Button
                  mt="sm"
                  fullWidth
                  disabled={data.chapters.length === 0 || data.mappings.length === 0}
                  loading={action.isPending}
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
              </Paper>

              <Paper withBorder radius="md" p="md">
                <Title order={4} fz={15} mb="sm">
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
                  color="red"
                  variant="light"
                  fullWidth
                  leftSection={<IconTrash size={16} />}
                  onClick={() =>
                    action.mutate(
                      { path: '/deletions/preview', body: { fileId: id, version: data.file.version } },
                      { onSuccess: (op) => openOperation(op.id) },
                    )
                  }
                >
                  {gone ? <Trans>Review record removal</Trans> : <Trans>Review permanent deletion</Trans>}
                </Button>
              </Paper>
            </div>
          </div>
          </>
        )}
      </Stack>
    </Modal>
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
  const { seriesId, seriesTitle, chapters, counterparts, label, recognized } = match
  const chapterCount = chapters.length
  const lowerLabel = label.toLowerCase()
  const importable = seriesId != null && chapterCount > 0 && counterparts.length === 0

  return (
    <Paper withBorder radius="md" p="md">
      <Group justify="space-between" align="center" wrap="nowrap" mb="sm">
        <Title order={4} fz={15}>
          <Trans>Not linked to any chapter</Trans>
        </Title>
        <Badge variant="light" color="gray">
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
        <Alert color="yellow">
          <Trans>
            The file name carries no chapter or volume number, so nothing can be matched to it. Rename it to
            the library's naming format and rescan, or link it by hand from the series' Files tab.
          </Trans>
        </Alert>
      )}

      {recognized && seriesId == null && (
        <Alert color="yellow">
          <Trans>
            This archive is not inside any series folder in its root, so there is no series to import it into.
            Move it into the right folder and rescan, or use the Import page to bring in the folder it lives in.
          </Trans>
        </Alert>
      )}

      {recognized && seriesId != null && chapterCount === 0 && (
        <Alert color="yellow">
          <Trans>
            {seriesTitle} has no {lowerLabel}. Refresh the series so the chapter exists, then import this
            archive.
          </Trans>
        </Alert>
      )}

      {importable && (
        <Stack gap="sm">
          <Alert color="blue">
            <Trans>
              No other file backs {lowerLabel}. Nothing has to be compared: importing adopts this archive and
              links it to the chapter.
            </Trans>
          </Alert>
          <Group gap="xs">
            <Button
              leftSection={<IconFileImport size={16} />}
              loading={action.isPending}
              onClick={() => action.mutate({ path: '/imports', body: { fileIds: [file.id] } })}
            >
              <Trans>Import this archive</Trans>
            </Button>
            <Button component={Link} to="/import" variant="subtle">
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
          <Alert color="yellow">
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
    </Paper>
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
    <Paper withBorder radius="md" p="md">
      <div className="health-compare">
        <div />
        <Text size="xs" fw={700} tt="uppercase" c="var(--ink-4)" style={{ letterSpacing: '0.05em' }}>
          <Trans>This archive</Trans>
        </Text>
        <Text size="xs" fw={700} tt="uppercase" c="var(--ink-4)" style={{ letterSpacing: '0.05em' }}>
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
        <Alert color="yellow" mt="md">
          <Trans>Byte-identical to the linked file. Importing gains nothing; delete one of them.</Trans>
        </Alert>
      )}

      {strip && analysis.pages.length !== counterpart.pages && (
        <Alert color="gray" mt="md">
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
        <Alert color="gray" mt="md">
          <Trans>
            The linked file has not been inventoried yet, so its pages cannot be shown. Run Scan files, then
            come back to compare them.
          </Trans>
        </Alert>
      )}
    </Paper>
  )
}

function OperationReview({ id, close }: { id: number; close: () => void }) {
  const { t } = useLingui()
  const { data, error } = useHealthData<OperationDetail>(`/operations/${id}`)
  const action = useHealthAction()
  const [confirm, setConfirm] = useState(false)
  const [reset, setReset] = useState(false)
  const chaptersCount = data?.chapters.length ?? 0
  const opStatus = data?.operation.status

  return (
    <Modal
      opened
      onClose={close}
      title={t`Operation #${id}`}
      size="min(1060px, 94vw)"
      centered
      scrollAreaComponent={ScrollArea.Autosize}
    >
      <Stack gap="lg">
        {(error ?? action.error) && <Alert color="red">{(error ?? action.error)?.message}</Alert>}
        {!data ? (
          <Loader />
        ) : (
          <div className="health-review">
            <div className="health-review-column">
              {data.operation.error && <Alert color="red">{data.operation.error}</Alert>}
              {data.operation.kind === 'delete' ? (
                <Alert color={data.file.size < 0 ? 'yellow' : 'red'}>
                  {data.file.size < 0 ? (
                    <Trans>
                      This file is already gone from disk, so nothing is deleted. It drops the record that
                      still links these chapters to it, so they stop reading as downloaded.
                    </Trans>
                  ) : (
                    <Trans>This permanently deletes the archive. Chapter records and reading history remain.</Trans>
                  )}{' '}
                  {data.chapters.some((c) => c.wanted) && (
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
                      <Paper key={candidate.chapterId} withBorder radius="md" p="md">
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
                          {candidate.analysis.pages.slice(0, 4).map((p, index) => (
                            <Image
                              key={p.name}
                              h={200}
                              fit="contain"
                              src={`/api/v1/health/operations/${id}/candidates/${candidate.chapterId}/pages/${index}`}
                              alt={`Candidate page ${index + 1}`}
                            />
                          ))}
                        </SimpleGrid>
                      </Paper>
                    )
                  })}
                </>
              )}
            </div>

            <div className="health-review-column">
              <Paper withBorder radius="md" p="md">
                <Status value={data.operation.status} />
                <Text fw={600} mt="sm" style={{ overflowWrap: 'anywhere' }}>
                  {data.file.relativePath}
                </Text>
                <Text size="sm" c="var(--ink-3)" mt={4}>
                  {bytes(data.file.size, t`Missing`)} ·{' '}
                  <Plural value={chaptersCount} one="# affected chapter" other="# affected chapters" />
                </Text>
              </Paper>

              <Paper withBorder radius="md" p="md">
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
                        color={data.operation.kind === 'delete' ? 'red' : 'brand'}
                        loading={action.isPending}
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
                  {!['completed', 'cancelled', 'failed'].includes(data.operation.status) && (
                    <Button
                      variant="default"
                      onClick={() => action.mutate({ path: `/operations/${id}/cancel` }, { onSuccess: close })}
                    >
                      <Trans>Cancel operation</Trans>
                    </Button>
                  )}
                  {['completed', 'cancelled', 'failed'].includes(data.operation.status) &&
                    data.operation.status !== 'review' && (
                      <Text size="sm" c="var(--ink-4)">
                        <Trans>This operation is {opStatus} and needs no further action.</Trans>
                      </Text>
                    )}
                </Stack>
              </Paper>
            </div>
          </div>
        )}
      </Stack>
    </Modal>
  )
}

function OptionsPanel() {
  const { t } = useLingui()
  const { data } = useHealthData<HealthOptions>('/options')
  const [draft, setDraft] = useState<HealthOptions | null>(null)
  const action = useHealthAction()
  const value = draft ?? data
  if (!value) return null
  const update = (patch: Partial<HealthOptions>) => setDraft({ ...value, ...patch })

  return (
    <Paper className="health-area-options" withBorder radius="lg" p="lg">
      <Stack>
        <Title order={3} fz={17}>
          <Trans>Health settings</Trans>
        </Title>
        {action.error && <Alert color="red">{action.error.message}</Alert>}
        <Switch
          label={t`Analyze new files and run daily reconciliation`}
          checked={value.automaticScanning}
          onChange={(e) => update({ automaticScanning: e.currentTarget.checked })}
        />
        <SimpleGrid cols={{ base: 1, sm: 2 }}>
          <NumberInput
            label={t`Daily scan hour (0-23)`}
            min={0}
            max={23}
            value={value.scanHour}
            onChange={(v) => update({ scanHour: Number(v) })}
          />
          <TextInput
            label={t`Timezone (empty uses server timezone)`}
            value={value.timeZone ?? ''}
            onChange={(e) => update({ timeZone: e.currentTarget.value || null })}
          />
          <NumberInput
            label={t`Pages decoded at once (0 = automatic)`}
            description={t`Scanning is almost entirely image decoding. Raise it to sweep the library faster, lower it to leave the CPU alone.`}
            min={0}
            max={32}
            value={value.scanWorkers}
            onChange={(v) => update({ scanWorkers: Number(v) })}
          />
          {(['warningPercent', 'errorPercent', 'warningGiB', 'errorGiB', 'backupDays'] as const).map((key) => (
            <NumberInput
              key={key}
              label={
                {
                  warningPercent: t`Low disk warning (%)`,
                  errorPercent: t`Low disk error (%)`,
                  warningGiB: t`Low disk warning (GiB)`,
                  errorGiB: t`Low disk error (GiB)`,
                  backupDays: t`Backup freshness (days)`,
                }[key]
              }
              min={0}
              value={value[key]}
              onChange={(v) => update({ [key]: Number(v) })}
            />
          ))}
        </SimpleGrid>
        <Button
          disabled={!draft}
          loading={action.isPending}
          onClick={() =>
            action.mutate({ path: '/options', method: 'PUT', body: value }, { onSuccess: () => setDraft(null) })
          }
        >
          <Trans>Save health settings</Trans>
        </Button>
      </Stack>
    </Paper>
  )
}

function CachePanel() {
  const { t } = useLingui()
  const cache = useImageCache()
  const rebuild = useRebuildImageCache()

  return (
    <Paper className="health-area-cache" withBorder radius="lg" p="lg">
      <Stack>
        <Title order={3} fz={17}>
          <Trans>Image cache and backups</Trans>
        </Title>
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
        {(cache.error ?? rebuild.error) && <Alert color="red">{(cache.error ?? rebuild.error)?.message}</Alert>}
        <Group gap="xs">
          <Button
            variant="default"
            loading={rebuild.isPending || cache.data?.status.running}
            onClick={() => rebuild.mutate(false)}
          >
            <Trans>Rebuild missing images</Trans>
          </Button>
          <Button component={Link} to="/settings?tab=system" variant="subtle">
            <Trans>Backup and cache tools</Trans>
          </Button>
        </Group>
      </Stack>
    </Paper>
  )
}
