import { t as now } from '@lingui/core/macro'
import { IconFlame } from '@tabler/icons-react'
import type { Icon } from '@tabler/icons-react'
import { engineWhy } from '../../ui/DiscoverRail'
import type { QueueCard } from '../../../api/discoverQueue'

/**
 * The queue's "why" line. Trending cards carry none of the recommender's per-item fields, so
 * {@link engineWhy} would read every one of them "Similar feel"; they get their own label instead.
 */
export function queueWhy(card: QueueCard): { Glyph: Icon; text: string } {
  if (card.origin === 'trending') return { Glyph: IconFlame, text: now`Trending now` }
  return engineWhy(card)
}
