import { Link } from 'react-router-dom'
import { Text } from '@mantine/core'
import { useTasteProfile } from '../../api/hooks'

/** Facets named on the strip. Past five it stops being a summary and starts being the Taste tab. */
const SHOWN = 5

/**
 * One line saying what is steering the picks below it, with a way through to the full breakdown.
 *
 * <p>
 * The whole taste panel lives on its own tab and stays there — Discover only needs to answer "why
 * these", not "what am I". The share is the facet's slice of the weighted read history, which is
 * the same number the recommender's tag channel works from.
 * </p>
 */
export function DiscoverTasteStrip() {
  const { data } = useTasteProfile('read')
  const facets = data?.tags?.slice(0, SHOWN) ?? []
  if (facets.length === 0) return null

  return (
    <div className="discover-taste-strip">
      <Text className="discover-taste-lead">Steering these picks</Text>
      {facets.map((facet) => (
        <span key={facet.name} className="discover-taste-chip">
          <b>{facet.name}</b>
          <i aria-hidden style={{ '--w': `${Math.round(facet.share * 100)}%` } as React.CSSProperties} />
          <em className="tnum">{Math.round(facet.share * 100)}%</em>
        </span>
      ))}
      <Link to="/discover/taste" className="discover-taste-more">
        Your taste
      </Link>
    </div>
  )
}
