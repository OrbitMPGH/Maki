import {
  ActionIcon,
  Badge,
  Group,
  Progress,
  Table,
  Text,
  Title,
  Tooltip,
} from '@mantine/core'
import { Link } from 'react-router-dom'
import { useQueue, useRemoveQueueItem, useRetryQueueItem } from '../api/hooks'

const statusColor: Record<string, string> = {
  Queued: 'gray',
  FetchingPages: 'blue',
  Downloading: 'blue',
  Validating: 'cyan',
  Packaging: 'cyan',
  Importing: 'teal',
  Completed: 'green',
  Failed: 'red',
  Cancelled: 'gray',
}

export default function ActivityPage() {
  const { data: queue } = useQueue()
  const retry = useRetryQueueItem()
  const remove = useRemoveQueueItem()

  return (
    <>
      <Title order={2} mb="md">
        Activity
      </Title>
      {!queue || queue.length === 0 ? (
        <Text c="dimmed">Queue is empty.</Text>
      ) : (
        <Table striped>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Series</Table.Th>
              <Table.Th>Chapter</Table.Th>
              <Table.Th>Source</Table.Th>
              <Table.Th w={220}>Progress</Table.Th>
              <Table.Th>Status</Table.Th>
              <Table.Th w={90}></Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {queue.map((q) => (
              <Table.Tr key={q.id}>
                <Table.Td>
                  <Text component={Link} to={`/series/${q.seriesId}`} size="sm" fw={600}>
                    {q.seriesTitle}
                  </Text>
                </Table.Td>
                <Table.Td>{q.chapterLabel}</Table.Td>
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
                        animated={q.status === 'Downloading'}
                      />
                      <Text size="xs" c="dimmed" w={48}>
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
                  <Tooltip label={q.errorMessage ?? q.status} withArrow>
                    <Badge size="sm" color={statusColor[q.status] ?? 'gray'} variant="light">
                      {q.status}
                    </Badge>
                  </Tooltip>
                </Table.Td>
                <Table.Td>
                  <Group gap={4} wrap="nowrap">
                    {q.status === 'Failed' && (
                      <Tooltip label="Retry" withArrow>
                        <ActionIcon
                          variant="subtle"
                          onClick={() => retry.mutate(q.id)}
                          aria-label="Retry download"
                        >
                          ↻
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
                        ✕
                      </ActionIcon>
                    </Tooltip>
                  </Group>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}
    </>
  )
}
