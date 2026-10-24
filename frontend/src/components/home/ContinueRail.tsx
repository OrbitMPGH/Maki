import type { HomeReadingItem } from '../../api/hooks'
import { ReadingRail } from './ReadingRail'

/**
 * Continue reading with its hero switched off. The scroll arrows live in the section header,
 * wired by the shared `Rail`, so this is the same `ReadingRail` every other caller renders.
 */
export function ContinueRail({ items }: { items: HomeReadingItem[] }) {
  return <ReadingRail items={items} />
}
