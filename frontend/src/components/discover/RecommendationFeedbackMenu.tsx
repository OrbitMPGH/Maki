import { ActionIcon, Button, Group, Menu, Text, Tooltip } from '@mantine/core'
import { notifications } from '@mantine/notifications'
import {
  IconDots, IconEye, IconEyeOff, IconClock, IconThumbUp, IconThumbDown,
} from '@tabler/icons-react'
import { Trans, useLingui } from '@lingui/react/macro'
import { useFeedbackState, useMutateFeedback, useUndoFeedback } from '../../api/recommendationFeedback'

type Command = {
  id: number
  action: string
  medium?: string
  expectedRevision: number
  clientMutationId: string
}

/**
 * Feedback actions for one catalogue title.
 *
 * Renders nothing for an item with no usable catalogue id, which is the only case where feedback is
 * meaningless: a synthetic activity row has nothing to record feedback against. Every other card is
 * fair game, including the browse rails: hiding a title there is the same fact about the same work.
 */
export function RecommendationFeedbackMenu({ providerId, surface }: { providerId: string; surface: string }) {
  const id = Number(providerId)
  const { t } = useLingui()
  const describe = useDescribe()
  const { data: state, isLoading } = useFeedbackState(id)
  const mutation = useMutateFeedback()
  const undo = useUndoFeedback()
  if (!Number.isSafeInteger(id) || id <= 0) return null

  const sentiment = state?.sentiment ?? 'none'
  const suppression = state?.suppression ?? 'none'
  const suppressed = suppression === 'hidden' || suppression === 'dismissed'
  const exposed = (state?.exposure.length ?? 0) > 0

  async function submit(action: string, medium?: string, retry?: Command) {
    // Falling back to 0 is what a title with no feedback yet needs. If the state query failed rather
    // than 404'd, a wrong guess here comes back as a 409 carrying the real state, which is a better
    // outcome than the disabled button this used to render.
    const command: Command = retry ?? {
      id, action, medium, expectedRevision: state?.revision ?? 0,
      clientMutationId: crypto.randomUUID(),
    }
    try {
      const result = await mutation.mutateAsync(command)
      // Previously a no-op returned silently, so pressing Hide on an already-hidden title looked
      // like a dead button. Say that nothing changed instead.
      if (!result.changed) {
        notifications.show({ message: t`No change: this title was already in that state.` })
        return
      }
      const eventId = result.eventId
      const revision = result.state.revision
      notifications.show({
        message: <Group gap="xs" wrap="wrap">
          <Text size="sm">{describe(result.feedbackEffect, result.queueEffect)}</Text>
          {eventId && <Button size="xs" variant="subtle" onClick={() => {
            void undo.mutateAsync({
              eventId, expectedRevision: revision,
              clientMutationId: crypto.randomUUID(),
            })
          }}><Trans>Undo</Trans></Button>}
        </Group>,
        autoClose: 8000,
      })
    } catch (error) {
      const reason = String(error)
      notifications.show({
        color: 'red', autoClose: false, message: <Group gap="xs" wrap="wrap">
          <Text size="sm"><Trans>Could not update {surface} feedback: {reason}</Trans></Text>
          <Button size="xs" variant="subtle" onClick={() => void submit(action, medium, command)}>
            <Trans>Retry</Trans>
          </Button>
        </Group>,
      })
    }
  }

  const busy = mutation.isPending || undo.isPending || isLoading

  return (
    <Group gap="xs" wrap="nowrap" justify="center">
      <Tooltip label={sentiment === 'liked' ? t`Remove your thumbs up` : t`More like this`} withArrow zIndex={2001}>
        <ActionIcon
          size="lg" variant={sentiment === 'liked' ? 'filled' : 'default'} color="teal"
          loading={busy} aria-pressed={sentiment === 'liked'} aria-label={t`Thumbs up`}
          onClick={() => void submit(sentiment === 'liked' ? 'clear-sentiment' : 'like')}
        >
          <IconThumbUp size={18} />
        </ActionIcon>
      </Tooltip>
      <Tooltip label={sentiment === 'disliked' ? t`Remove your thumbs down` : t`Not for me`} withArrow zIndex={2001}>
        <ActionIcon
          size="lg" variant={sentiment === 'disliked' ? 'filled' : 'default'} color="red"
          loading={busy} aria-pressed={sentiment === 'disliked'} aria-label={t`Thumbs down`}
          onClick={() => void submit(sentiment === 'disliked' ? 'clear-sentiment' : 'dislike')}
        >
          <IconThumbDown size={18} />
        </ActionIcon>
      </Tooltip>

      <Menu withinPortal position="bottom-end" width={260} zIndex={2000} shadow="md">
        <Menu.Target>
          <Tooltip label={t`More feedback options`} withArrow zIndex={2001}>
            <ActionIcon size="lg" variant="default" aria-label={t`More recommendation feedback actions`}>
              <IconDots size={18} />
            </ActionIcon>
          </Tooltip>
        </Menu.Target>
        <Menu.Dropdown>
          <Menu.Label>{t`Change this title only`}</Menu.Label>
          <Menu.Item leftSection={<IconEyeOff size={16} />} disabled={suppression === 'hidden'} onClick={() => void submit('hide')}>
            {t`Hide this title`}
          </Menu.Item>
          <Menu.Item leftSection={<IconClock size={16} />} disabled={suppression === 'dismissed'} onClick={() => void submit('dismiss')}>
            {t`Dismiss for 30 days`}
          </Menu.Item>
          <Menu.Divider />
          <Menu.Label>{t`Already read or seen, taste unchanged`}</Menu.Label>
          <Menu.Item leftSection={<IconEye size={16} />} onClick={() => void submit('mark-exposed', 'manga')}>
            {t`Read manga elsewhere`}
          </Menu.Item>
          <Menu.Item leftSection={<IconEye size={16} />} onClick={() => void submit('mark-exposed', 'anime')}>
            {t`Seen anime`}
          </Menu.Item>
          <Menu.Item leftSection={<IconEye size={16} />} onClick={() => void submit('mark-exposed', 'both')}>
            {t`Both`}
          </Menu.Item>
          <Menu.Divider />
          <Menu.Item disabled={!suppressed} onClick={() => void submit('clear-suppression')}>
            {t`Restore hidden or dismissed title`}
          </Menu.Item>
          <Menu.Item disabled={!exposed} onClick={() => void submit('clear-exposure')}>{t`Clear read or seen`}</Menu.Item>
          <Text size="xs" c="dimmed" px="sm" py="xs">
            {t`A thumbs down hides this title and pushes down titles closer to it than to what you kept. Hide only removes it.`}
          </Text>
        </Menu.Dropdown>
      </Menu>
    </Group>
  )
}

function useDescribe() {
  const { t } = useLingui()
  return (feedbackEffect: string, queueEffect: string): string => {
    switch (feedbackEffect) {
      case 'positive-title':
        return t`Thumbs up. This title now steers your recommendations, and it will not be recommended back to you.`
      case 'negative-taste':
        return t`Thumbs down. This title is out, and titles closer to it than to what you kept rank lower.`
      case 'negative-title':
        return t`Removed from recommendations. Only this title: its genres and author are unaffected.`
      case 'temporary':
        return t`Dismissed for 30 days. It comes back on its own.`
      case 'neutral-exposure':
        return t`Marked as already read or seen. Your taste profile was not changed.`
      default:
        return queueEffect === 'suppressed'
          ? t`Updated. This title stays out of recommendations.`
          : t`Updated. This title can appear in recommendations again.`
    }
  }
}
