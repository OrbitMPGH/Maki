import type { ReactNode } from 'react'
import { Group, Stack, Text, Title } from '@mantine/core'

/**
 * Consistent page header: title (+ optional description) on the left, actions
 * on the right, wrapping gracefully on narrow screens.
 *
 * `face` is the one real choice here. A page you *arrive at* gets the display face and reads as a
 * headline; a page that is a form or a log gets Inter, because a 34px poster face above a settings
 * panel is just loud. See .claude/rules/design-system.md.
 */
const FACE_STYLES = {
  display: {
    fontFamily: 'var(--font-display)',
    fontWeight: 400,
    fontSize: '2.125rem',
    lineHeight: 1,
    letterSpacing: '0.012em',
    textTransform: 'uppercase',
  },
  text: {
    fontWeight: 700,
    fontSize: '1.75rem',
    lineHeight: 1.15,
    letterSpacing: '-0.02em',
  },
} as const

export function PageHeader({
  title,
  description,
  actions,
  face = 'display',
}: {
  title: ReactNode
  description?: ReactNode
  actions?: ReactNode
  face?: keyof typeof FACE_STYLES
}) {
  return (
    <Group justify="space-between" align="flex-end" wrap="wrap" gap="sm" mb="lg">
      <Stack gap={6} style={{ minWidth: 0 }}>
        <Title order={1} style={{ ...FACE_STYLES[face], color: 'var(--ink-hi)' }}>
          {title}
        </Title>
        {description && (
          <Text size="sm" c="dimmed" maw={620}>
            {description}
          </Text>
        )}
      </Stack>
      {actions && <Group gap="xs">{actions}</Group>}
    </Group>
  )
}
