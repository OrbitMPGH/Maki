import { Fragment, useEffect, useMemo, useState } from 'react'
import {
  ActionIcon,
  Anchor,
  Badge,
  Button,
  Group,
  Loader,
  Modal,
  Pagination,
  Progress,
  Stack,
  Table,
  Tabs,
  Text,
  Title,
  Tooltip,
} from '@mantine/core'
import {
  IconArrowBackUp,
  IconArrowBarToUp,
  IconArrowDown,
  IconArrowUp,
  IconChevronDown,
  IconChevronRight,
  IconHistory,
  IconRefresh,
  IconSearch,
  IconTrash,
  IconX,
} from '@tabler/icons-react'
import { Link, useSearchParams } from 'react-router-dom'
import { notifications } from '@mantine/notifications'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { plural, t as now } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import {
  useClearQueue,
  useQueue,
  useQueueHistory,
  useRemoveQueueItem,
  useReorderQueue,
  useRetryQueueItem,
} from '../api/hooks'
import {
  isVolumeSearchStarted,
  upgradeReasonLabel,
  volumeSearchResultText,
  useCutoffUnmet,
  useRevertUpgrade,
  useRevertUpgradeGroup,
  useRunUpgradeScan,
  useTorrentProposals,
  useUpgradeHistory,
  useUpgradeSettings,
  useUpgradesSummary,
  useVolumeSearch,
  QUALITY_TIER_LABELS,
} from '../api/upgrades'
import type {
  CutoffUnmetRowDto,
  QualitySnapshotDto,
  UpgradeHistoryRowDto,
  UpgradeQueueInfoDto,
} from '../api/upgrades'
import { ApiError } from '../api/client'
import { useAuth } from '../auth/AuthProvider'
import { ImportReviewModal } from '../components/ImportReviewModal'
import { FileQualityBadge } from '../components/series/FileQualityBadge'
import { TorrentProposalCard } from '../components/upgrades/TorrentProposalCard'
import { EmptyState } from '../components/ui/EmptyState'
import { PageHeader } from '../components/ui/PageHeader'
import { Panel } from '../components/ui/Panel'
import { TableSkeleton } from '../components/ui/TableSkeleton'
import { FigureStrip } from '../components/ui/FigureStrip'
import { StatusDot } from '../components/ui/StatusDot'
import { isQueueActive, needsImportReview, queueStatusVisual, statusToken } from '../components/ui/status'
import { queueErrorMessage, queueItemLabel, queueOriginOrUnknown } from '../api/queue'
import { useLabel } from '../i18n-context'
import { useSourceLabel } from '../sourceLabels'
import { formatBytes, formatDate, formatDateTime, formatTime } from '../format'
import { SurfaceFrame } from '../components/ui/SurfaceFrame'

const HISTORY_PAGE_SIZE = 25
const UPGRADES_PAGE_SIZE = 25
const UPGRADE_HISTORY_PAGE_SIZE = 25

/** "Ch.148", "Ch.148 - Title", or the chapter's own title/"One-shot" when it has no number. */
function cutoffRowChapterLabel(row: CutoffUnmetRowDto): string {
  const { chapterNumber, chapterTitle } = row
  if (chapterNumber === null) return chapterTitle ?? now`One-shot`
  return chapterTitle ? now`Ch.${chapterNumber} - ${chapterTitle}` : now`Ch.${chapterNumber}`
}

/** Same shape as `cutoffRowChapterLabel`, for an `UpgradeHistoryRowDto`. */
function upgradeHistoryChapterLabel(row: UpgradeHistoryRowDto): string {
  const { chapterNumber, chapterTitle } = row
  if (chapterNumber === null) return chapterTitle ?? now`One-shot`
  return chapterTitle ? now`Ch.${chapterNumber} - ${chapterTitle}` : now`Ch.${chapterNumber}`
}

/**
 * "MangaDex Aggregator 969px (0)": source, tier, width and score, so a same-tier win (moving from
 * one aggregator to a better-scoring one) still reads as a change instead of repeating itself.
 */
function snapshotLabel(
  renderLabel: (m: MessageDescriptor) => string,
  sourceLabel: (key: string) => string,
  snapshot: QualitySnapshotDto,
): string {
  const tierLabel = renderLabel(QUALITY_TIER_LABELS[snapshot.tier])
  const { medianWidth, score } = snapshot
  const source = snapshot.sourceName ? sourceLabel(snapshot.sourceName) : null
  if (source && medianWidth != null) return now`${source} ${tierLabel} ${medianWidth}px (${score})`
  if (source) return now`${source} ${tierLabel} (${score})`
  if (medianWidth != null) return now`${tierLabel} ${medianWidth}px (${score})`
  return now`${tierLabel} (${score})`
}

/** "MangaDex Aggregator 969px (0) to MangaFire Aggregator 969px (5)". */
function snapshotTransitionText(
  renderLabel: (m: MessageDescriptor) => string,
  sourceLabel: (key: string) => string,
  before: QualitySnapshotDto,
  after: QualitySnapshotDto,
): string {
  const beforeLabel = snapshotLabel(renderLabel, sourceLabel, before)
  const afterLabel = snapshotLabel(renderLabel, sourceLabel, after)
  return now`${beforeLabel} to ${afterLabel}`
}

