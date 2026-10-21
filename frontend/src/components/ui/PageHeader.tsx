import type { ReactNode } from 'react'
import { Group, Stack, Text, Title } from '@mantine/core'

/**
 * Consistent page header: title (+ optional description) on the left, actions
 * on the right, wrapping gracefully on narrow screens. `compact` is the smaller
 * header for operational pages, where the content below is the point.
 */
export function PageHeader({
  title,
  description,
  actions,
  compact,
  className,
}: {
  title: ReactNode
  description?: ReactNode
  actions?: ReactNode
  compact?: boolean
  className?: string
}) {
  const heading = (
    <Stack gap={2} style={{ minWidth: 0 }} className="page-header-title">
      <Title order={1}>{title}</Title>
      {description && (
        <Text size="sm" c="var(--ink-3)" maw={620}>
          {description}
        </Text>
      )}
    </Stack>
  )

  return (
    <Group
      className={className ? `page-header ${className}` : 'page-header'}
      data-compact={compact || undefined}
      justify="space-between"
      align="flex-end"
      wrap="wrap"
      gap="sm"
      mb={compact ? 'md' : 'lg'}
    >
      {heading}
      {actions && <Group gap="xs">{actions}</Group>}
    </Group>
  )
}
