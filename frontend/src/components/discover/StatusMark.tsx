import type { CSSProperties } from 'react'
import { VisuallyHidden } from '@mantine/core'
import { useLabel } from '../../i18n-context'
import { seriesStatusGlyph, seriesStatusVisual, statusColor, statusOnColor } from '../ui/status'

/**
 * A series' publication status as the catalogue card draws it under the title: the filled disc in
 * the status colour, then the word. For the list rows on Discover that show a pick without its
 * poster card, so status reads the same in a row as on a card.
 */
export function StatusMark({ status, word = true }: { status: string; word?: boolean }) {
  const label = useLabel()
  const visual = seriesStatusVisual(status)
  const Glyph = seriesStatusGlyph(status)
  return (
    <span className="discover-sub-state status-mark" title={word ? undefined : label(visual.label)}>
      <span
        className="discover-sub-disc"
        style={{ '--tone': statusColor(visual.color), '--tone-on': statusOnColor(visual.color) } as CSSProperties}
        aria-hidden
      >
        <Glyph size={10} />
      </span>
      {word ? <span className="discover-sub-word">{label(visual.label)}</span> : <VisuallyHidden>{label(visual.label)}</VisuallyHidden>}
    </span>
  )
}
