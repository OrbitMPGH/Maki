import { Tooltip } from '@mantine/core'
import { useLingui } from '@lingui/react/macro'
import { useLabel } from '../i18n-context'
import { contentRatingVisual } from './ui/status'

/**
 * The content rating beside a hero's status chip: outlined in its own colour (theme.css), on a scale
 * of its own so no rating reads as a status. Safe is teal, the cool end.
 */
export function ContentRatingChip({ rating, tooltipZIndex }: { rating: string | null | undefined; tooltipZIndex?: number }) {
  const { t } = useLingui()
  const label = useLabel()
  const visual = contentRatingVisual(rating ?? null)
  if (!visual) return null
  return (
    <Tooltip label={t`Content rating`} withArrow zIndex={tooltipZIndex}>
      <span className="series-hero-status" data-content-rating={rating}>
        <visual.Icon size={14} />
        {label(visual.label)}
      </span>
    </Tooltip>
  )
}
