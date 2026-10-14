import type { CSSProperties } from 'react'
import { Tooltip } from '@mantine/core'
import { useLingui } from '@lingui/react/macro'
import { useLabel } from '../../i18n-context'
import { contentRatingVisual, seriesStatusVisual, statusToken } from './status'

/**
 * The publication status and content rating pills shared by the series band, the Discover hero and
 * the Discover modal, so a result and the series it becomes read as one object. Both are hairline
 * chips; only the status carries a hue, as a dot, so the row has one colour mark. The rating's word
 * says what it is without a colour of its own.
 */
export function HeroStatusPills({
  status,
  contentRating,
  tooltipZIndex,
}: {
  status: string
  contentRating: string | null
  tooltipZIndex?: number
}) {
  const { t } = useLingui()
  const label = useLabel()
  const visual = seriesStatusVisual(status)
  const rating = contentRatingVisual(contentRating)
  return (
    <>
      <span
        className="series-hero-status"
        data-tone
        style={{ '--dot': `var(--${statusToken(visual.color)})` } as CSSProperties}
      >
        {label(visual.label)}
      </span>
      {rating && (
        <Tooltip label={t`Content rating`} withArrow zIndex={tooltipZIndex}>
          <span className="series-hero-status" data-quiet>
            {label(rating.label)}
          </span>
        </Tooltip>
      )}
    </>
  )
}
