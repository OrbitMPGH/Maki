import type { ReactNode } from 'react'
import { Group, Stack, Text, Title } from '@mantine/core'

/**
 * Consistent page header: title (+ optional description) on the left, actions
 * on the right, wrapping gracefully on narrow screens.
 *
 * With `aside` the layout turns into two columns instead: everything textual stacks on the left
 * with the actions under it, and the aside (a figures panel on Home and Stats) sits at the right,
 * bottom-aligned. Under `md` the aside drops below at full width.
 */
export function PageHeader({
  title,
  description,
  actions,
  eyebrow,
  aside,
}: {
  title: ReactNode
  description?: ReactNode
  actions?: ReactNode
  eyebrow?: ReactNode
  aside?: ReactNode
}) {
  if (aside) {
    return (
      <div className="page-header page-header-split">
        <Stack gap={2} style={{ minWidth: 0 }}>
          {eyebrow && <span className="page-header-eyebrow">{eyebrow}</span>}
          <Title order={1}>{title}</Title>
          {description && (
            <Text size="sm" c="var(--ink-3)" maw={620}>
              {description}
            </Text>
          )}
          {actions && (
            <Group gap="xs" mt="sm">
              {actions}
            </Group>
          )}
        </Stack>
        {aside}
      </div>
    )
  }

  return (
    <Group className="page-header" justify="space-between" align="flex-end" wrap="wrap" gap="sm" mb="lg">
      <Stack gap={2} style={{ minWidth: 0 }}>
        {eyebrow && <span className="page-header-eyebrow">{eyebrow}</span>}
        <Title order={1}>{title}</Title>
        {description && (
          <Text size="sm" c="var(--ink-3)" maw={620}>
            {description}
          </Text>
        )}
      </Stack>
      {actions && <Group gap="xs">{actions}</Group>}
    </Group>
  )
}
