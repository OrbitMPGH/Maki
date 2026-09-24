import { ActionIcon, Tooltip } from '@mantine/core'
import { notifications } from '@mantine/notifications'
import { IconBookmark, IconBookmarkFilled } from '@tabler/icons-react'
import { useLingui } from '@lingui/react/macro'
import { randomUUID } from '../../lib/uuid'
import { usePlanToRead, usePlanToReadAdd, usePlanToReadRemove } from '../../api/planToRead'
import type { RecommendationItem } from '../../api/hooks'

/**
 * Bookmark toggle for the Shortlist. Hidden once the title is already in the library: a shelf row
 * cannot also be "planned" for it.
 */
export function PlanToReadButton({
  item,
  origin = 'manual',
}: {
  item: RecommendationItem
  origin?: 'taste' | 'trending' | 'manual'
}) {
  const { t } = useLingui()
  const providerId = Number(item.providerId)
  const { data: entries } = usePlanToRead()
  const add = usePlanToReadAdd()
  const remove = usePlanToReadRemove()

  if (!Number.isSafeInteger(providerId) || providerId <= 0) return null

  const planned = entries?.some((e) => e.providerId === providerId) ?? false
  const busy = add.isPending || remove.isPending

  async function toggle() {
    try {
      if (planned) {
        await remove.mutateAsync(providerId)
      } else {
        await add.mutateAsync({ providerId, origin, clientMutationId: randomUUID() })
      }
    } catch (error) {
      notifications.show({ color: 'var(--danger)', message: String(error) })
    }
  }

  return (
    <Tooltip label={planned ? t`Remove from shortlist` : t`Add to shortlist`} withArrow zIndex={2001}>
      <ActionIcon
        size="lg"
        variant={planned ? 'filled' : 'default'}
        color="var(--info)"
        loading={busy}
        aria-pressed={planned}
        aria-label={planned ? t`Remove from shortlist` : t`Add to shortlist`}
        onClick={() => void toggle()}
      >
        {planned ? <IconBookmarkFilled size={18} /> : <IconBookmark size={18} />}
      </ActionIcon>
    </Tooltip>
  )
}
