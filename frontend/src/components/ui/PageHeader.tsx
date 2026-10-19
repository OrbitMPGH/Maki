import type { ReactNode } from 'react'
import { Group, Stack, Text, Title } from '@mantine/core'

/**
 * Consistent page header: title (+ optional description) on the left, actions
 * on the right, wrapping gracefully on narrow screens. `compact` is the smaller
 * header for operational pages, where the content below is the point.
 *
 * `center` sits between the title and the actions when the header is wide enough for all three
 * on one row, and drops to its own full-width row underneath when it is not (`.page-header-split`).
 */
export function PageHeader({
  title,
  description,
  actions,
  center,
  compact,
  className,
}: {
  title: ReactNode
  description?: ReactNode
  actions?: ReactNode
  center?: ReactNode
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

  if (center) {
    return (
      <div className="page-header-wrap" data-compact={compact || undefined}>
        <div className={className ? `page-header page-header-split ${className}` : 'page-header page-header-split'}>
          {heading}
          <div className="page-header-center">{center}</div>
          {actions && (
            <Group gap="xs" wrap="nowrap" className="page-header-actions">
              {actions}
            </Group>
          )}
        </div>
      </div>
    )
  }

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
