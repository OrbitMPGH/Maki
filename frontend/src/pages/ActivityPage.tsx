import { useMemo, useState } from 'react'
import {
  ActionIcon,
  Badge,
  Button,
  Group,
  Loader,
  Modal,
  Pagination,
  Progress,
  SimpleGrid,
  Stack,
  Table,
  Text,
  Title,
  Tooltip,
} from '@mantine/core'
import {
  IconArrowBarToUp,
  IconArrowDown,
  IconArrowUp,
  IconAlertTriangle,
  IconClock,
  IconHistory,
  IconInbox,
  IconLoader2,
  IconRefresh,
  IconTrash,
  IconX,
} from '@tabler/icons-react'
import { Link } from 'react-router-dom'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import {
  useClearQueue,
  useQueue,
  useQueueHistory,
  useRemoveQueueItem,
  useReorderQueue,
  useRetryQueueItem,
} from '../api/hooks'
import { useAuth } from '../auth/AuthProvider'
import { ImportReviewModal } from '../components/ImportReviewModal'
import { EmptyState } from '../components/ui/EmptyState'
import { PageHeader } from '../components/ui/PageHeader'
import { StatTile } from '../components/ui/StatTile'
import { isQueueActive, needsImportReview, queueStatusVisual } from '../components/ui/status'
import { queueErrorMessage, queueItemLabel } from '../api/queue'
import { useLabel } from '../i18n-context'
import { formatDateTime, formatTime } from '../format'
import { SurfaceFrame } from '../components/ui/SurfaceFrame'

const HISTORY_PAGE_SIZE = 25

export default function ActivityPage() {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const { data: queue } = useQueue()
  const retry = useRetryQueueItem()
  const remove = useRemoveQueueItem()
  const reorder = useReorderQueue()
  const clear = useClearQueue()
  const { can } = useAuth()
  const canManageQueue = can('ManageDownloadQueue')

  const [historyPage, setHistoryPage] = useState(1)
  const [clearConfirmOpen, setClearConfirmOpen] = useState(false)
  const [reviewing, setReviewing] = useState<number | null>(null)
  const { data: history } = useQueueHistory(historyPage, HISTORY_PAGE_SIZE)
  const historyPageCount = history ? Math.ceil(history.total / HISTORY_PAGE_SIZE) : 0

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

      <SimpleGrid cols={{ base: 2, sm: 4 }} spacing="sm" mb="lg">
        <StatTile label={t`In progress`} value={stats.active} icon={IconLoader2} accent="info" />
        <StatTile label={t`Queued`} value={stats.queued} icon={IconClock} accent="gray" />
        <StatTile label={t`Needs review`} value={stats.review} icon={IconAlertTriangle} accent="warn" />
        <StatTile label={t`Failed`} value={stats.failed} icon={IconX} accent="danger" />
      </SimpleGrid>

      {queueItems.length === 0 ? (
        <EmptyState
          icon={IconInbox}
          title={t`Nothing in the queue`}
          description={t`Queued and downloading chapters show up here. Trigger a search from a series page or the library.`}
        />
      ) : (
        <Table.ScrollContainer minWidth={720}>
          <Table className="panel-table" verticalSpacing="sm">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>
                  <Trans>Series</Trans>
                </Table.Th>
                <Table.Th>
                  <Trans>Chapter</Trans>
                </Table.Th>
                <Table.Th>
                  <Trans>Source</Trans>
                </Table.Th>
                <Table.Th w={240}>
                  <Trans>Progress</Trans>
                </Table.Th>
                <Table.Th w={150}>
                  <Trans>Status</Trans>
                </Table.Th>
                <Table.Th w={190} />
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
                    <Table.Td>
                      {q.status === 'Resolving' ? (
                        <Group gap={6} wrap="nowrap">
                          <Loader size="xs" />
                          <Text size="sm" c="var(--ink-3)">
                            <Trans>Finding source</Trans>
                          </Text>
                        </Group>
                      ) : (
                        <Text size="sm" c="var(--ink-3)">
                          {q.sourceName}
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
                            color={q.status === 'Failed' ? 'red' : 'brand'}
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
                        <Badge
                          size="sm"
                          color={visual.color}
                          variant="light"
                          leftSection={<visual.Icon size={12} />}
                        >
                          {renderLabel(visual.label)}
                        </Badge>
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
                                disabled={index === 0}
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
                                disabled={index === 0}
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
                                disabled={index === queueItems.length - 1}
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
              color="var(--danger)"
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

        {!history || history.items.length === 0 ? (
          <EmptyState
            icon={IconHistory}
            title={t`No history yet`}
            description={t`Completed and cancelled downloads show up here.`}
          />
        ) : (
          <>
            <Table.ScrollContainer minWidth={640}>
              <Table className="panel-table" verticalSpacing="sm">
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>
                      <Trans>Series</Trans>
                    </Table.Th>
                    <Table.Th>
                      <Trans>Chapter</Trans>
                    </Table.Th>
                    <Table.Th>
                      <Trans>Source</Trans>
                    </Table.Th>
                    <Table.Th w={150}>
                      <Trans>Status</Trans>
                    </Table.Th>
                    <Table.Th w={160}>
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
                        <Table.Td>
                          <Text size="sm" c="var(--ink-3)">
                            {q.sourceName}
                          </Text>
                        </Table.Td>
                        <Table.Td>
                          <Badge
                            size="sm"
                            color={visual.color}
                            variant="light"
                            leftSection={<visual.Icon size={12} />}
                          >
                            {renderLabel(visual.label)}
                          </Badge>
                        </Table.Td>
                        <Table.Td>
                          <Text size="xs" c="var(--ink-3)" className="tnum">
                            {q.completedAt ? formatDateTime(q.completedAt) : '-'}
                          </Text>
                        </Table.Td>
                      </Table.Tr>
                    )
                  })}
                </Table.Tbody>
              </Table>
            </Table.ScrollContainer>

            {historyPageCount > 1 && (
              <Group justify="center">
                <Pagination total={historyPageCount} value={historyPage} onChange={setHistoryPage} />
              </Group>
            )}
          </>
        )}
      </Stack>
    </SurfaceFrame>
  )
}
