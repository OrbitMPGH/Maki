import { Button, Group, Menu, Text } from '@mantine/core'
import { notifications } from '@mantine/notifications'
import { IconDots, IconEye, IconEyeOff, IconClock } from '@tabler/icons-react'
import { useFeedbackState, useMutateFeedback, useUndoFeedback } from '../../api/recommendationFeedback'

export function RecommendationFeedbackMenu({ providerId, surface }: { providerId: string; surface: string }) {
  const id = Number(providerId)
  const { data: state, isLoading, isError } = useFeedbackState(id)
  const mutation = useMutateFeedback()
  const undo = useUndoFeedback()
  if (!Number.isSafeInteger(id) || id <= 0) return null

  async function submit(action: string, medium?: string, retry?: {
    id: number; action: string; medium?: string; expectedRevision: number; clientMutationId: string
  }) {
    const command = retry ?? {
      id, action, medium, expectedRevision: state?.revision ?? 0,
      clientMutationId: crypto.randomUUID(),
    }
    try {
      const result = await mutation.mutateAsync(command)
      if (!result.changed) return
      notifications.show({
        message: <Group gap="xs" wrap="wrap">
          <Text size="sm">{result.queueEffect === 'suppressed'
            ? 'Title removed from recommendations. Your taste profile was not changed.'
            : 'Title can appear in recommendations again.'}</Text>
          {result.eventId && <Button size="xs" variant="subtle" onClick={() => {
            void undo.mutateAsync({
              eventId: result.eventId!, expectedRevision: result.state.revision,
              clientMutationId: crypto.randomUUID(),
            })
          }}>Undo</Button>}
        </Group>,
        autoClose: 8000,
      })
    } catch (error) {
      notifications.show({
        color: 'red', message: <Group gap="xs" wrap="wrap">
          <Text size="sm">Could not update {surface} feedback: {String(error)}</Text>
          <Button size="xs" variant="subtle" onClick={() => void submit(action, medium, command)}>Retry</Button>
        </Group>,
      })
    }
  }

  return (
    <Menu withinPortal position="bottom-end">
      <Menu.Target>
        <Button variant="light" color="gray" leftSection={<IconDots size={16} />}
          loading={mutation.isPending || isLoading} disabled={isError}
          aria-label="Recommendation feedback actions">
          Feedback
        </Button>
      </Menu.Target>
      <Menu.Dropdown>
        <Menu.Label>Change this title only</Menu.Label>
        <Menu.Item leftSection={<IconEyeOff size={16} />} onClick={() => void submit('hide')}>
          Hide this title
        </Menu.Item>
        <Menu.Item leftSection={<IconClock size={16} />} onClick={() => void submit('dismiss')}>
          Dismiss for 30 days
        </Menu.Item>
        <Menu.Divider />
        <Menu.Label>Already read or seen, taste unchanged</Menu.Label>
        <Menu.Item leftSection={<IconEye size={16} />} onClick={() => void submit('mark-exposed', 'manga')}>
          Read manga elsewhere
        </Menu.Item>
        <Menu.Item leftSection={<IconEye size={16} />} onClick={() => void submit('mark-exposed', 'anime')}>
          Seen anime
        </Menu.Item>
        <Menu.Item leftSection={<IconEye size={16} />} onClick={() => void submit('mark-exposed', 'both')}>
          Both
        </Menu.Item>
        <Menu.Divider />
        <Menu.Item onClick={() => void submit('clear-suppression')}>Restore hidden or dismissed title</Menu.Item>
        <Menu.Item onClick={() => void submit('clear-exposure')}>Clear read or seen</Menu.Item>
        <Text size="xs" c="dimmed" px="sm" py="xs">Hiding affects this title, not its genres or related works.</Text>
      </Menu.Dropdown>
    </Menu>
  )
}
