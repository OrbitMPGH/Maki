import { useState } from 'react'
import { Button, Group, SimpleGrid, Text } from '@mantine/core'
import { IconChevronRight } from '@tabler/icons-react'
import type { DiscoverRail, RecommendationItem } from '../../api/hooks'
import { RecommendationCard } from '../ui/DiscoverRail'

/**
 * The catalogue rails as one grid with a chip row, instead of five stacked rails.
 *
 * <p>
 * Popular, Top rated, Newly released, Manhwa and Manhua are five answers to the same question, and
 * as five rails they cost five screens of scrolling to compare. One grid shows more of the chosen
 * one than a rail ever did, and switching is instant because every rail is already in hand: this
 * component adds no request of its own.
 * </p>
 */
export function DiscoverCatalogue({
  rails,
  cols,
  seriesIdFor,
  onOpen,
  onShowMore,
}: {
  /** The catalogue rails, in the order their chips should appear. */
  rails: DiscoverRail[]
  /** Columns per breakpoint, from the page's density. Poster size is the reader's call, not the
      window's, so this grid has no column count of its own. */
  cols: Record<string, number>
  seriesIdFor: (item: RecommendationItem) => number | null
  onOpen: (item: RecommendationItem) => void
  /** Opens the selected feed in Discover's filterable full catalogue view. */
  onShowMore: (rail: DiscoverRail) => void
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

      <Group justify="space-between" mt="md" mb="sm">
        <Text c="dimmed" size="sm">
          {active.items.length} title{active.items.length === 1 ? '' : 's'}
        </Text>
        <Button
          variant="subtle"
          size="xs"
          rightSection={<IconChevronRight size={14} />}
          onClick={() => onShowMore(active)}
        >
          Show more
        </Button>
      </Group>

      <SimpleGrid cols={cols} spacing="md" className="discover-cat-grid">
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
