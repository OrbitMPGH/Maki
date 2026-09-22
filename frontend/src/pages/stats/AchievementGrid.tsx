import { Badge, Card, Group, Progress, SimpleGrid, Stack, Text, ThemeIcon, Title, Tooltip } from '@mantine/core'
import {
  IconArmchair,
  IconBook,
  IconCalendar,
  IconCheck,
  IconClock,
  IconCompass,
  IconConfetti,
  IconDownload,
  IconFlag,
  IconFlame,
  IconGhost,
  IconHelpHexagon,
  IconLibrary,
  IconLock,
  IconMoon,
  IconMoonStars,
  IconMountain,
  IconSparkles,
  IconSunrise,
  IconWorld,
  type Icon,
} from '@tabler/icons-react'
import { Trans, useLingui } from '@lingui/react/macro'
import type { Achievement } from '../../api/hooks'
import { formatNumber, formatReadingTime } from '../../format'

/** Server icon keys (`AchievementCatalog.cs`) mapped to the closest Tabler glyph. No literal owl icon
 * exists, so night-owl borrows the starry moon instead. */
const ICONS: Record<string, Icon> = {
  book: IconBook,
  clock: IconClock,
  flag: IconFlag,
  compass: IconCompass,
  calendar: IconCalendar,
  flame: IconFlame,
  sparkle: IconSparkles,
  moon: IconMoon,
  sofa: IconArmchair,
  mountain: IconMountain,
  check: IconCheck,
  globe: IconWorld,
  owl: IconMoonStars,
  sunrise: IconSunrise,
  ghost: IconGhost,
  confetti: IconConfetti,
  library: IconLibrary,
  download: IconDownload,
}

function iconFor(key: string): Icon {
  return ICONS[key] ?? IconHelpHexagon
}

/**
 * Tier colours run cool to warm so the ladder reads at a glance without needing the label. Locked
 * rows deliberately keep their shape and lose only their colour: a badge you have not earned should
 * still show you what it takes.
 */
const TIER_COLORS = [
  'var(--tier-1)',
  'var(--tier-2)',
  'var(--tier-3)',
  'var(--tier-4)',
  'var(--tier-5)',
  'var(--tier-6)',
]

const TIER_SOFT_COLORS = [
  'var(--tier-1-soft)',
  'var(--tier-2-soft)',
  'var(--tier-3-soft)',
  'var(--tier-4-soft)',
  'var(--tier-5-soft)',
  'var(--tier-6-soft)',
]

function tierColor(tier: number): string {
  return TIER_COLORS[Math.min(Math.max(tier, 1), TIER_COLORS.length) - 1]
}

function tierSoftColor(tier: number): string {
  return TIER_SOFT_COLORS[Math.min(Math.max(tier, 1), TIER_SOFT_COLORS.length) - 1]
}

/**
 * Marathoner grades on seconds, everything else on a plain count. Formatting by magnitude rather
 * than carrying a unit on the definition keeps the server's DTO free of display concerns.
 */
function formatValue(achievement: Achievement, value: number): string {
  if (achievement.key === 'marathoner') {
    return formatReadingTime(value)
  }

  return formatNumber(value)
}

function AchievementCard({ achievement }: { achievement: Achievement }) {
  const { t } = useLingui()
  const earned = achievement.tier > 0
  const next = achievement.nextThreshold
  const floor = achievement.tier > 0 ? achievement.tiers[achievement.tier - 1] : 0
  const progress =
    next === null ? 1 : Math.min(1, Math.max(0, (achievement.value - floor) / (next - floor)))
  const nextValue = next === null ? '' : formatValue(achievement, next)

  return (
    <Card
      withBorder
      radius="md"
      padding="md"
      style={{
        borderColor: earned ? tierColor(achievement.tier) : undefined,
        opacity: earned ? 1 : 0.72,
      }}
    >
      <Stack gap={6}>
        <Group justify="space-between" wrap="nowrap" gap="xs">
          <Group gap={8} wrap="nowrap" style={{ minWidth: 0 }}>
            <ThemeIcon
              size="sm"
              radius="xl"
              variant={earned ? 'light' : 'default'}
              color={earned ? tierColor(achievement.tier) : 'var(--neutral)'}
            >
              {(() => {
                const Icon = iconFor(achievement.icon)
                return <Icon size={14} />
              })()}
            </ThemeIcon>
            <Text fw={600} size="sm" lineClamp={1}>
              {achievement.name}
            </Text>
          </Group>
          {earned ? (
            achievement.graded && achievement.tierName ? (
              <Badge
                size="sm"
                variant="light"
                style={{ backgroundColor: tierSoftColor(achievement.tier), color: tierColor(achievement.tier) }}
              >
                {achievement.tierName}
              </Badge>
            ) : (
              <Badge size="sm" variant="light" color="var(--ok)">
                <Trans>Earned</Trans>
              </Badge>
            )
          ) : (
            <IconLock size={14} opacity={0.5} />
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
            />
            <Group justify="space-between" gap="xs">
              <Text size="xs" c="var(--ink-3)" className="tnum">
                {formatValue(achievement, achievement.value)}
              </Text>
              {next !== null && (
                <Tooltip label={t`Next tier at ${nextValue}`}>
                  <Text size="xs" c="var(--ink-3)" className="tnum">
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
  const reader = achievements.filter((a) => a.track === 'Reader')
  const library = achievements.filter((a) => a.track === 'Library')

  return (
    <Stack gap="lg">
      <Stack gap="xs">
        <Title order={4}>
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
          <Title order={4}>
            <Trans>The library</Trans>
          </Title>
          {/* Said plainly, because these count what is on disk and the library is shared: on a
              multi-user instance everybody sees the same numbers here, and presenting them beside
              the reading badges without saying so would read as a claim about the viewer. */}
          <Text size="sm" c="var(--ink-3)">
            <Trans>Earned by the library itself, so everyone on this instance shares them.</Trans>
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
