import { useState } from 'react'
import { SimpleGrid } from '@mantine/core'
import type { DiscoverRail, RecommendationItem } from '../../api/hooks'
import { RecommendationCard } from '../ui/DiscoverRail'

/**
 * The catalogue rails as one grid with a chip row, instead of five stacked rails.
 *
 * <p>
 * Popular, Top rated, Newly released, Manhwa and Manhua are five answers to the same question, and
 * as five rails they cost five screens of scrolling to compare. One grid shows more of the chosen
 * one than a rail ever did, and switching is instant because every rail is already in hand — this
 * component adds no request of its own.
 * </p>
 */
export function DiscoverCatalogue({
  rails,
  seriesIdFor,
  onOpen,
}: {
  /** The catalogue rails, in the order their chips should appear. */
  rails: DiscoverRail[]
  seriesIdFor: (item: RecommendationItem) => number | null
  onOpen: (item: RecommendationItem) => void
}) {
  const [activeKey, setActiveKey] = useState<string | null>(null)
  if (rails.length === 0) return null

  const active = rails.find((r) => r.key === activeKey) ?? rails[0]

  return (
    <>
      <div className="discover-cat-tabs" role="tablist" aria-label="Catalogue feeds">
        {rails.map((rail) => (
          <button
            key={rail.key}
            type="button"
            role="tab"
            aria-selected={rail.key === active.key}
            className="discover-cat-tab"
            onClick={() => setActiveKey(rail.key)}
          >
            {rail.title}
          </button>
        ))}
      </div>

      <SimpleGrid
        cols={{ base: 2, xs: 3, sm: 4, md: 6, lg: 8 }}
        spacing="md"
        mt="md"
        className="discover-cat-grid"
      >
        {active.items.map((item) => (
          <RecommendationCard
            key={item.providerId}
            item={item}
            inLibrarySeriesId={seriesIdFor(item)}
            onOpen={onOpen}
          />
        ))}
      </SimpleGrid>
    </>
  )
}
