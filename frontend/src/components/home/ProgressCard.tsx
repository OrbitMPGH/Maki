import {
  Badge,
  Card,
  Divider,
  Group,
  Progress,
  RingProgress,
  SimpleGrid,
  Stack,
  Text,
} from '@mantine/core'
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
    <div className="hero-stat">
      <span className="hero-stat-n tnum">
        {FigIcon && (
          <FigIcon size={16} style={{ color: 'var(--brand)', marginRight: 5, verticalAlign: -2 }} />
        )}
        {value}
      </span>
      <span className="hero-stat-l">{label}</span>
    </div>
  )
}

/**
 * Home's progression card, matching the Stats overview's ProgressStrip so the same numbers read
 * the same in both places. The whole card links to Stats for the full picture.
 */
export function ProgressCard({ summary }: { summary: ProgressSummary }) {
  const { level } = summary

  return (
    <Card withBorder radius="lg" padding="lg" component={Link} to="/stats" style={{ display: 'block' }}>
      <Group justify="space-between" wrap="wrap" gap="lg">
        <Group gap="md" wrap="nowrap">
          <RingProgress
            size={62}
            thickness={6}
            roundCaps
            sections={[{ value: level.progress * 100, color: 'var(--brand)' }]}
            label={
              <Text ta="center" fw={700} size="sm" className="tnum" c="var(--ink-hi)">
                {level.level}
              </Text>
            }
          />
          <Stack gap={2}>
            <Text fw={700} c="var(--ink-hi)">
              Level {level.level}
            </Text>
            <Text size="xs" c="var(--ink-4)" className="tnum">
              {level.intoLevel.toLocaleString()} / {level.levelSpan.toLocaleString()} XP to level{' '}
              {level.level + 1}
            </Text>
          </Stack>
        </Group>

        <div className="hero-stats">
          <Figure value={summary.chaptersRead.toLocaleString()} label="chapters read" />
          <Figure value={formatReadingTime(summary.readingSeconds)} label="time read" />
          {summary.showStreaks && (
            <>
              <Figure value={summary.currentStreak} label="day streak" icon={IconFlame} />
              <Figure value={summary.longestStreak} label="best streak" />
            </>
          )}
          <Figure value={`${summary.earned}/${summary.total}`} label="achievements" icon={IconTrophy} />
        </div>
      </Group>

      {(summary.goals.length > 0 || summary.recent.length > 0) && (
        <Stack gap="md" mt="md">
          <Divider color="var(--hairline)" />
          {summary.goals.length > 0 && (
            <SimpleGrid cols={{ base: 1, sm: 2, lg: 4 }} spacing="sm">
              {summary.goals.map((goal) => {
                const done = Math.min(1, goal.progress / Math.max(1, goal.target))
                return (
                  <Stack key={goal.id} gap={4}>
                    <Group justify="space-between" gap="xs">
                      <Text size="xs" c="var(--ink-4)">
                        {goal.period === 'Day'
                          ? 'Today'
                          : goal.period === 'Week'
                            ? 'This week'
                            : goal.period === 'Month'
                              ? 'This month'
                              : 'This year'}
                      </Text>
                      <Text size="xs" c="var(--ink-3)" fw={600} className="tnum">
                        {goal.progress.toLocaleString()} / {goal.target.toLocaleString()}
                      </Text>
                    </Group>
                    <Progress
                      value={done * 100}
                      size="sm"
                      radius="xl"
                      // Teal for a met goal, matching the series band's "downloads complete" bar,
                      // rather than Mantine's green, which is not in the app's palette.
                      color={done >= 1 ? 'teal' : 'brand'}
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
