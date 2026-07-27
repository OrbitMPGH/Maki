import { Link } from 'react-router-dom'
import { Badge, Button, Group, Paper, Progress, Stack, Text } from '@mantine/core'
import { IconChevronRight } from '@tabler/icons-react'
import { queueStatusVisual } from '../ui/status'
import type { QueueItemDto } from '../../api/types'

const MAX_ROWS = 5

/**
 * Compact view of what's downloading right now, with a link to the full Activity page. Home shows
 * this only while something is in flight — the caller drops the whole section when the list is
 * empty, so an idle library doesn't carry a permanently blank panel.
 */
export function DownloadingStrip({ items }: { items: QueueItemDto[] }) {
  const shown = items.slice(0, MAX_ROWS)

  return (
    <Paper withBorder radius="lg" p="md">
      <Stack gap="sm">
        {shown.map((q) => {
          const visual = queueStatusVisual(q.status)
          return (
            <Group key={q.id} gap="sm" wrap="nowrap">
              <Text
                component={Link}
                to={`/series/${q.seriesId}`}
                size="sm"
                fw={600}
                c="brand.4"
                lineClamp={1}
                style={{ flex: '1 1 0', minWidth: 0 }}
              >
                {q.seriesTitle}
              </Text>
              <Text size="sm" c="dimmed" className="tnum" style={{ whiteSpace: 'nowrap' }}>
                {q.chapterLabel}
              </Text>
              {q.pagesTotal > 0 && (
                <Progress
                  value={(q.pagesDone / q.pagesTotal) * 100}
                  radius="xl"
                  size="sm"
                  animated={q.status === 'Downloading'}
                  w={120}
                  visibleFrom="sm"
                />
              )}
              <Badge
                variant="light"
                color={visual.color}
                size="sm"
                leftSection={<visual.Icon size={11} />}
              >
                {visual.label}
              </Badge>
            </Group>
          )
        })}

        <Group justify="space-between" wrap="nowrap">
          <Text size="xs" c="dimmed">
            {items.length > MAX_ROWS
              ? `${items.length - MAX_ROWS} more in the queue`
              : `${items.length} in the queue`}
          </Text>
          <Button
            component={Link}
            to="/activity"
            variant="subtle"
            size="compact-sm"
            rightSection={<IconChevronRight size={14} />}
          >
            View activity
          </Button>
        </Group>
      </Stack>
    </Paper>
  )
}
