import { Link } from 'react-router-dom'
import { Skeleton, Text } from '@mantine/core'
import { useTasteProfile } from '../../api/hooks'

/** Facets named on the strip. Past five it stops being a summary and starts being the Taste tab. */
const SHOWN = 5

/**
 * One line saying what is steering the picks below it, with a way through to the full breakdown.
 *
 * <p>
 * The whole taste panel lives on its own tab and stays there. Discover only needs to answer "why
 * these", not "what am I". Tags stay ordered by weighted taste strength, while the percentage says
 * how many of the reader's series carry the tag.
 * </p>
 */
export function DiscoverTasteStrip() {
  const { data, isLoading } = useTasteProfile('read')
  const facets = [...(data?.tags ?? [])]
    .sort((a, b) => b.support - a.support || a.name.localeCompare(b.name))
    .slice(0, SHOWN)
  const seriesCount = data?.seriesCount ?? 0
  if (isLoading) {
    return (
      <div className="discover-taste-strip" aria-hidden>
        <Skeleton h={12} w={120} />
        {Array.from({ length: SHOWN }, (_, i) => (
          <Skeleton key={i} h={30} w={88 + (i % 3) * 14} radius="md" />
        ))}
      </div>
    )
  }
  if (facets.length === 0) return null

  return (
    <div className="discover-taste-strip">
      <Text className="discover-taste-lead">Steering these picks</Text>
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
      <Link to="/discover/taste" className="discover-taste-more">
        Your taste
      </Link>
    </div>
  )
}
