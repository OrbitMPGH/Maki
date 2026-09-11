import type { ReactNode } from 'react'
import { Button, Stack, Text, ThemeIcon } from '@mantine/core'
import type { Icon } from '@tabler/icons-react'
import { Link } from 'react-router-dom'

export type EmptyStateVariant = 'quiet' | 'setup' | 'filtered'

/** Friendly empty/zero-data state with an optional call to action. */
export function EmptyState({
  icon: IconCmp,
  title,
  description,
  actionLabel,
  actionTo,
  onAction,
  variant = 'quiet',
  className,
}: {
  icon: Icon
  title: string
  description?: ReactNode
  actionLabel?: string
  actionTo?: string
  onAction?: () => void
  variant?: EmptyStateVariant
  className?: string
}) {
  const classes = ['empty-state', `empty-state--${variant}`, className].filter(Boolean).join(' ')

  return (
    <Stack className={classes} align="center" gap="sm" py={64} px="md" data-variant={variant}>
      <ThemeIcon className="empty-state-icon" size={64} radius="xl" variant="light" color="gray">
        <IconCmp size={30} stroke={1.6} />
      </ThemeIcon>
      <Text className="empty-state-title" fw={650} fz="lg">
        {title}
      </Text>
      {description && (
        <Text className="empty-state-description" c="dimmed" size="sm" ta="center" maw={420}>
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
