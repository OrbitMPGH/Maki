import { ThemeIcon } from '@mantine/core'
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
  IconMoon,
  IconMoonStars,
  IconMountain,
  IconPlant,
  IconSunrise,
  IconWorld,
  type Icon,
} from '../../lib/icons'

/** Server icon keys (`AchievementCatalog.cs`) mapped to the closest Phosphor glyph. No literal owl icon
 * exists, so night-owl borrows the starry moon instead. */
const ICONS: Record<string, Icon> = {
  book: IconBook,
  clock: IconClock,
  flag: IconFlag,
  compass: IconCompass,
  calendar: IconCalendar,
  flame: IconFlame,
  sparkle: IconPlant,
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
 * badges deliberately keep their shape and lose only their colour: a badge you have not earned should
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

/** Tier 0 (an ungraded achievement) takes the first rung's colour. */
export function tierColor(tier: number): string {
  return TIER_COLORS[Math.min(Math.max(tier, 1), TIER_COLORS.length) - 1]
}

/** An achievement's badge: its glyph on a disc in its tier's colour. Stats and the notification feed share it. */
export function AchievementBadge({
  icon,
  tier,
  earned = true,
  size = 'sm',
}: {
  icon: string
  tier: number
  earned?: boolean
  size?: 'sm' | 'md'
}) {
  const Glyph = iconFor(icon)
  return (
    <ThemeIcon
      size={size}
      radius="xl"
      variant={earned ? 'light' : 'default'}
      color={earned ? tierColor(tier) : 'var(--neutral)'}
    >
      <Glyph size={16} />
    </ThemeIcon>
  )
}
