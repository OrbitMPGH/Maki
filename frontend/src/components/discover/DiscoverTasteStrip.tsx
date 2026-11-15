import { Skeleton } from '@mantine/core'
import { useTasteProfile } from '../../api/hooks'

/** Facets named on the strip. Past five it stops being a summary and starts being the Taste tab. */
const SHOWN = 5

/**
 * The tags steering the picks, at the top of the Your taste tab. Tags stay ordered by weighted
 * taste strength, while the percentage says how many of the reader's series carry the tag.
 */
export function DiscoverTasteStrip() {
  const { data, isLoading } = useTasteProfile('read')
  const facets = (data?.tags ?? []).slice(0, SHOWN)
  const seriesCount = data?.seriesCount ?? 0
  if (isLoading) {
    return (
      <div className="discover-taste-strip" aria-hidden>
        <div className="discover-taste-chips">
          {Array.from({ length: SHOWN }, (_, i) => (
            <Skeleton key={i} h={30} w={88 + (i % 3) * 14} radius="md" />
          ))}
        </div>
      </div>
    )
  }
  if (facets.length === 0) return null

  return (
    <div className="discover-taste-strip">
      <div className="discover-taste-chips">
        {facets.map((facet) => {
          const percent = seriesCount > 0 ? Math.round((facet.support / seriesCount) * 100) : 0
          return (
            <span key={facet.name} className="discover-taste-chip">
              <b>{facet.name}</b>
              <i aria-hidden style={{ '--w': `${percent}%` } as React.CSSProperties} />
              <em className="tnum">{percent}%</em>
            </span>
          )
        })}
      </div>
    </div>
  )
}
