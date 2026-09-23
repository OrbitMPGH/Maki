import {
  Alert,
  Card,
  Group,
  Progress,
  RingProgress,
  SimpleGrid,
  Skeleton,
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
import { Trans, Plural, useLingui } from '@lingui/react/macro'
import { msg, plural } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { useAchievements, useProgressSummary, useLeaderboard } from '../../api/hooks'
import type { ReadingGoal } from '../../api/hooks'
import { Panel } from '../../components/ui/Panel'
import { StatTile } from '../../components/ui/StatTile'
import { formatNumber, formatReadingTime } from '../../format'
import { useLabel } from '../../i18n-context'
import { AchievementGrid } from './AchievementGrid'

const GOAL_LABELS: Record<ReadingGoal['period'], MessageDescriptor> = {
  Day: msg`Today`,
  Week: msg`This week`,
  Month: msg`This month`,
  Year: msg`This year`,
}

/** Target count, pluralised by metric: "12 / 50 chapters", "1 / 30 minutes". */
function metricProgress(metric: ReadingGoal['metric'], target: number): string {
  switch (metric) {
    case 'Chapters':
      return plural(target, { one: '# chapter', other: '# chapters' })
    case 'Minutes':
      return plural(target, { one: '# minute', other: '# minutes' })
    case 'SeriesFinished':
      return plural(target, { one: '# series finished', other: '# series finished' })
  }
}

function GoalCard({ goal }: { goal: ReadingGoal }) {
  const renderLabel = useLabel()
  const done = Math.min(1, goal.progress / Math.max(1, goal.target))
  return (
    <Card withBorder radius="md" padding="md">
      <Group justify="space-between" align="flex-start" wrap="nowrap">
        <Stack gap={2}>
          <Text size="sm" fw={600}>
            {renderLabel(GOAL_LABELS[goal.period])}
          </Text>
          <Text size="xs" c="var(--ink-3)" className="tnum">
            {formatNumber(goal.progress)} / {metricProgress(goal.metric, goal.target)}
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
  const { t } = useLingui()
  const { data: summary, isLoading, isError } = useProgressSummary(userId)
  const { data: achievements } = useAchievements(userId, summary?.enabled !== false)
  // Only meaningful for your own view: the endpoint answers about who opted in, not about whoever
  // an admin is currently looking at.
  const { data: leaderboard } = useLeaderboard(!userId)

  if (isLoading && !summary) {
    return (
      <Stack gap="lg" aria-hidden>
        <Panel p="md">
          <Group gap="md" wrap="nowrap">
            <Skeleton h={92} w={92} circle />
            <Stack gap={8}>
              <Skeleton h={16} w={90} />
              <Skeleton h={10} w={180} />
              <Skeleton h={8} w={140} />
            </Stack>
          </Group>
        </Panel>
        <SimpleGrid cols={{ base: 2, sm: 4 }} spacing="sm">
          <StatTile label={t`Chapters read`} value="" icon={IconBook2} loading />
          <StatTile label={t`Time reading`} value="" icon={IconClock} loading />
          <StatTile label={t`Series finished`} value="" icon={IconChecks} loading />
          <StatTile label={t`Days read`} value="" icon={IconFlame} loading />
        </SimpleGrid>
        <Stack gap="sm">
          <Skeleton h={12} w={64} />
          <SimpleGrid cols={{ base: 1, sm: 2, lg: 4 }} spacing="sm">
            {Array.from({ length: 8 }, (_, i) => (
              <Skeleton key={i} h={104} radius="lg" />
            ))}
          </SimpleGrid>
        </Stack>
      </Stack>
    )
  }

  // Kept apart from the switched-off case on purpose. Both leave `summary.enabled` falsy, and
  // reporting a failed request as "you turned this off" sends the reader to a settings toggle that
  // is already in the position they want.
  if (isError || !summary) {
    return (
      <Alert icon={<IconAlertTriangle size={16} />} color="var(--danger)" variant="light">
        <Trans>Could not load your progress. The server logs will say why.</Trans>
      </Alert>
    )
  }

  if (!summary.enabled) {
    return (
      <Alert icon={<IconInfoCircle size={16} />} color="var(--neutral)" variant="light">
        <Trans>
          Progress tracking is switched off. Turn it back on under Settings to see levels,
          achievements and streaks. Nothing was lost while it was off: all of it is worked out from
          your reading history whenever it is asked for.
        </Trans>
      </Alert>
    )
  }

  const { level } = summary
  const earnedCount = summary.earned
  const currentLevel = level.level
  const nextLevel = level.level + 1
  const intoLevel = formatNumber(level.intoLevel)
  const levelSpan = formatNumber(level.levelSpan)

  return (
    <Stack gap="lg">
      <Panel p="md">
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
              <Title order={4}>
                <Trans>Level {currentLevel}</Trans>
              </Title>
              <Text size="sm" c="var(--ink-3)" className="tnum">
                <Trans>
                  {intoLevel} / {levelSpan} XP to level {nextLevel}
                </Trans>
              </Text>
              <Text size="xs" c="var(--ink-3)" className="tnum">
                <Trans>
                  {earnedCount} of{' '}
                  <Plural value={summary.total} one="# achievement earned" other="# achievements earned" />
                </Trans>
              </Text>
            </Stack>
          </Group>

          {summary.showStreaks && (
            <Group gap="lg">
              <Stack gap={0} align="center">
                <Text size="xl" fw={700} className="tnum">
                  {summary.currentStreak}
                </Text>
                <Text size="xs" c="var(--ink-3)">
                  <Trans>day streak</Trans>
                </Text>
              </Stack>
              <Stack gap={0} align="center">
                <Text size="xl" fw={700} className="tnum">
                  {summary.longestStreak}
                </Text>
                <Text size="xs" c="var(--ink-3)">
                  <Trans>best streak</Trans>
                </Text>
              </Stack>
            </Group>
          )}
        </Group>
      </Panel>

      <SimpleGrid cols={{ base: 2, sm: 4 }} spacing="sm">
        <StatTile label={t`Chapters read`} value={summary.chaptersRead} icon={IconBook2} />
        <StatTile
          label={t`Time reading`}
          value={formatReadingTime(summary.readingSeconds)}
          icon={IconClock}
        />
        <StatTile label={t`Series finished`} value={summary.seriesFinished} icon={IconChecks} />
        <StatTile label={t`Days read`} value={summary.daysRead} icon={IconFlame} />
      </SimpleGrid>

      {summary.goals.length > 0 && (
        <Stack gap="xs">
          <Title order={4}>
            <Trans>Goals</Trans>
          </Title>
          <SimpleGrid cols={{ base: 1, sm: 2, lg: 4 }} spacing="sm">
            {summary.goals.map((goal) => (
              <GoalCard key={goal.id} goal={goal} />
            ))}
          </SimpleGrid>
        </Stack>
      )}

      {achievements && achievements.length > 0 && <AchievementGrid achievements={achievements} />}

      {leaderboard && leaderboard.length > 0 && (
        <Panel p="md">
          <Title order={4} mb="xs">
            <Trans>Around the house</Trans>
          </Title>
          <Table className="panel-table ops-table" highlightOnHover>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>
                  <Trans>Reader</Trans>
                </Table.Th>
                <Table.Th ta="right">
                  <Trans>Level</Trans>
                </Table.Th>
                <Table.Th ta="right">
                  <Trans>Chapters</Trans>
                </Table.Th>
                <Table.Th ta="right">
                  <Trans>Streak</Trans>
                </Table.Th>
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
                    {formatNumber(row.chaptersRead)}
                  </Table.Td>
                  <Table.Td ta="right" className="tnum">
                    {row.currentStreak}
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Panel>
      )}
    </Stack>
  )
}
