import type { HomeReadingItem } from '../../api/hooks'
import { ReadingRail } from './ReadingRail'
import type { ReadingRailKind } from './ReadingCardMenu'

/**
 * A reading section with its hero switched off. The scroll arrows live in the section header,
 * wired by the shared `Rail`, so this is the same `ReadingRail` every other caller renders.
 */
export function ContinueRail({ items, rail }: { items: HomeReadingItem[]; rail: ReadingRailKind }) {
  return <ReadingRail items={items} rail={rail} />
}
