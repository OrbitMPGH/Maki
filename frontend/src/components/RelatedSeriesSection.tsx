import { useState } from 'react'
import { IconAffiliate } from '@tabler/icons-react'
import {
  useRootFolders,
  useSeriesIdLookup,
  useSeriesRelated,
  type RecommendationItem,
} from '../api/hooks'
import { DiscoverDetailModal } from './DiscoverDetailModal'
import { DiscoverRailRow } from './ui/DiscoverRail'
import { SectionHeader } from './ui/SectionHeader'

/**
 * Sequels/prequels/spin-offs/side stories of this series that aren't already in the library —
 * "for easy adding" per the backlog item. Reuses Discover's rail + detail-modal add flow so the
 * card look and the Add affordance stay one thing, not two.
 */
export function RelatedSeriesSection({ seriesId }: { seriesId: number }) {
  const { data: related } = useSeriesRelated(seriesId)
  const { data: rootFolders } = useRootFolders()
  const seriesIdFor = useSeriesIdLookup()
  const [detailItem, setDetailItem] = useState<RecommendationItem | null>(null)

  if (!related || related.length === 0) return null

  return (
    <>
      <SectionHeader icon={IconAffiliate} title="Related series" />
      <DiscoverRailRow items={related} seriesIdFor={seriesIdFor} onOpen={setDetailItem} />

      <DiscoverDetailModal
        item={detailItem}
        inLibrarySeriesId={detailItem ? seriesIdFor(detailItem) : null}
        rootFolders={rootFolders}
        onClose={() => setDetailItem(null)}
      />
    </>
  )
}
