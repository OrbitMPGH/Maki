import { useState } from 'react'
import { IconAffiliate } from '@tabler/icons-react'
import {
  useRootFolders,
  useSeriesIdLookup,
  useSeriesRelated,
  useUiSettings,
  type RecommendationItem,
} from '../api/hooks'
import { DiscoverDetailModal } from './DiscoverDetailModal'
import { DiscoverRailRow } from './ui/DiscoverRail'
import { SectionHeader } from './ui/SectionHeader'
import { Paper } from '@mantine/core'

/**
 * Sequels/prequels/spin-offs/side stories of this series that aren't already in the library,
 * for easy adding per the backlog item. Reuses Discover's rail + detail-modal add flow so the
 * card look and the Add affordance stay one thing, not two.
 *
 * Hideable from Settings, along with its sibling SimilarSeriesSection; the setting also gates the
 * query, so a hidden rail costs no request.
 */
export function RelatedSeriesSection({ seriesId }: { seriesId: number }) {
  const { data: ui } = useUiSettings()
  // `!== false` rather than a truthiness check: while the settings query is in flight the rail should
  // behave as it will once it lands (on, the default) instead of mounting a moment later.
  const enabled = ui?.seriesSections?.related !== false
  const { data: related } = useSeriesRelated(seriesId, enabled)
  const { data: rootFolders } = useRootFolders()
  const seriesIdFor = useSeriesIdLookup()
  const [detailItem, setDetailItem] = useState<RecommendationItem | null>(null)

  if (!enabled || !related || related.length === 0) return null

  return (
    <>
      <Paper withBorder radius="lg" p="lg">
        <SectionHeader icon={IconAffiliate} title="Related series" />
        <DiscoverRailRow items={related} seriesIdFor={seriesIdFor} onOpen={setDetailItem} />

        <DiscoverDetailModal
          item={detailItem}
          inLibrarySeriesId={detailItem ? seriesIdFor(detailItem) : null}
          rootFolders={rootFolders}
          onClose={() => setDetailItem(null)}
        />
      </Paper>
    </>
  )
}
