import { useMemo, useState } from 'react'
import {
  ActionIcon,
  Badge,
  Group,
  Pagination,
  Progress,
  SimpleGrid,
  Stack,
  Table,
  Text,
  Title,
  Tooltip,
} from '@mantine/core'
import { IconClock, IconHistory, IconInbox, IconLoader2, IconRefresh, IconX } from '@tabler/icons-react'
import { Link } from 'react-router-dom'
import { useQueue, useQueueHistory, useRemoveQueueItem, useRetryQueueItem } from '../api/hooks'
import { EmptyState } from '../components/ui/EmptyState'
import { PageHeader } from '../components/ui/PageHeader'
import { StatTile } from '../components/ui/StatTile'
import { isQueueActive, queueStatusVisual } from '../components/ui/status'

const HISTORY_PAGE_SIZE = 25

export default function ActivityPage() {
  const { data: queue } = useQueue()
  const retry = useRetryQueueItem()
  const remove = useRemoveQueueItem()

  const [historyPage, setHistoryPage] = useState(1)
  const { data: history } = useQueueHistory(historyPage, HISTORY_PAGE_SIZE)
  const historyPageCount = history ? Math.ceil(history.total / HISTORY_PAGE_SIZE) : 0

  const queueItems = useMemo(() => queue?.items ?? [], [queue])
  const truncated = queue ? queue.total > queueItems.length : false

  const stats = useMemo(
    () => ({
      active: queueItems.filter((q) => isQueueActive(q.status)).length,
      queued: queueItems.filter((q) => q.status === 'Queued').length,
      failed: queueItems.filter((q) => q.status === 'Failed').length,
    }),
    [queueItems],
  )

  return (
    <>
      <PageHeader
        title="Activity"
        description="Live download queue — pages are fetched, validated and packaged into CBZ files two at a time."
      />

      <SimpleGrid cols={{ base: 3 }} spacing="sm" mb="lg" maw={560}>
        <StatTile label="In progress" value={stats.active} icon={IconLoader2} accent="info" />
        <StatTile label="Queued" value={stats.queued} icon={IconClock} accent="gray" />
        <StatTile label="Failed" value={stats.failed} icon={IconX} accent="danger" />
      </SimpleGrid>

      {queueItems.length === 0 ? (
        <EmptyState
          icon={IconInbox}
          title="Nothing in the queue"
          description="Queued and downloading chapters show up here. Trigger a search from a series page or the library."
        />
      ) : (
        <Table.ScrollContainer minWidth={720}>
          <Table verticalSpacing="sm">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Series</Table.Th>
                <Table.Th>Chapter</Table.Th>
                <Table.Th>Source</Table.Th>
                <Table.Th w={240}>Progress</Table.Th>
                <Table.Th w={150}>Status</Table.Th>
                <Table.Th w={80} />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {queueItems.map((q) => {
                const visual = queueStatusVisual(q.status)
                const retryInfo =
                  q.status === 'Failed' && q.retryCount > 0
                    ? `Retried ${q.retryCount}x${
                        q.nextAttempt
                          ? ` — next attempt ${new Date(q.nextAttempt).toLocaleTimeString([], {
                              hour: '2-digit',
                              minute: '2-digit',
                            })}`
                          : ''
                      }`
                    : null
                const tooltipLabel = [q.errorMessage, retryInfo].filter(Boolean).join(' — ') || visual.label
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
                        {q.chapterLabel}
                      </Text>
                    </Table.Td>
                    <Table.Td>
                      <Text size="sm" c="dimmed">
                        {q.sourceName}
                      </Text>
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
                          <Text size="xs" c="dimmed" w={52} className="tnum" ta="right">
                            {q.pagesDone}/{q.pagesTotal}
                          </Text>
                        </Group>
                      ) : (
                        <Text size="xs" c="dimmed">
                          —
                        </Text>
                      )}
                    </Table.Td>
                    <Table.Td>
                      <Tooltip label={tooltipLabel} withArrow disabled={!q.errorMessage && !retryInfo}>
                        <Badge
                          size="sm"
                          color={visual.color}
                          variant="light"
                          leftSection={<visual.Icon size={12} />}
                        >
                          {visual.label}
                        </Badge>
                      </Tooltip>
                    </Table.Td>
                    <Table.Td>
                      <Group gap={4} wrap="nowrap" justify="flex-end">
                        {q.status === 'Failed' && (
                          <Tooltip label="Retry" withArrow>
                            <ActionIcon
                              variant="subtle"
                              color="gray"
                              onClick={() => retry.mutate(q.id)}
                              aria-label="Retry download"
                            >
                              <IconRefresh size={16} />
                            </ActionIcon>
                          </Tooltip>
                        )}
                        <Tooltip label="Remove" withArrow>
                          <ActionIcon
                            variant="subtle"
                            color="red"
                            onClick={() => remove.mutate(q.id)}
                            aria-label="Remove from queue"
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
        <Text size="xs" c="dimmed" mt="xs">
          Showing {queueItems.length} of {queue?.total} queued items. The rest are still queued and
          will download — they're just not listed here.
        </Text>
      )}

      <Stack gap="sm" mt="xl">
        <Group gap="xs">
          <IconHistory size={18} />
          <Title order={4}>History</Title>
        </Group>

        {!history || history.items.length === 0 ? (
          <EmptyState
            icon={IconHistory}
            title="No history yet"
            description="Completed and cancelled downloads show up here."
          />
        ) : (
          <>
            <Table.ScrollContainer minWidth={640}>
              <Table verticalSpacing="sm">
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>Series</Table.Th>
                    <Table.Th>Chapter</Table.Th>
                    <Table.Th>Source</Table.Th>
                    <Table.Th w={150}>Status</Table.Th>
                    <Table.Th w={160}>Completed</Table.Th>
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
                            {q.chapterLabel}
                          </Text>
                        </Table.Td>
                        <Table.Td>
                          <Text size="sm" c="dimmed">
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
                            {visual.label}
                          </Badge>
                        </Table.Td>
                        <Table.Td>
                          <Text size="xs" c="dimmed" className="tnum">
                            {q.completedAt ? new Date(q.completedAt).toLocaleString() : '—'}
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
    </>
  )
}