/** Same as `snapshotTransitionText`, from the before snapshot and whichever after/predicted one has. */
function upgradeTransitionLabel(
  renderLabel: (m: MessageDescriptor) => string,
  sourceLabel: (key: string) => string,
  upgrade: UpgradeQueueInfoDto,
): string | null {
  const { before, replacedFiles } = upgrade
  const target = upgrade.after ?? upgrade.predicted
  if (before && target) return snapshotTransitionText(renderLabel, sourceLabel, before, target)
  if (replacedFiles != null) return plural(replacedFiles, { one: 'Replaces # file', other: 'Replaces # files' })
  return null
}

interface UpgradeHistoryEntry {
  key: string
  groupId: string | null
  rows: UpgradeHistoryRowDto[]
}

/** Rows that share a `groupId` are one volume replacement; the list itself is flat. */
function groupUpgradeHistory(rows: UpgradeHistoryRowDto[]): UpgradeHistoryEntry[] {
  const entries: UpgradeHistoryEntry[] = []
  const byGroup = new Map<string, UpgradeHistoryEntry>()
  for (const row of rows) {
    if (!row.groupId) {
      entries.push({ key: `row-${row.id}`, groupId: null, rows: [row] })
      continue
    }
    const existing = byGroup.get(row.groupId)
    if (existing) {
      existing.rows.push(row)
      continue
    }
    const entry = { key: `group-${row.groupId}`, groupId: row.groupId, rows: [row] }
    byGroup.set(row.groupId, entry)
    entries.push(entry)
  }
  return entries
}

function upgradeReasonText(renderLabel: (m: MessageDescriptor) => string, reason: string | null): string | null {
  if (!reason) return null
  return upgradeReasonLabel(renderLabel, reason)
}

/** "Not an upgrade: <reason>" for a rejected upgrade queue/history row, else null. */
function upgradeRejectionText(
  renderLabel: (m: MessageDescriptor) => string,
  upgrade: UpgradeQueueInfoDto | null,
): string | null {
  if (!upgrade || upgrade.outcome !== 'rejected') return null
  const reason = upgradeReasonText(renderLabel, upgrade.reason)
  return reason ? now`Not an upgrade: ${reason}` : null
}

type ActivityTab = 'queue' | 'upgrades'

