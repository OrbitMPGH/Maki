import { Card, Group, Text } from '@mantine/core'
import type { Icon } from '@tabler/icons-react'

const ACCENT: Record<string, string> = {
  brand: 'var(--brand)',
  ok: 'var(--ok)',
  warn: 'var(--warn)',
  info: 'var(--info)',
  danger: 'var(--danger)',
  gray: 'var(--mantine-color-dark-3)',
}

/**
 * Compact metric tile with a coloured left accent and an icon. Values use
 * tabular figures so a row of tiles stays aligned.
 *
 * `delta` is the fractional change against a comparison period (0.12 for +12%). Optional, and
 * omitted everywhere the tile has nothing to compare against — Home and the achievements panel
 * both show standing totals, which have no previous window.
 */
export function StatTile({
  label,
  value,
  icon: IconCmp,
  accent = 'brand',
  delta,
  deltaLabel,
  invertDelta = false,
}: {
  label: string
  value: string | number
  icon: Icon
  accent?: keyof typeof ACCENT
  /** Fractional change vs the previous period. Null means "compared, but the baseline was zero". */
  delta?: number | null
  /** What the comparison is against, e.g. "vs previous 30 days". Shown as a tooltip title. */
  deltaLabel?: string
  /** For metrics where up is bad (series dropped, removed). */
  invertDelta?: boolean
}) {
  const color = ACCENT[accent] ?? ACCENT.brand
  const hasDelta = delta !== undefined
  // A percentage change from a zero baseline is not a percentage. Print a dash rather than a
  // number that reads as an infinite improvement.
  const good = invertDelta ? (delta ?? 0) < 0 : (delta ?? 0) > 0
  const deltaColor =
    delta === null || delta === 0 ? 'var(--mantine-color-dimmed)' : good ? 'var(--ok)' : 'var(--danger)'

  return (
    <Card className="stat-tile" padding="md" radius="lg">
      <span className="stat-accent" style={{ background: color }} />
      <Group justify="space-between" align="flex-start" wrap="nowrap" gap="xs">
        <div style={{ minWidth: 0 }}>
          <Text size="xs" c="dimmed" fw={600} tt="uppercase" style={{ letterSpacing: '0.05em' }}>
            {label}
          </Text>
          <Text fz={26} fw={750} lh={1.1} mt={6} className="tnum">
            {value}
          </Text>
          {hasDelta && (
            <Text size="xs" fw={600} mt={4} className="tnum" style={{ color: deltaColor }} title={deltaLabel}>
              {delta === null
                ? '—'
                : `${delta > 0 ? '+' : ''}${Math.round(delta * 100)}%`}
            </Text>
          )}
        </div>
        <IconCmp size={20} stroke={1.8} style={{ color, opacity: 0.9, flexShrink: 0 }} />
      </Group>
    </Card>
  )
}
