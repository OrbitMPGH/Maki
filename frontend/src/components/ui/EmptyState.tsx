import type { ReactNode } from 'react'
import { Button, Stack, Text, ThemeIcon } from '@mantine/core'
import type { Icon } from '@tabler/icons-react'
import { Link } from 'react-router-dom'

/**
 * Friendly empty/zero-data state with an optional call to action. `compact` is for a section of an
 * operational page, where an empty queue is the normal state and should not take the screen.
 */
export function EmptyState({
  icon: IconCmp,
  title,
  description,
  actionLabel,
  actionTo,
  onAction,
  compact,
}: {
  icon: Icon
  title: string
  description?: ReactNode
  actionLabel?: string
  actionTo?: string
  onAction?: () => void
  compact?: boolean
}) {
  return (
    <Stack align="center" gap={compact ? 6 : 'sm'} py={compact ? 28 : 64} px="md">
      <ThemeIcon size={compact ? 40 : 64} radius="xl" variant="light" color="var(--neutral)">
        <IconCmp size={compact ? 20 : 30} stroke={1.6} />
      </ThemeIcon>
      <Text fw={650} fz={compact ? 'md' : 'lg'}>
        {title}
      </Text>
      {description && (
        <Text c="var(--ink-3)" size="sm" ta="center" maw={420}>
          {description}
        </Text>
      )}
      {actionLabel &&
        (actionTo ? (
          <Button component={Link} to={actionTo} mt="xs" variant="light">
            {actionLabel}
          </Button>
        ) : (
          <Button onClick={onAction} mt="xs" variant="light">
            {actionLabel}
          </Button>
        ))}
    </Stack>
  )
}
