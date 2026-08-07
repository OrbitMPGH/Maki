import { Badge, Card, Group, Progress, RingProgress, SimpleGrid, Stack, Text } from '@mantine/core'
import { Link } from 'react-router-dom'
import type { GamificationSummary } from '../../api/hooks'

/**
 * Home's progression row: level, streak, active goals and the last few badges. Compact by design —
 * the full picture lives on the Stats page, and this is the glance that tells you whether it is
 * worth going there.
 */
export function ProgressCard({ summary }: { summary: GamificationSummary }) {
  const { level } = summary
  const recent = summary.recent.slice(0, 3)

  return (
    <Card withBorder radius="md" padding="md" component={Link} to="/stats" style={{ display: 'block' }}>
      <Group justify="space-between" wrap="wrap" gap="lg" align="flex-start">
        <Group gap="md" wrap="nowrap">
          <RingProgress
            size={64}
            thickness={7}
            roundCaps
            sections={[{ value: level.progress * 100, color: 'var(--brand)' }]}
            label={
              <Text ta="center" fw={700} size="sm" className="tnum">
                {level.level}
              </Text>
            }
          />
          <Stack gap={2}>
            <Text fw={600} size="sm">
              Level {level.level}
            </Text>
            <Text size="xs" c="dimmed" className="tnum">
              {summary.chaptersRead.toLocaleString()} chapters read
            </Text>
            {summary.showStreaks && summary.currentStreak > 0 && (
              <Text size="xs" c="dimmed" className="tnum">
                {summary.currentStreak} day streak
              </Text>
            )}
          </Stack>
        </Group>

        {recent.length > 0 && (
          <Group gap="xs">
            {recent.map((a) => (
              <Badge key={`${a.key}-${a.tier}`} variant="light" size="sm">
                {a.tierName ? `${a.name} · ${a.tierName}` : a.name}
              </Badge>
            ))}
          </Group>
        )}
      </Group>

      {summary.goals.length > 0 && (
        <SimpleGrid cols={{ base: 1, sm: 2, lg: 4 }} spacing="sm" mt="md">
          {summary.goals.map((goal) => {
            const done = Math.min(1, goal.progress / Math.max(1, goal.target))
            return (
              <Stack key={goal.id} gap={4}>
                <Group justify="space-between" gap="xs">
                  <Text size="xs" c="dimmed">
                    {goal.period === 'Day'
                      ? 'Today'
                      : goal.period === 'Week'
                        ? 'This week'
                        : goal.period === 'Month'
                          ? 'This month'
                          : 'This year'}
                  </Text>
                  <Text size="xs" c="dimmed" className="tnum">
                    {goal.progress.toLocaleString()} / {goal.target.toLocaleString()}
                  </Text>
                </Group>
                <Progress
                  value={done * 100}
                  size="sm"
                  radius="xl"
                  color={done >= 1 ? 'green' : undefined}
                />
              </Stack>
            )
          })}
        </SimpleGrid>
      )}
    </Card>
  )
}
