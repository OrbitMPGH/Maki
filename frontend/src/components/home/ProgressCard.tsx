import { Badge, Card, Group, Progress, RingProgress, SimpleGrid, Stack, Text } from '@mantine/core'
import { IconFlame, IconTrophy } from '@tabler/icons-react'
import { Link } from 'react-router-dom'
import type { ProgressSummary } from '../../api/hooks'
import { formatReadingTime } from '../../pages/stats/duration'

function Figure({
  value,
  label,
  icon: FigIcon,
}: {
  value: string | number
  label: string
  icon?: typeof IconFlame
}) {
  return (
    <Stack gap={0} align="center" miw={64}>
      <Group gap={4} wrap="nowrap">
        {FigIcon && <FigIcon size={15} style={{ color: 'var(--brand)' }} />}
        <Text fz={20} fw={700} className="tnum">
          {value}
        </Text>
      </Group>
      <Text size="xs" c="dimmed">
        {label}
      </Text>
    </Stack>
  )
}

/**
 * Home's progression card, matching the Stats overview's ProgressStrip so the same numbers read
 * the same in both places. The whole card links to Stats for the full picture.
 */
export function ProgressCard({ summary }: { summary: ProgressSummary }) {
  const { level } = summary

  return (
    <Card withBorder radius="md" padding="md" component={Link} to="/stats" style={{ display: 'block' }}>
      <Group justify="space-between" wrap="wrap" gap="lg">
        <Group gap="md" wrap="nowrap">
          <RingProgress
            size={62}
            thickness={6}
            roundCaps
            sections={[{ value: level.progress * 100, color: 'var(--brand)' }]}
            label={
              <Text ta="center" fw={700} size="sm" className="tnum">
                {level.level}
              </Text>
            }
          />
          <Stack gap={2}>
            <Text fw={650}>Level {level.level}</Text>
            <Text size="xs" c="dimmed" className="tnum">
              {level.intoLevel.toLocaleString()} / {level.levelSpan.toLocaleString()} XP to level{' '}
              {level.level + 1}
            </Text>
          </Stack>
        </Group>

        <Group gap="xl" wrap="wrap">
          <Figure value={summary.chaptersRead.toLocaleString()} label="chapters read" />
          <Figure value={formatReadingTime(summary.readingSeconds)} label="time read" />
          {summary.showStreaks && (
            <>
              <Figure value={summary.currentStreak} label="day streak" icon={IconFlame} />
              <Figure value={summary.longestStreak} label="best streak" />
            </>
          )}
          <Figure value={`${summary.earned}/${summary.total}`} label="achievements" icon={IconTrophy} />
        </Group>
      </Group>

      {(summary.goals.length > 0 || summary.recent.length > 0) && (
        <Stack gap="md" mt="md">
          {summary.goals.length > 0 && (
            <SimpleGrid cols={{ base: 1, sm: 2, lg: 4 }} spacing="sm">
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

          {summary.recent.length > 0 && (
            <Group gap="xs">
              {summary.recent.slice(0, 3).map((a) => (
                <Badge key={`${a.key}-${a.tier}`} variant="light" size="sm">
                  {a.tierName ? `${a.name} · ${a.tierName}` : a.name}
                </Badge>
              ))}
            </Group>
          )}
        </Stack>
      )}
    </Card>
  )
}
