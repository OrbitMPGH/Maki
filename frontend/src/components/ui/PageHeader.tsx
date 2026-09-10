import type { ReactNode } from 'react'
import { Group, Stack, Text, Title } from '@mantine/core'

export type PageHeaderProps = {
  title: ReactNode
  description?: ReactNode
  actions?: ReactNode
  className?: string
}

/**
 * Consistent page header: title (+ optional description) on the left, actions
 * on the right, wrapping gracefully on narrow screens.
 */
export function PageHeader({
  title,
  description,
  actions,
  className,
}: PageHeaderProps) {
  const classes = ['page-header', className].filter(Boolean).join(' ')

  return (
    <Group className={classes} justify="space-between" align="flex-end" wrap="wrap" gap="sm" mb="lg">
      <Stack className="page-header-copy" gap={2}>
        <Title order={1}>{title}</Title>
        {description && (
          <Text size="sm" c="dimmed" maw={620}>
            {description}
          </Text>
        )}
      </Stack>
      {actions && <Group className="page-header-actions" gap="xs">{actions}</Group>}
    </Group>
  )
}
