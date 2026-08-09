import {
  Alert,
  Card,
  Group,
  Loader,
  Progress,
  RingProgress,
  SimpleGrid,
  Stack,
  Table,
  Text,
  Title,
} from '@mantine/core'
import {
  IconAlertTriangle,
  IconBook2,
  IconChecks,
  IconClock,
  IconFlame,
  IconInfoCircle,
} from '@tabler/icons-react'
import { useAchievements, useGamificationSummary, useLeaderboard } from '../../api/hooks'
import type { ReadingGoal } from '../../api/hooks'
import { StatTile } from '../../components/ui/StatTile'
import { formatReadingTime } from '../rewind/duration'
import { AchievementGrid } from './AchievementGrid'

const GOAL_LABELS: Record<ReadingGoal['period'], string> = {
  Day: 'Today',
  Week: 'This week',
  Month: 'This month',
  Year: 'This year',
}

const METRIC_LABELS: Record<ReadingGoal['metric'], string> = {
  Chapters: 'chapters',
  Minutes: 'minutes',
  SeriesFinished: 'series finished',
}

function GoalCard({ goal }: { goal: ReadingGoal }) {
  const done = Math.min(1, goal.progress / Math.max(1, goal.target))
  return (
    <Card withBorder radius="md" padding="md">
      <Group justify="space-between" align="flex-start" wrap="nowrap">
        <Stack gap={2}>
          <Text size="sm" fw={600}>
            {GOAL_LABELS[goal.period]}
          </Text>
          <Text size="xs" c="dimmed" className="tnum">
            {goal.progress.toLocaleString()} / {goal.target.toLocaleString()}{' '}
            {METRIC_LABELS[goal.metric]}
          </Text>
        </Stack>
        <RingProgress
          size={54}
          thickness={6}
          roundCaps
          sections={[{ value: done * 100, color: done >= 1 ? 'green' : 'var(--brand)' }]}
        />
      </Group>
      <Progress value={done * 100} size="xs" radius="xl" mt="sm" color={done >= 1 ? 'green' : undefined} />
    </Card>
  )
}

/**
 * Standing progression: level, badges, goals, leaderboard.
 *
 * The reading heatmap used to live here; it moved to Overview, which is the tab about reading. What
 * is left is the progression system itself, which is the one thing on this page that has no window.
 */
export function AchievementsPanel({ userId }: { userId?: number }) {
  const { data: summary, isLoading, isError } = useGamificationSummary(userId)
  const { data: achievements } = useAchievements(userId, summary?.enabled !== false)
  // Only meaningful for your own view: the endpoint answers about who opted in, not about whoever
  // an admin is currently looking at.
  const { data: leaderboard } = useLeaderboard(!userId)

  if (isLoading && !summary) {
    return (
      <Group justify="center" py={64}>
        <Loader />
      </Group>
    )
  }

  // Kept apart from the switched-off case on purpose. Both leave `summary.enabled` falsy, and
  // reporting a failed request as "you turned this off" sends the reader to a settings toggle that
  // is already in the position they want.
  if (isError || !summary) {
    return (
      <Alert icon={<IconAlertTriangle size={16} />} color="red" variant="light">
        Could not load your progress. The server logs will say why.
      </Alert>
    )
  }

  if (!summary.enabled) {
    return (
      <Alert icon={<IconInfoCircle size={16} />} color="gray" variant="light">
        Progress tracking is switched off. Turn it back on under Settings to see levels, achievements
        and streaks. Nothing was lost while it was off: all of it is worked out from your reading
        history whenever it is asked for.
      </Alert>
    )
  }

  const { level } = summary

  return (
    <Stack gap="lg">
      <Card withBorder radius="md" padding="md">
        <Group justify="space-between" wrap="wrap" gap="lg">
          <Group gap="md" wrap="nowrap">
            <RingProgress
              size={92}
              thickness={9}
              roundCaps
              sections={[{ value: level.progress * 100, color: 'var(--brand)' }]}
              label={
                <Text ta="center" fw={700} size="lg" className="tnum">
                  {level.level}
                </Text>
              }
            />
            <Stack gap={2}>
              <Title order={4}>Level {level.level}</Title>
              <Text size="sm" c="dimmed" className="tnum">
                {level.intoLevel.toLocaleString()} / {level.levelSpan.toLocaleString()} XP to level{' '}
                {level.level + 1}
              </Text>
              <Text size="xs" c="dimmed" className="tnum">
                {summary.earned} of {summary.total} achievements earned
              </Text>
            </Stack>
          </Group>

          {summary.showStreaks && (
            <Group gap="lg">
              <Stack gap={0} align="center">
                <Text size="xl" fw={700} className="tnum">
                  {summary.currentStreak}
                </Text>
                <Text size="xs" c="dimmed">
                  day streak
                </Text>
              </Stack>
              <Stack gap={0} align="center">
                <Text size="xl" fw={700} className="tnum">
                  {summary.longestStreak}
                </Text>
                <Text size="xs" c="dimmed">
                  best streak
                </Text>
              </Stack>
            </Group>
          )}
        </Group>
      </Card>

      <SimpleGrid cols={{ base: 2, sm: 4 }} spacing="sm">
        <StatTile label="Chapters read" value={summary.chaptersRead} icon={IconBook2} />
        <StatTile
          label="Time reading"
          value={formatReadingTime(summary.readingSeconds)}
          icon={IconClock}
        />
        <StatTile label="Series finished" value={summary.seriesFinished} icon={IconChecks} />
        <StatTile label="Days read" value={summary.daysRead} icon={IconFlame} />
      </SimpleGrid>

      {summary.goals.length > 0 && (
        <Stack gap="xs">
          <Title order={4}>Goals</Title>
          <SimpleGrid cols={{ base: 1, sm: 2, lg: 4 }} spacing="sm">
            {summary.goals.map((goal) => (
              <GoalCard key={goal.id} goal={goal} />
            ))}
          </SimpleGrid>
        </Stack>
      )}

      {achievements && achievements.length > 0 && <AchievementGrid achievements={achievements} />}

      {leaderboard && leaderboard.length > 0 && (
        <Card withBorder radius="md" padding="md">
          <Title order={4} mb="xs">
            Around the house
          </Title>
          <Table highlightOnHover>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Reader</Table.Th>
                <Table.Th ta="right">Level</Table.Th>
                <Table.Th ta="right">Chapters</Table.Th>
                <Table.Th ta="right">Streak</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {leaderboard.map((row) => (
                <Table.Tr key={row.userId}>
                  <Table.Td>{row.name}</Table.Td>
                  <Table.Td ta="right" className="tnum">
                    {row.level}
                  </Table.Td>
                  <Table.Td ta="right" className="tnum">
                    {row.chaptersRead.toLocaleString()}
                  </Table.Td>
                  <Table.Td ta="right" className="tnum">
                    {row.currentStreak}
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Card>
      )}
    </Stack>
  )
}