export default function ActivityPage() {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const sourceLabel = useSourceLabel()
  const { data: queue } = useQueue()
  const retry = useRetryQueueItem()
  const remove = useRemoveQueueItem()
  const reorder = useReorderQueue()
  const clear = useClearQueue()
  const { can } = useAuth()
  const canManageQueue = can('ManageDownloadQueue')
  const isAdmin = can('Admin')
  const revertUpgrade = useRevertUpgrade()
  const revertGroup = useRevertUpgradeGroup()
  const runScan = useRunUpgradeScan()
  const runVolumeSearch = useVolumeSearch()
  const canDownload = can('DownloadChapters')

  // URL-synced like SeriesDetailPage's chapters/files/details tabs, so a link to Activity can point
  // straight at Upgrades and a refresh doesn't bounce back to Queue.
  const [searchParams, setSearchParams] = useSearchParams()
  const requestedTab = searchParams.get('tab')
  const tab: ActivityTab = requestedTab === 'upgrades' ? 'upgrades' : 'queue'
  const changeTab = (value: string | null) => {
    if (!value) return
    const next = new URLSearchParams(searchParams)
    if (value === 'queue') next.delete('tab')
    else next.set('tab', value)
    setSearchParams(next, { replace: true })
  }

  const [historyPage, setHistoryPage] = useState(1)
  const [clearConfirmOpen, setClearConfirmOpen] = useState(false)
  const [reviewing, setReviewing] = useState<number | null>(null)
  const { data: history } = useQueueHistory(historyPage, HISTORY_PAGE_SIZE)
  const historyPageCount = history ? Math.ceil(history.total / HISTORY_PAGE_SIZE) : 0

  const [upgradesPage, setUpgradesPage] = useState(1)
  const upgradesTabActive = tab === 'upgrades'
  // Gated on the tab being open: an instance-wide cutoff evaluation is real work, and Queue is the
  // tab most people leave open, so it shouldn't keep re-running one in the background.
  const { data: upgradesSummary } = useUpgradesSummary(upgradesTabActive)
  const { data: upgradeSettings } = useUpgradeSettings()
  const { data: cutoffUnmet } = useCutoffUnmet(upgradesPage, UPGRADES_PAGE_SIZE, undefined, upgradesTabActive)
  const upgradesPageCount = cutoffUnmet ? Math.ceil(cutoffUnmet.total / UPGRADES_PAGE_SIZE) : 0

  const [upgradeHistoryPage, setUpgradeHistoryPage] = useState(1)
  const { data: upgradeHistory } = useUpgradeHistory(
    upgradeHistoryPage,
    UPGRADE_HISTORY_PAGE_SIZE,
    undefined,
    upgradesTabActive,
  )
  const upgradeHistoryPageCount = upgradeHistory ? Math.ceil(upgradeHistory.total / UPGRADE_HISTORY_PAGE_SIZE) : 0
  const historyEntries = useMemo(() => groupUpgradeHistory(upgradeHistory?.rows ?? []), [upgradeHistory])
  const [expandedGroups, setExpandedGroups] = useState<Set<string>>(new Set())
  const toggleGroup = (groupId: string) =>
    setExpandedGroups((prev) => {
      const next = new Set(prev)
      if (!next.delete(groupId)) next.add(groupId)
      return next
    })
  // The proposals list is small, unlike the cutoff evaluation behind the summary, so the tab badge
  // can be live while Queue is showing.
  const { data: proposals } = useTorrentProposals(undefined, canDownload)
  const pendingProposals = proposals?.length ?? 0

  const runUpgradeScan = () =>
    runScan.mutate(undefined, {
      onSuccess: () => {
        notifications.show({
          message: now`Scan started`,
          color: 'var(--ok)',
        })
      },
      onError: (error) => {
        notifications.show({
          message:
            error instanceof ApiError && error.status === 409
              ? now`A scan is already running`
              : now`Couldn't start the scan`,
          color: 'var(--danger)',
        })
      },
    })

  const onRevertError = (error: unknown) => {
    const code = error instanceof ApiError ? error.code : null
    const message =
      code === 'error.upgrades.alreadyReverted'
        ? now`This upgrade was already reverted`
        : code === 'error.upgrades.trashGone'
          ? now`The trashed copy of this file is gone`
          : code === 'error.upgrades.notLatest'
            ? now`A newer upgrade has replaced this file since`
            : code === 'error.upgrades.revertMoveFailed'
              ? now`A file could not be moved back, nothing was changed`
              : now`Could not revert this upgrade`
    notifications.show({ message, color: 'var(--danger)' })
  }

  const revertUpgradeRow = (historyId: number) => revertUpgrade.mutate(historyId, { onError: onRevertError })
  const revertGroupRow = (groupId: string) => revertGroup.mutate(groupId, { onError: onRevertError })

  const runUpgradeVolumeSearch = () =>
    runVolumeSearch.mutate(undefined, {
      onSuccess: (result) => {
        notifications.show({
          message: isVolumeSearchStarted(result) ? now`Volume search started` : volumeSearchResultText(renderLabel, result),
          color: 'var(--ok)',
        })
      },
      onError: (error) => {
        notifications.show({
          message:
            error instanceof ApiError && error.status === 409
              ? now`A scan is already running`
              : now`Couldn't start the volume search`,
          color: 'var(--danger)',
        })
      },
    })

  // The list can shrink out from under the current page (a profile edit, a file getting upgraded
  // elsewhere), which would otherwise strand the view on a page past the end showing an empty table
  // with no pager to get back.
  useEffect(() => {
    if (upgradesPageCount > 0 && upgradesPage > upgradesPageCount) setUpgradesPage(upgradesPageCount)
  }, [upgradesPageCount, upgradesPage])

  useEffect(() => {
    if (upgradeHistoryPageCount > 0 && upgradeHistoryPage > upgradeHistoryPageCount) {
      setUpgradeHistoryPage(upgradeHistoryPageCount)
    }
  }, [upgradeHistoryPageCount, upgradeHistoryPage])

  const queueItems = useMemo(() => queue?.items ?? [], [queue])
  const truncated = queue ? queue.total > queueItems.length : false
  const shownQueueCount = queueItems.length
  const totalQueueCount = queue?.total ?? 0

  const moveItem = (index: number, direction: -1 | 1) => {
    const target = index + direction
    if (target < 0 || target >= queueItems.length) {
      return
    }
    const ids = queueItems.map((q) => q.id)
    ;[ids[index], ids[target]] = [ids[target], ids[index]]
    reorder.mutate(ids)
  }

  const moveToTop = (index: number) => {
    if (index === 0) {
      return
    }
    const ids = queueItems.map((q) => q.id)
    const [id] = ids.splice(index, 1)
    ids.unshift(id)
    reorder.mutate(ids)
  }

  const stats = useMemo(
    () => ({
      active: queueItems.filter((q) => isQueueActive(q.status)).length,
      queued: queueItems.filter((q) => q.status === 'Queued').length,
      review: queueItems.filter((q) => needsImportReview(q.status)).length,
      failed: queueItems.filter((q) => q.status === 'Failed').length,
    }),
    [queueItems],
  )

  return (
    <SurfaceFrame pageStyle="operational">
      <PageHeader
        compact
        title={t`Activity`}
        description={t`Live download queue: pages are fetched, validated and packaged into CBZ files two at a time.`}
        actions={
          canManageQueue && queue && queue.total > 0 ? (
            <Button color="var(--danger)" variant="light" leftSection={<IconTrash size={16} />} onClick={() => setClearConfirmOpen(true)}>
              <Trans>Clear queue</Trans>
            </Button>
          ) : undefined
        }
      />

      <Tabs
        value={tab}
        variant="unstyled"
        classNames={{ list: 'series-tabs page-tabs', tab: 'series-tab' }}
        onChange={changeTab}
        keepMounted={false}
      >
        <Tabs.List mb="md">
          <Tabs.Tab value="queue"><Trans>Queue</Trans></Tabs.Tab>
          <Tabs.Tab value="upgrades">
            <Trans>Upgrades</Trans>
            {pendingProposals > 0 && <span className="series-tab-count tnum">{pendingProposals}</span>}
          </Tabs.Tab>
        </Tabs.List>

        <Tabs.Panel value="queue">
      <FigureStrip
        loading={!queue}
        figures={[
          { label: t`In progress`, value: stats.active, tone: 'info' },
          { label: t`Queued`, value: stats.queued },
          { label: t`Needs review`, value: stats.review, tone: 'warn' },
          { label: t`Failed`, value: stats.failed, tone: 'danger' },
        ]}
      />

      {!queue ? (
        <TableSkeleton columns={5} rows={4} />
      ) : queueItems.length === 0 ? (
        <EmptyState
          compact
          mood="asleep"
          title={t`Nothing in the queue`}
          description={t`Queued and downloading chapters show up here. Trigger a search from a series page or the library.`}
        />
      ) : (
        <Panel p={0} className="table-panel">
          <Table.ScrollContainer minWidth={720}>
            <Table className="panel-table activity-queue-table" verticalSpacing="sm">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>
                    <Trans>Series</Trans>
                  </Table.Th>
                  <Table.Th>
                    <Trans>Chapter</Trans>
                  </Table.Th>
                  <Table.Th data-priority="low">
                    <Trans>Source</Trans>
                  </Table.Th>
                  <Table.Th>
                    <Trans>Progress</Trans>
                  </Table.Th>
                  <Table.Th>
                    <Trans>Status</Trans>
                  </Table.Th>
                  <Table.Th />
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {queueItems.map((q, index) => {
                  const visual = queueStatusVisual(q.status)
                  const reorderable = q.status === 'Queued' || q.status === 'RateLimited'
                  const { retryCount, nextAttempt } = q
                  const nextAttemptTime = nextAttempt ? formatTime(nextAttempt) : null
                  const retryInfo =
                    q.status === 'Failed' && retryCount > 0
                      ? nextAttemptTime
                        ? t`Retried ${retryCount}x - next attempt ${nextAttemptTime}`
                        : t`Retried ${retryCount}x`
                      : null
                  const failure = queueErrorMessage(q, renderLabel)
                  const tooltipLabel =
                    [failure, retryInfo].filter(Boolean).join(' - ') || renderLabel(visual.label)
                  const isUpgradeOrigin = queueOriginOrUnknown(q.origin) === 'upgrade'
                  const rejectionText = q.upgrade ? upgradeRejectionText(renderLabel, q.upgrade) : null
                  const transitionText = q.upgrade ? upgradeTransitionLabel(renderLabel, sourceLabel, q.upgrade) : null
                  return (
                    <Table.Tr key={q.id}>
                      <Table.Td>
                        <Text
                          component={Link}
                          to={`/series/${q.seriesId}`}
                          size="sm"
                          fw={600}
                          c="brand.4"
                          lineClamp={1}
                        >
                          {q.seriesTitle}
                        </Text>
                      </Table.Td>
                      <Table.Td>
                        <Text size="sm" className="tnum">
                          {queueItemLabel(q)}
                        </Text>
                      </Table.Td>
                      <Table.Td data-priority="low">
                        {q.status === 'Resolving' ? (
                          <Group gap={6} wrap="nowrap">
                            <Loader size="xs" />
                            <Text size="sm" c="var(--ink-3)">
                              <Trans>Finding source</Trans>
                            </Text>
                          </Group>
                        ) : (
                          <Stack gap={2}>
                            <Group gap={4} wrap="nowrap">
                              <Text size="sm" c="var(--ink-3)">
                                {sourceLabel(q.sourceName)}
                              </Text>
                              {q.upgrade && (isUpgradeOrigin || q.upgrade.force) && (
                                <Badge size="xs" variant="light" color="var(--brand)">
                                  {isUpgradeOrigin ? <Trans>Upgrade</Trans> : <Trans>Replace</Trans>}
                                </Badge>
                              )}
                            </Group>
                            {transitionText && (
                              <Text size="xs" c="var(--ink-3)">
                                {transitionText}
                              </Text>
                            )}
                          </Stack>
                        )}
                      </Table.Td>
                      <Table.Td>
                        {q.pagesTotal > 0 ? (
                          <Group gap="xs" wrap="nowrap">
                            <Progress
                              value={(q.pagesDone / q.pagesTotal) * 100}
                              style={{ flex: 1 }}
                              radius="xl"
                              animated={q.status === 'Downloading'}
                              color={q.status === 'Failed' ? 'var(--danger)' : 'brand'}
                            />
                            <Text size="xs" c="var(--ink-3)" w={52} className="tnum" ta="right">
                              {q.pagesDone}/{q.pagesTotal}
                            </Text>
                          </Group>
                        ) : (
                          <Text size="xs" c="var(--ink-3)">
                            -
                          </Text>
                        )}
                      </Table.Td>
                      <Table.Td>
                        <Tooltip label={tooltipLabel} withArrow disabled={!failure && !retryInfo || !!rejectionText}>
                          <StatusDot tone={rejectionText ? 'warn' : statusToken(visual.color)} live={isQueueActive(q.status)}>
                            {rejectionText ?? renderLabel(visual.label)}
                          </StatusDot>
                        </Tooltip>
                      </Table.Td>
                      <Table.Td>
                        <Group gap={4} wrap="nowrap" justify="flex-end">
                          {reorderable && (
                            <>
                              <Tooltip label={t`Move to top`} withArrow>
                                <ActionIcon
                                  variant="subtle"
                                  color="var(--neutral)"
                                  disabled={index === 0 || reorder.isPending}
                                  onClick={() => moveToTop(index)}
                                  aria-label={t`Move to top of queue`}
                                >
                                  <IconArrowBarToUp size={16} />
                                </ActionIcon>
                              </Tooltip>
                              <Tooltip label={t`Move up`} withArrow>
                                <ActionIcon
                                  variant="subtle"
                                  color="var(--neutral)"
                                  disabled={index === 0 || reorder.isPending}
                                  onClick={() => moveItem(index, -1)}
                                  aria-label={t`Move up in queue`}
                                >
                                  <IconArrowUp size={16} />
                                </ActionIcon>
                              </Tooltip>
                              <Tooltip label={t`Move down`} withArrow>
                                <ActionIcon
                                  variant="subtle"
                                  color="var(--neutral)"
                                  disabled={index === queueItems.length - 1 || reorder.isPending}
                                  onClick={() => moveItem(index, 1)}
                                  aria-label={t`Move down in queue`}
                                >
                                  <IconArrowDown size={16} />
                                </ActionIcon>
                              </Tooltip>
                            </>
                          )}
                          {needsImportReview(q.status) && canManageQueue && (
                            <Button size="compact-sm" variant="light" color="var(--warn)" onClick={() => setReviewing(q.id)}>
                              <Trans>Review</Trans>
                            </Button>
                          )}
                          {q.status === 'Failed' && (
                            <Tooltip label={t`Retry`} withArrow>
                              <ActionIcon
                                variant="subtle"
                                color="var(--neutral)"
                                onClick={() => retry.mutate(q.id)}
                                aria-label={t`Retry download`}
                              >
                                <IconRefresh size={16} />
                              </ActionIcon>
                            </Tooltip>
                          )}
                          <Tooltip label={t`Remove`} withArrow>
                            <ActionIcon
                              variant="subtle"
                              color="var(--danger)"
                              onClick={() => remove.mutate(q.id)}
                              aria-label={t`Remove from queue`}
                            >
                              <IconX size={16} />
                            </ActionIcon>
                          </Tooltip>
                        </Group>
                      </Table.Td>
                    </Table.Tr>
                  )
                })}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        </Panel>
      )}

      {truncated && (
        <Text size="xs" c="var(--ink-3)" mt="xs">
          <Trans>
            Showing {shownQueueCount} of {totalQueueCount} queued items. The rest are still queued
            and will download, they're just not listed here.
          </Trans>
        </Text>
      )}

      <ImportReviewModal queueItemId={reviewing} onClose={() => setReviewing(null)} />

      <Modal
        opened={clearConfirmOpen}
        onClose={() => setClearConfirmOpen(false)}
        title={
          <Plural
            value={queue?.total ?? 0}
            one="Clear # queued download?"
            other="Clear # queued downloads?"
          />
        }
        centered
      >
        <Stack gap="sm">
          <Text size="sm">
            <Trans>Pending downloads will be removed. Downloads already in progress will be cancelled.</Trans>
          </Text>
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setClearConfirmOpen(false)}>
              <Trans>Cancel</Trans>
            </Button>
            <Button
              color="var(--danger-fill)"
              loading={clear.isPending}
              onClick={() => {
                clear.mutate(undefined, { onSuccess: () => setClearConfirmOpen(false) })
              }}
            >
              <Trans>Clear queue</Trans>
            </Button>
          </Group>
        </Stack>
      </Modal>

      <Stack gap="sm" mt="xl">
        <Group gap="xs">
          <IconHistory size={18} />
          <Title order={4}>
            <Trans>History</Trans>
          </Title>
        </Group>

        {!history ? (
          <TableSkeleton columns={6} />
        ) : history.items.length === 0 ? (
          <EmptyState
            compact
            title={t`No history yet`}
            description={t`Completed and cancelled downloads show up here.`}
          />
        ) : (
          <>
            <Panel p={0} className="table-panel">
              <Table.ScrollContainer minWidth={640}>
                <Table className="panel-table activity-history-table" verticalSpacing="sm">
                  <Table.Thead>
                    <Table.Tr>
                      <Table.Th>
                        <Trans>Series</Trans>
                      </Table.Th>
                      <Table.Th>
                        <Trans>Chapter</Trans>
                      </Table.Th>
                      <Table.Th data-priority="low">
                        <Trans>Source</Trans>
                      </Table.Th>
                      <Table.Th>
                        <Trans>Status</Trans>
                      </Table.Th>
                      <Table.Th data-priority="low">
                        <Trans>Completed</Trans>
                      </Table.Th>
                      <Table.Th />
                    </Table.Tr>
                  </Table.Thead>
                  <Table.Tbody>
                    {history.items.map((q) => {
                      const visual = queueStatusVisual(q.status)
                      const isUpgradeOrigin = queueOriginOrUnknown(q.origin) === 'upgrade'
                      const rejectionText = q.upgrade ? upgradeRejectionText(renderLabel, q.upgrade) : null
                      const transitionText = q.upgrade ? upgradeTransitionLabel(renderLabel, sourceLabel, q.upgrade) : null
                      const showRevert =
                        q.upgrade?.outcome === 'applied' &&
                        (q.upgrade.historyId != null || q.upgrade.historyGroupId != null)
                      return (
                        <Table.Tr key={q.id}>
                          <Table.Td>
                            <Text
                              component={Link}
                              to={`/series/${q.seriesId}`}
                              size="sm"
                              fw={600}
                              c="brand.4"
                              lineClamp={1}
                            >
                              {q.seriesTitle}
                            </Text>
                          </Table.Td>
                          <Table.Td>
                            <Text size="sm" className="tnum">
                              {queueItemLabel(q)}
                            </Text>
                          </Table.Td>
                          <Table.Td data-priority="low">
                            <Stack gap={2}>
                              <Group gap={4} wrap="nowrap">
                                <Text size="sm" c="var(--ink-3)">
                                  {sourceLabel(q.sourceName)}
                                </Text>
                                {q.upgrade && (isUpgradeOrigin || q.upgrade.force) && (
                                  <Badge size="xs" variant="light" color="var(--brand)">
                                    {isUpgradeOrigin ? <Trans>Upgrade</Trans> : <Trans>Replace</Trans>}
                                  </Badge>
                                )}
                              </Group>
                              {transitionText && (
                                <Text size="xs" c="var(--ink-3)">
                                  {transitionText}
                                </Text>
                              )}
                            </Stack>
                          </Table.Td>
                          <Table.Td>
                            <StatusDot tone={rejectionText ? 'warn' : statusToken(visual.color)}>
                              {rejectionText ?? renderLabel(visual.label)}
                            </StatusDot>
                          </Table.Td>
                          <Table.Td data-priority="low">
                            <Text size="xs" c="var(--ink-3)" className="tnum" style={{ whiteSpace: 'nowrap' }}>
                              {q.completedAt ? formatDateTime(q.completedAt) : '-'}
                            </Text>
                          </Table.Td>
                          <Table.Td>
                            {showRevert &&
                              (q.upgrade!.reverted ? (
                                <Text size="xs" c="var(--ink-3)">
                                  <Trans>Reverted</Trans>
                                </Text>
                              ) : (
                                <Tooltip
                                  label={q.upgrade!.trashAvailable ? t`Revert this upgrade` : t`The trashed copy is gone`}
                                  withArrow
                                >
                                  <ActionIcon
                                    variant="subtle"
                                    color="var(--neutral)"
                                    disabled={!q.upgrade!.trashAvailable}
                                    loading={revertUpgrade.isPending || revertGroup.isPending}
                                    onClick={() =>
                                      q.upgrade!.historyGroupId
                                        ? revertGroupRow(q.upgrade!.historyGroupId)
                                        : revertUpgradeRow(q.upgrade!.historyId!)
                                    }
                                    aria-label={t`Revert this upgrade`}
                                  >
                                    <IconArrowBackUp size={16} />
                                  </ActionIcon>
                                </Tooltip>
                              ))}
                          </Table.Td>
                        </Table.Tr>
                      )
                    })}
                  </Table.Tbody>
                </Table>
              </Table.ScrollContainer>
            </Panel>

            {historyPageCount > 1 && (
              <Group justify="center">
                <Pagination total={historyPageCount} value={historyPage} onChange={setHistoryPage} />
              </Group>
            )}
          </>
        )}
      </Stack>
        </Tabs.Panel>

        <Tabs.Panel value="upgrades">
          {!upgradesSummary ? (
            <TableSkeleton columns={6} rows={4} />
          ) : (
            <Stack gap="xl">
              <Group justify="space-between" align="flex-start" wrap="wrap" gap="sm">
                <Text size="sm" c="var(--ink-3)">
                  {(() => {
                    // The daily job is the only thing that writes `lastScanDate`; a per-series scan
                    // from this same button doesn't touch it, so the label says which one this is.
                    const lastScanText = upgradesSummary.lastScanDate
                      ? formatDate(upgradesSummary.lastScanDate)
                      : t`never`
                    const trashSize = formatBytes(upgradesSummary.trashBytes)
                    const trashFilesText = plural(upgradesSummary.trashFiles, { one: '# file', other: '# files' })
                    const retentionDays = upgradeSettings?.trashRetentionDays
                    const lastVolumeSearchText = upgradesSummary.lastVolumeSearchDate
                      ? formatDate(upgradesSummary.lastVolumeSearchDate)
                      : t`never`
                    return (
                      <>
                        <Trans>Last library scan: {lastScanText}</Trans>
                        {' · '}
                        <Trans>Last volume search: {lastVolumeSearchText}</Trans>
                        {' · '}
                        <Trans>Trash: {trashSize} across {trashFilesText}</Trans>
                        {retentionDays != null && (
                          <>
                            {', '}
                            <Plural value={retentionDays} one="kept # day" other="kept # days" />
                          </>
                        )}
                      </>
                    )
                  })()}
                </Text>
                {isAdmin && (
                  <Group gap="xs">
                    <Button
                      size="xs"
                      variant="default"
                      leftSection={<IconSearch size={14} />}
                      loading={runVolumeSearch.isPending}
                      disabled={upgradesSummary.scanRunning}
                      onClick={runUpgradeVolumeSearch}
                    >
                      <Trans>Search volumes now</Trans>
                    </Button>
                    <Button
                      size="xs"
                      variant="default"
                      leftSection={<IconRefresh size={14} />}
                      loading={runScan.isPending}
                      disabled={upgradesSummary.scanRunning}
                      onClick={runUpgradeScan}
                    >
                      {upgradesSummary.scanRunning ? <Trans>Scan running…</Trans> : <Trans>Scan now</Trans>}
                    </Button>
                  </Group>
                )}
              </Group>

              {proposals && proposals.length > 0 && (
                <Stack gap="sm">
                  <Title order={4}>
                    <Trans>Volume releases waiting for you</Trans>
                  </Title>
                  {proposals.map((proposal) => (
                    <TorrentProposalCard key={proposal.id} proposal={proposal} />
                  ))}
                </Stack>
              )}

              <Stack gap="sm">
                <Title order={4}>
                  <Trans>Recent upgrades</Trans>
                </Title>
                {!upgradeHistory ? (
                  <TableSkeleton columns={5} />
                ) : upgradeHistory.rows.length === 0 ? (
                  <EmptyState
                    compact
                    title={t`No upgrades yet`}
                    description={t`Files replaced by the automatic upgrader show up here.`}
                  />
                ) : (
                  <>
                    <Panel p={0} className="table-panel">
                      <Table.ScrollContainer minWidth={780}>
                        <Table className="panel-table activity-upgrade-history-table" verticalSpacing="sm">
                          <Table.Thead>
                            <Table.Tr>
                              <Table.Th><Trans>Series</Trans></Table.Th>
                              <Table.Th><Trans>Chapter</Trans></Table.Th>
                              <Table.Th><Trans>Change</Trans></Table.Th>
                              <Table.Th data-priority="low"><Trans>When</Trans></Table.Th>
                              <Table.Th />
                            </Table.Tr>
                          </Table.Thead>
                          <Table.Tbody>
                            {historyEntries.map((entry) => {
                              const [first] = entry.rows
                              const { groupId } = entry
                              if (!groupId) {
                                return (
                                  <Table.Tr key={entry.key}>
                                    <Table.Td>
                                      <Text
                                        component={Link}
                                        to={`/series/${first.seriesId}`}
                                        size="sm"
                                        fw={600}
                                        c="brand.4"
                                        lineClamp={1}
                                      >
                                        {first.seriesTitle}
                                      </Text>
                                    </Table.Td>
                                    <Table.Td>
                                      <Text size="sm" className="tnum">
                                        {upgradeHistoryChapterLabel(first)}
                                      </Text>
                                    </Table.Td>
                                    <Table.Td>
                                      <Text size="sm" c="var(--ink-3)">
                                        {snapshotTransitionText(renderLabel, sourceLabel, first.before, first.after)}
                                      </Text>
                                    </Table.Td>
                                    <Table.Td data-priority="low">
                                      <Text size="xs" c="var(--ink-3)" className="tnum" style={{ whiteSpace: 'nowrap' }}>
                                        {formatDateTime(first.createdAt)}
                                      </Text>
                                    </Table.Td>
                                    <Table.Td>
                                      {first.revertedAt ? (
                                        <Text size="xs" c="var(--ink-3)">
                                          <Trans>Reverted</Trans>
                                        </Text>
                                      ) : (
                                        <Tooltip
                                          label={first.trashAvailable ? t`Revert this upgrade` : t`The trashed copy is gone`}
                                          withArrow
                                        >
                                          <ActionIcon
                                            variant="subtle"
                                            color="var(--neutral)"
                                            disabled={!first.trashAvailable}
                                            loading={revertUpgrade.isPending}
                                            onClick={() => revertUpgradeRow(first.id)}
                                            aria-label={t`Revert this upgrade`}
                                          >
                                            <IconArrowBackUp size={16} />
                                          </ActionIcon>
                                        </Tooltip>
                                      )}
                                    </Table.Td>
                                  </Table.Tr>
                                )
                              }
                              const expanded = expandedGroups.has(groupId)
                              const fileCount = Math.max(first.groupSize, entry.rows.length)
                              const filesText = plural(fileCount, { one: '# file', other: '# files' })
                              const { fileName } = first
                              const reverted = entry.rows.every((r) => r.revertedAt)
                              const revertable = entry.rows.every((r) => r.trashAvailable)
                              return (
                                <Fragment key={entry.key}>
                                  <Table.Tr>
                                    <Table.Td>
                                      <Text
                                        component={Link}
                                        to={`/series/${first.seriesId}`}
                                        size="sm"
                                        fw={600}
                                        c="brand.4"
                                        lineClamp={1}
                                      >
                                        {first.seriesTitle}
                                      </Text>
                                    </Table.Td>
                                    <Table.Td>
                                      <Group gap={4} wrap="nowrap">
                                        <ActionIcon
                                          variant="subtle"
                                          color="var(--neutral)"
                                          size="sm"
                                          onClick={() => toggleGroup(groupId)}
                                          aria-label={expanded ? t`Hide replaced files` : t`Show replaced files`}
                                          aria-expanded={expanded}
                                        >
                                          {expanded ? <IconChevronDown size={14} /> : <IconChevronRight size={14} />}
                                        </ActionIcon>
                                        <Text size="sm" style={{ wordBreak: 'break-all' }}>
                                          <Trans>{fileName} replaced {filesText}</Trans>
                                        </Text>
                                      </Group>
                                    </Table.Td>
                                    <Table.Td>
                                      <Text size="sm" c="var(--ink-3)">
                                        {snapshotLabel(renderLabel, sourceLabel, first.after)}
                                      </Text>
                                    </Table.Td>
                                    <Table.Td data-priority="low">
                                      <Text size="xs" c="var(--ink-3)" className="tnum" style={{ whiteSpace: 'nowrap' }}>
                                        {formatDateTime(first.createdAt)}
                                      </Text>
                                    </Table.Td>
                                    <Table.Td>
                                      {reverted ? (
                                        <Text size="xs" c="var(--ink-3)">
                                          <Trans>Reverted</Trans>
                                        </Text>
                                      ) : (
                                        <Tooltip
                                          label={revertable ? t`Revert this upgrade` : t`The trashed copy is gone`}
                                          withArrow
                                        >
                                          <ActionIcon
                                            variant="subtle"
                                            color="var(--neutral)"
                                            disabled={!revertable}
                                            loading={revertGroup.isPending}
                                            onClick={() => revertGroupRow(groupId)}
                                            aria-label={t`Revert this upgrade`}
                                          >
                                            <IconArrowBackUp size={16} />
                                          </ActionIcon>
                                        </Tooltip>
                                      )}
                                    </Table.Td>
                                  </Table.Tr>
                                  {expanded &&
                                    entry.rows.map((row) => (
                                      <Table.Tr key={row.id}>
                                        <Table.Td />
                                        <Table.Td colSpan={2}>
                                          <Text size="xs" c="var(--ink-3)" className="tnum">
                                            {upgradeHistoryChapterLabel(row)}
                                            {' · '}
                                            {snapshotLabel(renderLabel, sourceLabel, row.before)}
                                          </Text>
                                        </Table.Td>
                                        <Table.Td data-priority="low" />
                                        <Table.Td />
                                      </Table.Tr>
                                    ))}
                                </Fragment>
                              )
                            })}
                          </Table.Tbody>
                        </Table>
                      </Table.ScrollContainer>
                    </Panel>

                    {upgradeHistoryPageCount > 1 && (
                      <Group justify="center">
                        <Pagination
                          total={upgradeHistoryPageCount}
                          value={upgradeHistoryPage}
                          onChange={setUpgradeHistoryPage}
                        />
                      </Group>
                    )}
                  </>
                )}
              </Stack>

              <Stack gap="sm">
                <Title order={4}>
                  <Trans>Cutoff unmet</Trans>
                </Title>
                {!cutoffUnmet ? (
                  <TableSkeleton columns={6} rows={4} />
                ) : !upgradesSummary.profilesConfigured ? (
                  <EmptyState
                    compact
                    title={t`No quality profile assigned yet`}
                    description={
                      <Trans>
                        Nothing here has a quality profile of its own or an instance default to fall back
                        to, so nothing can be judged against a cutoff. Set one up in{' '}
                        <Anchor component={Link} to="/settings?tab=library&s=profiles">
                          Settings - Quality profiles
                        </Anchor>
                        .
                      </Trans>
                    }
                  />
                ) : cutoffUnmet.total === 0 ? (
                  <EmptyState
                    compact
                    title={t`Every file meets its cutoff`}
                    description={t`Nothing downloaded falls short of the quality profile assigned to it.`}
                  />
                ) : (
                  <>
                    <Panel p={0} className="table-panel">
                      <Table.ScrollContainer minWidth={780}>
                        <Table className="panel-table activity-upgrades-table" verticalSpacing="sm">
                          <Table.Thead>
                            <Table.Tr>
                              <Table.Th><Trans>Series</Trans></Table.Th>
                              <Table.Th><Trans>Chapter</Trans></Table.Th>
                              <Table.Th><Trans>Current file</Trans></Table.Th>
                              <Table.Th data-priority="low"><Trans>Score</Trans></Table.Th>
                              <Table.Th data-priority="low"><Trans>Profile</Trans></Table.Th>
                              <Table.Th data-priority="low"><Trans>File name</Trans></Table.Th>
                            </Table.Tr>
                          </Table.Thead>
                          <Table.Tbody>
                            {cutoffUnmet.rows.map((row) => {
                              const cutoffLabel = renderLabel(QUALITY_TIER_LABELS[row.cutoff])
                              return (
                              <Table.Tr key={row.fileId}>
                                <Table.Td>
                                  <Text
                                    component={Link}
                                    to={`/series/${row.seriesId}`}
                                    size="sm"
                                    fw={600}
                                    c="brand.4"
                                    lineClamp={1}
                                  >
                                    {row.seriesTitle}
                                  </Text>
                                </Table.Td>
                                <Table.Td>
                                  <Text size="sm" className="tnum">
                                    {cutoffRowChapterLabel(row)}
                                  </Text>
                                </Table.Td>
                                <Table.Td>
                                  <FileQualityBadge quality={row.quality} />
                                </Table.Td>
                                <Table.Td data-priority="low">
                                  <Text size="sm" c="var(--ink-3)" className="tnum">
                                    {row.quality.score ?? '-'}
                                  </Text>
                                </Table.Td>
                                <Table.Td data-priority="low">
                                  <Tooltip label={t`Cutoff: ${cutoffLabel}`} withArrow>
                                    <Text size="sm" c="var(--ink-3)" lineClamp={1}>
                                      {row.profileName}
                                    </Text>
                                  </Tooltip>
                                </Table.Td>
                                <Table.Td data-priority="low">
                                  <Text size="xs" c="var(--ink-3)" lineClamp={1} style={{ wordBreak: 'break-all' }}>
                                    {row.fileName}
                                  </Text>
                                </Table.Td>
                              </Table.Tr>
                              )
                            })}
                          </Table.Tbody>
                        </Table>
                      </Table.ScrollContainer>
                    </Panel>

                    {upgradesPageCount > 1 && (
                      <Group justify="center">
                        <Pagination total={upgradesPageCount} value={upgradesPage} onChange={setUpgradesPage} />
                      </Group>
                    )}
                  </>
                )}
              </Stack>
            </Stack>
          )}
        </Tabs.Panel>
      </Tabs>
    </SurfaceFrame>
  )
}
