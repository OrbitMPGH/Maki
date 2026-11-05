import { Tooltip } from '@mantine/core'
import { useLingui } from '@lingui/react/macro'
import { useLabel } from '../i18n-context'
import { contentRatingVisual } from './ui/status'

/**
 * The content rating beside a hero's status chip. Neutral outline for every rating: in the status
 * colours "Safe" read as the same green as "Completed", two different facts in one colour.
 */
export function ContentRatingChip({ rating, tooltipZIndex }: { rating: string | null | undefined; tooltipZIndex?: number }) {
  const { t } = useLingui()
  const label = useLabel()
  const visual = contentRatingVisual(rating ?? null)
  if (!visual) return null
  return (
    <Tooltip label={t`Content rating`} withArrow zIndex={tooltipZIndex}>
      <span className="series-hero-status" data-quiet data-content-rating={rating}>
        <visual.Icon size={14} />
        {label(visual.label)}
      </span>
    </Tooltip>
  )
}
