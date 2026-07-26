import { Badge, Group, Title, ThemeIcon } from '@mantine/core'
import type { Icon } from '@tabler/icons-react'

/**
 * The heading above a rail or grid: an accent icon, the title, an optional count badge, and an
 * optional right-aligned action ("Find more", "Refresh").
 *
 * Shared by Discover, the series page's related rail and the Home dashboard, which is why `count`
 * is optional — Home's rails already say how many items they hold by showing them.
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
    <Group gap="xs" mb="sm" mt="xl" wrap="nowrap">
      <ThemeIcon variant="light" color="brand" size="md" radius="md">
        <SectionIcon size={16} />
      </ThemeIcon>
      <Title order={4}>{title}</Title>
      {count != null && (
        <Badge variant="light" color="gray" size="sm">
          {count}
        </Badge>
      )}
      {action && <div style={{ marginLeft: 'auto' }}>{action}</div>}
    </Group>
  )
}
