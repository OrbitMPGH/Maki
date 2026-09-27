import { useEffect, useMemo, useState } from 'react'
import {
  ActionIcon,
  Anchor,
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
  IconArrowBarToUp,
  IconArrowDown,
  IconArrowUp,
  IconHistory,
  IconRefresh,
  IconTrash,
  IconX,
} from '@tabler/icons-react'
import { Link, useSearchParams } from 'react-router-dom'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { t as now } from '@lingui/core/macro'
import {
  useClearQueue,
  useQueue,
  useQueueHistory,
  useRemoveQueueItem,
  useReorderQueue,
  useRetryQueueItem,
} from '../api/hooks'
import { useCutoffUnmet, useUpgradesSummary, QUALITY_TIER_LABELS } from '../api/upgrades'
import type { CutoffUnmetRowDto } from '../api/upgrades'
import { useAuth } from '../auth/AuthProvider'
import { ImportReviewModal } from '../components/ImportReviewModal'
import { FileQualityBadge } from '../components/series/FileQualityBadge'
import { EmptyState } from '../components/ui/EmptyState'
import { PageHeader } from '../components/ui/PageHeader'
import { Panel } from '../components/ui/Panel'
import { TableSkeleton } from '../components/ui/TableSkeleton'
import { FigureStrip } from '../components/ui/FigureStrip'
import { StatusDot } from '../components/ui/StatusDot'
import { isQueueActive, needsImportReview, queueStatusVisual, statusToken } from '../components/ui/status'
import { queueErrorMessage, queueItemLabel } from '../api/queue'
import { useLabel } from '../i18n-context'
import { useSourceLabel } from '../sourceLabels'
import { formatDateTime, formatTime } from '../format'
import { SurfaceFrame } from '../components/ui/SurfaceFrame'

const HISTORY_PAGE_SIZE = 25
const UPGRADES_PAGE_SIZE = 25

/** "Ch.148", "Ch.148 - Title", or the chapter's own title/"One-shot" when it has no number. */
function cutoffRowChapterLabel(row: CutoffUnmetRowDto): string {
  const { chapterNumber, chapterTitle } = row
  if (chapterNumber === null) return chapterTitle ?? now`One-shot`
  return chapterTitle ? now`Ch.${chapterNumber} - ${chapterTitle}` : now`Ch.${chapterNumber}`
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
  const { data: cutoffUnmet } = useCutoffUnmet(upgradesPage, UPGRADES_PAGE_SIZE, undefined, upgradesTabActive)
  const upgradesPageCount = cutoffUnmet ? Math.ceil(cutoffUnmet.total / UPGRADES_PAGE_SIZE) : 0

  // The list can shrink out from under the current page (a profile edit, a file getting upgraded
  // elsewhere), which would otherwise strand the view on a page past the end showing an empty table
  // with no pager to get back.
  useEffect(() => {
    if (upgradesPageCount > 0 && upgradesPage > upgradesPageCount) setUpgradesPage(upgradesPageCount)
  }, [upgradesPageCount, upgradesPage])

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
          <Tabs.Tab value="upgrades"><Trans>Upgrades</Trans></Tabs.Tab>
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
                          <Text size="sm" c="var(--ink-3)">
                            {sourceLabel(q.sourceName)}
                          </Text>
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
                        <Tooltip label={tooltipLabel} withArrow disabled={!failure && !retryInfo}>
                          <StatusDot tone={statusToken(visual.color)} live={isQueueActive(q.status)}>
                            {renderLabel(visual.label)}
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
          <TableSkeleton columns={5} />
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
                    </Table.Tr>
                  </Table.Thead>
                  <Table.Tbody>
                    {history.items.map((q) => {
                      const visual = queueStatusVisual(q.status)
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
                            <Text size="sm" c="var(--ink-3)">
                              {sourceLabel(q.sourceName)}
                            </Text>
                          </Table.Td>
                          <Table.Td>
                            <StatusDot tone={statusToken(visual.color)}>{renderLabel(visual.label)}</StatusDot>
                          </Table.Td>
                          <Table.Td data-priority="low">
                            <Text size="xs" c="var(--ink-3)" className="tnum" style={{ whiteSpace: 'nowrap' }}>
                              {q.completedAt ? formatDateTime(q.completedAt) : '-'}
                            </Text>
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
          {!upgradesSummary || !cutoffUnmet ? (
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
                <Group justify="center" mt="sm">
                  <Pagination total={upgradesPageCount} value={upgradesPage} onChange={setUpgradesPage} />
                </Group>
              )}
            </>
          )}
        </Tabs.Panel>
      </Tabs>
    </SurfaceFrame>
  )
}
