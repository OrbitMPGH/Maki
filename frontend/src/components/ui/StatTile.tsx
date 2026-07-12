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
 */
export function StatTile({
  label,
  value,
  icon: IconCmp,
  accent = 'brand',
}: {
  label: string
  value: string | number
  icon: Icon
  accent?: keyof typeof ACCENT
}) {
  const color = ACCENT[accent] ?? ACCENT.brand
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
        </div>
        <IconCmp size={20} stroke={1.8} style={{ color, opacity: 0.9, flexShrink: 0 }} />
      </Group>
    </Card>
  )
}
