import { Group, Title } from '@mantine/core'
import type { Icon } from '@tabler/icons-react'

/**
 * The heading above a shelf or grid: a quiet icon, the title, an optional count, and an
 * optional right-aligned action ("Find more", "Refresh").
 *
 * Shared by Discover, the series page's related rail and the Home dashboard, which is why `count`
 * is optional: Home's rails already say how many items they hold by showing them.
 */
export function SectionHeader({
  icon: SectionIcon,
  title,
  count,
  action,
}: {
  icon: Icon
  title: string
  count?: number
  action?: React.ReactNode
}) {
  return (
    <Group className="section-header" gap="xs" mb="sm" mt="xl" wrap="nowrap">
      <span className="section-header-mark" aria-hidden="true">
        <SectionIcon size={16} />
      </span>
      <Title order={4}>{title}</Title>
      {count != null && (
        <span className="section-header-count">{count}</span>
      )}
      {action && <div className="section-header-action">{action}</div>}
    </Group>
  )
}
