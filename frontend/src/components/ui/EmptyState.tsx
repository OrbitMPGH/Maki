import type { ReactNode } from 'react'
import { Button, Stack, Text, ThemeIcon } from '@mantine/core'
import type { Icon } from '@tabler/icons-react'
import { Link } from 'react-router-dom'

/** Friendly empty/zero-data state with an optional call to action. */
export function EmptyState({
  icon: IconCmp,
  title,
  description,
  actionLabel,
  actionTo,
  onAction,
}: {
  icon: Icon
  title: string
  description?: ReactNode
  actionLabel?: string
  actionTo?: string
  onAction?: () => void
}) {
  return (
    <Stack align="center" gap="sm" py={64} px="md">
      <ThemeIcon size={64} radius="xl" variant="light" color="gray">
        <IconCmp size={30} stroke={1.6} />
      </ThemeIcon>
      <Text fw={650} fz="lg">
        {title}
      </Text>
      {description && (
        <Text c="dimmed" size="sm" ta="center" maw={420}>
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
