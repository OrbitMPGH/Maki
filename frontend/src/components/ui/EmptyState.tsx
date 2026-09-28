import type { ReactNode } from 'react'
import { Button, Stack, Text } from '@mantine/core'
import { Link } from 'react-router-dom'

/**
 * What a section says when it has nothing to show: a plain statement, one line on what would fill
 * it, and at most one thing to do about it. Left-aligned with the content it stands in for, not a
 * centred icon card. `compact` is for a section of an operational page, where an empty queue is the
 * normal state and should not take the screen.
 */
export function EmptyState({
  title,
  description,
  actionLabel,
  actionTo,
  onAction,
  compact,
}: {
  title: string
  description?: ReactNode
  actionLabel?: string
  actionTo?: string
  onAction?: () => void
  compact?: boolean
}) {
  return (
    <Stack
      className="empty-state"
      data-compact={compact || undefined}
      align="flex-start"
      gap={4}
      py={compact ? 'sm' : 'xl'}
    >
      <Text className="empty-state-title">{title}</Text>
      {description && (
        <Text c="var(--ink-3)" size="sm" maw={520}>
          {description}
        </Text>
      )}
      {actionLabel &&
        (actionTo ? (
          <Button component={Link} to={actionTo} mt="sm" variant="default" size="sm">
            {actionLabel}
          </Button>
        ) : (
          <Button onClick={onAction} mt="sm" variant="default" size="sm">
            {actionLabel}
          </Button>
        ))}
    </Stack>
  )
}
