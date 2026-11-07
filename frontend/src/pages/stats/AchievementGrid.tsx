import { Badge, Card, Group, Progress, SimpleGrid, Stack, Text, Title, Tooltip } from '@mantine/core'
import { IconLock } from '../../lib/icons'
import { AchievementBadge, tierColor } from './AchievementBadge'
import { Trans, useLingui } from '@lingui/react/macro'
import type { Achievement } from '../../api/hooks'
import { useAuth } from '../../auth/AuthProvider'
import { formatNumber, formatReadingTime } from '../../format'

const TIER_SOFT_COLORS = [
  'var(--tier-1-soft)',
  'var(--tier-2-soft)',
  'var(--tier-3-soft)',
  'var(--tier-4-soft)',
  'var(--tier-5-soft)',
  'var(--tier-6-soft)',
]

/** Several tier hues miss 4.5:1 as small text; pulling them toward the ink keeps the hue and passes. */
function tierTextColor(tier: number): string {
  return `color-mix(in srgb, ${tierColor(tier)} 65%, var(--ink-hi))`
}

function tierSoftColor(tier: number): string {
  return TIER_SOFT_COLORS[Math.min(Math.max(tier, 1), TIER_SOFT_COLORS.length) - 1]
}

/**
 * Marathoner grades on seconds, everything else on a plain count. Formatting by magnitude rather
 * than carrying a unit on the definition keeps the server's DTO free of display concerns.
 */
export function formatAchievementValue(achievement: Achievement, value: number): string {
  if (achievement.key === 'marathoner') {
    return formatReadingTime(value)
  }

  return formatNumber(value)
}

const formatValue = formatAchievementValue

/** The tier floor the achievement is currently graded from: 0 until the first tier is earned. */
export function achievementFloor(achievement: Achievement): number {
  return achievement.tier > 0 ? achievement.tiers[achievement.tier - 1] : 0
}

/** 0..1 through the current tier. 1 for a maxed-out achievement (no next threshold). */
export function achievementFraction(achievement: Achievement): number {
  if (achievement.nextThreshold === null) return 1
  const floor = achievementFloor(achievement)
  const span = achievement.nextThreshold - floor
  return span <= 0 ? 1 : Math.min(1, Math.max(0, (achievement.value - floor) / span))
}

/**
 * Closest to its next tier first, maxed-out ones last, and an untouched lock (no progress at all)
 * sorted after anything already underway. Stable within each of those three groups.
 */
export function sortAchievements(achievements: Achievement[]): Achievement[] {
  const rank = (a: Achievement) => {
    if (a.nextThreshold === null) return 2
    return achievementFraction(a) > 0 ? 0 : 1
  }
  return [...achievements].sort((a, b) => {
    const diff = rank(a) - rank(b)
    return diff !== 0 ? diff : achievementFraction(b) - achievementFraction(a)
  })
}

/** Across every achievement still short of its top tier, the one nearest to its next one. */
export function closestToNextTier(achievements: Achievement[]): Achievement | null {
  let best: Achievement | null = null
  let bestFraction = -1
  for (const a of achievements) {
    if (a.nextThreshold === null) continue
    const fraction = achievementFraction(a)
    if (fraction > bestFraction) {
      bestFraction = fraction
      best = a
    }
  }
  return best
}

function AchievementCard({ achievement }: { achievement: Achievement }) {
  const { t } = useLingui()
  const earned = achievement.tier > 0
  const next = achievement.nextThreshold
  const progress = achievementFraction(achievement)
  const nextValue = next === null ? '' : formatValue(achievement, next)

  return (
    <Card
      withBorder
      radius="md"
      padding="md"
      style={{
        borderColor: earned ? tierColor(achievement.tier) : undefined,
        opacity: earned ? 1 : 0.85,
      }}
    >
      <Stack gap={6}>
        <Group justify="space-between" wrap="nowrap" gap="xs">
          <Group gap={8} wrap="nowrap" style={{ minWidth: 0 }}>
            <AchievementBadge icon={achievement.icon} tier={achievement.tier} earned={earned} />
            <Text fw={600} size="sm" lineClamp={1}>
              {achievement.name}
            </Text>
          </Group>
          {earned ? (
            achievement.graded && achievement.tierName ? (
              <Badge
                size="sm"
                variant="light"
                style={{
                  flexShrink: 0,
                  backgroundColor: tierSoftColor(achievement.tier),
                  color: tierTextColor(achievement.tier),
                }}
              >
                {achievement.tierName}
              </Badge>
            ) : (
              <Badge size="sm" variant="light" color="var(--ok)" style={{ flexShrink: 0 }}>
                <Trans>Earned</Trans>
              </Badge>
            )
          ) : (
            <IconLock size={16} opacity={0.5} />
          )}
        </Group>

        <Text size="xs" c="var(--ink-3)" lineClamp={2}>
          {achievement.description}
        </Text>

        {achievement.graded && (
          <>
            <Progress
              value={progress * 100}
              size="sm"
              radius="xl"
              color={earned ? tierColor(achievement.tier) : 'var(--neutral)'}
              aria-label={achievement.name}
            />
            <Group justify="space-between" gap="xs">
              <Text size="xs" c="var(--ink-3)">
                {formatValue(achievement, achievement.value)}
              </Text>
              {next !== null && (
                <Tooltip label={t`Next tier at ${nextValue}`}>
                  <Text size="xs" c="var(--ink-3)">
                    / {nextValue}
                  </Text>
                </Tooltip>
              )}
            </Group>
          </>
        )}
      </Stack>
    </Card>
  )
}

export function AchievementGrid({ achievements }: { achievements: Achievement[] }) {
  const reader = sortAchievements(achievements.filter((a) => a.track === 'Reader'))
  const library = sortAchievements(achievements.filter((a) => a.track === 'Library'))
  const { singleUser } = useAuth()

  return (
    <Stack gap="lg">
      <Stack gap="xs">
        <Title order={3} size="h4">
          <Trans>Reading</Trans>
        </Title>
        <SimpleGrid cols={{ base: 1, sm: 2, lg: 4 }} spacing="sm">
          {reader.map((a) => (
            <AchievementCard key={a.key} achievement={a} />
          ))}
        </SimpleGrid>
      </Stack>

      {library.length > 0 && (
        <Stack gap="xs">
          <Title order={3} size="h4">
            <Trans>The library</Trans>
          </Title>
          {/* Said plainly, because these count what is on disk and the library is shared: on a
              multi-user instance everybody sees the same numbers here, and presenting them beside
              the reading badges without saying so would read as a claim about the viewer. */}
          <Text size="sm" c="var(--ink-3)">
            {singleUser ? (
              <Trans>Earned by the library itself, not by your reading.</Trans>
            ) : (
              <Trans>Earned by the library itself, so everyone on this instance shares them.</Trans>
            )}
          </Text>
          <SimpleGrid cols={{ base: 1, sm: 2, lg: 4 }} spacing="sm">
            {library.map((a) => (
              <AchievementCard key={a.key} achievement={a} />
            ))}
          </SimpleGrid>
        </Stack>
      )}
    </Stack>
  )
}
