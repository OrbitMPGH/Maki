import { Tooltip } from '@mantine/core'
import type { ReactNode } from 'react'
import type { MangaBakaSourceRating } from '../api/hooks'
import type { MetadataLink } from '../api/types'
import { formatFixedDecimal } from '../format'
import { MetadataLinks } from './MetadataLinks'
import { MetadataSiteIcon } from './MetadataSiteIcon'

/**
 * The site chips with each site's MangaBaka-normalised score on its own link, so the ratings and the
 * places they come from are one row. A rated site with no link keeps a plain chip.
 */
export function ScoredSiteChips({
  links,
  sourceRatings,
  extra,
}: {
  links: MetadataLink[]
  sourceRatings: MangaBakaSourceRating[]
  extra?: ReactNode
}) {
  const ratingBySite = new Map(sourceRatings.map((r) => [r.source.toLowerCase(), r.rating]))
  // Scored sites first: a bare mark leading the row read as a rating that failed to load.
  const scored = links
    .map((link) => {
      const rating = ratingBySite.get(link.site)
      return rating != null && link.score == null ? { ...link, score: formatFixedDecimal(rating / 10, 1) } : link
    })
    .sort((a, b) => Number(b.score != null) - Number(a.score != null))
  const unlinked = sourceRatings.filter((r) => !scored.some((link) => link.site === r.source.toLowerCase()))

  if (scored.length === 0 && unlinked.length === 0 && !extra) return null

  return (
    <MetadataLinks
      links={scored}
      chips
      extra={
        <>
          {unlinked.map((r) => (
            <Tooltip key={r.source} label={r.source} withArrow zIndex={1001}>
              <span className="site-chip" data-static tabIndex={0}>
                <MetadataSiteIcon site={r.source.toLowerCase()} monogram={r.source.slice(0, 2).toUpperCase()} size={15} />
                <span className="tnum">{formatFixedDecimal(r.rating / 10, 1)}</span>
              </span>
            </Tooltip>
          ))}
          {extra}
        </>
      }
    />
  )
}
