import { useMemo, useState } from 'react'
import {
  ActionIcon,
  Badge,
  Group,
  Progress,
  SimpleGrid,
  Switch,
  Table,
  Text,
  Tooltip,
} from '@mantine/core'
import { IconClock, IconInbox, IconLoader2, IconRefresh, IconX } from '@tabler/icons-react'
import { Link } from 'react-router-dom'
import { useQueue, useRemoveQueueItem, useRetryQueueItem } from '../api/hooks'
import { EmptyState } from '../components/ui/EmptyState'
import { PageHeader } from '../components/ui/PageHeader'
import { StatTile } from '../components/ui/StatTile'
import { isQueueActive, queueStatusVisual } from '../components/ui/status'

export default function ActivityPage() {
  const [showHistory, setShowHistory] = useState(false)
  const { data: queue } = useQueue(showHistory)
  const retry = useRetryQueueItem()
  const remove = useRemoveQueueItem()

  const stats = useMemo(() => {
    const list = queue ?? []
    return {
      active: list.filter((q) => isQueueActive(q.status)).length,
      queued: list.filter((q) => q.status === 'Queued').length,
      failed: list.filter((q) => q.status === 'Failed').length,
    }
  }, [queue])

  return (
    <>
      <PageHeader
        title="Activity"
        description="Live download queue — pages are fetched, validated and packaged into CBZ files two at a time."
        actions={
          <Switch
            label="Show history"
            checked={showHistory}
            onChange={(e) => setShowHistory(e.currentTarget.checked)}
          />
        }
      />

      <SimpleGrid cols={{ base: 3 }} spacing="sm" mb="lg" maw={560}>
        <StatTile label="In progress" value={stats.active} icon={IconLoader2} accent="info" />
        <StatTile label="Queued" value={stats.queued} icon={IconClock} accent="gray" />
        <StatTile label="Failed" value={stats.failed} icon={IconX} accent="danger" />
      </SimpleGrid>

      {!queue || queue.length === 0 ? (
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
              {queue.map((q) => {
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
                      <Tooltip label={q.errorMessage ?? visual.label} withArrow disabled={!q.errorMessage}>
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
    </>
  )
}
