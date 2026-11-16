import type { ReactNode } from 'react'
import { Tooltip } from '@mantine/core'
import { useLingui } from '@lingui/react/macro'
import { useLabel } from '../../i18n-context'
import { IconBooks, IconDeviceTv } from '../../lib/icons'
import { ContentRatingChip } from '../ContentRatingChip'
import { seriesStatusVisual, statusToken } from '../ui/status'

/**
 * Status, content rating and type as the band's pills, shared by the series page and the Discover
 * modal so a result and the series it becomes read the same. The type is the quiet one.
 */
export function HeroChips({
  status,
  contentRating,
  typeLabel,
  animeName,
  hasAnime,
  tooltipZIndex,
  children,
}: {
  status: string
  contentRating: string | null | undefined
  typeLabel: string | null
  hasAnime?: boolean
  animeName?: string | null
  tooltipZIndex?: number
  children?: ReactNode
}) {
  const { t } = useLingui()
  const label = useLabel()
  const state = seriesStatusVisual(status)
  const tone = statusToken(state.color)
  const animeLabel = animeName?.trim() || t`Has an anime adaptation`

  return (
    <div className="hero-chips">
      <span className="series-hero-status" style={{ color: `var(--${tone})`, background: `var(--${tone}-soft)` }}>
        <state.Icon size={14} />
        {label(state.label)}
      </span>
      <ContentRatingChip rating={contentRating} tooltipZIndex={tooltipZIndex} />
      {typeLabel && (
        <span className="series-hero-status" data-quiet>
          <IconBooks size={14} />
          {typeLabel}
          {hasAnime && (
            <Tooltip label={animeLabel} withArrow zIndex={tooltipZIndex}>
              <span className="hero-anime-mark" role="img" aria-label={animeLabel} tabIndex={0}>
                <IconDeviceTv size={14} />
              </span>
            </Tooltip>
          )}
        </span>
      )}
      {children}
    </div>
  )
}
