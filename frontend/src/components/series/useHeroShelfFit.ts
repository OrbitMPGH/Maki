import { type CSSProperties } from 'react'
import { useElementSize } from '@mantine/hooks'

/** Room kept between the title block and the shelf when the title is long enough to touch it. */
const SHELF_GAP = 16

/**
 * The shelf sits on the poster's bottom edge, so a long title has to make the poster taller rather
 * than push the shelf past it. The CSS can't size the poster from the column beside it (the
 * column's height comes from the poster), so the column's natural height is measured here and the
 * poster takes `max(its own minimum, --hero-content-h)`.
 */
export function useHeroShelfFit() {
  const ident = useElementSize()
  const shelf = useElementSize()
  // Content boxes: the shelf's 1px border is on top of what the observer reports.
  const natural = ident.height + SHELF_GAP + shelf.height + 2
  const style = (ident.height > 0 ? { '--hero-content-h': `${Math.ceil(natural)}px` } : undefined) as
    | CSSProperties
    | undefined
  return { identRef: ident.ref, shelfRef: shelf.ref, style }
}
