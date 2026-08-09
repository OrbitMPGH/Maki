import { Anchor, Card, Group, RingProgress, Stack, Text } from '@mantine/core'
import { IconFlame, IconTrophy } from '@tabler/icons-react'
import type { GamificationSummary } from '../../api/hooks'

function Figure({ value, label, icon: FigIcon }: { value: string | number; label: string; icon?: typeof IconFlame }) {
  return (
    <Stack gap={0} align="center" miw={72}>
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
 * Standing progression, condensed to one row: level, streak, badges.
 *
 * Overview is about a window; this is the one thing on the page that is not. It stays small and
 * links onward rather than repeating the Achievements tab's hero card, which shows the same
 * numbers with room to explain them.
 */
export function ProgressStrip({
  summary,
  onOpenAchievements,
}: {
  summary: GamificationSummary
  onOpenAchievements: () => void
}) {
  const { level } = summary

  return (
    <Card padding="md" radius="lg" withBorder>
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
          {summary.showStreaks && (
            <>
              <Figure value={summary.currentStreak} label="day streak" icon={IconFlame} />
              <Figure value={summary.longestStreak} label="best streak" />
            </>
          )}
          <Figure value={`${summary.earned}/${summary.total}`} label="achievements" icon={IconTrophy} />
          <Anchor component="button" type="button" size="sm" onClick={onOpenAchievements}>
            View all
          </Anchor>
        </Group>
      </Group>
    </Card>
  )
}
