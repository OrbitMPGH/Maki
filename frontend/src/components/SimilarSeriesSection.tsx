import { useState } from 'react'
import { IconSparkles } from '@tabler/icons-react'
import {
  useRootFolders,
  useSeriesIdLookup,
  useSeriesSimilar,
  useUiSettings,
  type RecommendationItem,
} from '../api/hooks'
import { DiscoverDetailModal } from './DiscoverDetailModal'
import { DiscoverRailRow } from './ui/DiscoverRail'
import { SectionHeader } from './ui/SectionHeader'

/**
 * Series that feel like this one, scored by the semantic recommender with this series as its only
 * seed. Sits below RelatedSeriesSection and answers the other half of the question: that rail shows
 * relations MangaBaka has declared, this one shows titles nobody has linked but that read alike.
 *
 * Unlike the other rails this one keeps the per-card reason line, because "why is this here" has no
 * other answer — there is no relation to name, only the tags and genres the pick shares with the seed.
 *
 * Renders nothing when the setting is off, when the series has no MangaBaka id, or when the embedding
 * index isn't built; the setting also gates the query, so a hidden rail costs no request.
 */
export function SimilarSeriesSection({ seriesId }: { seriesId: number }) {
  const { data: ui } = useUiSettings()
  // `!== false` rather than a truthiness check: while the settings query is in flight the rail should
  // behave as it will once it lands (on, the default) instead of mounting a moment later.
  const enabled = ui?.seriesSections?.similar !== false
  const { data: similar } = useSeriesSimilar(seriesId, enabled)
  const { data: rootFolders } = useRootFolders()
  const seriesIdFor = useSeriesIdLookup()
  const [detailItem, setDetailItem] = useState<RecommendationItem | null>(null)

  if (!enabled || !similar || similar.length === 0) return null

  return (
    <>
      <SectionHeader icon={IconSparkles} title="More like this" />
      <DiscoverRailRow items={similar} seriesIdFor={seriesIdFor} onOpen={setDetailItem} showReason />

      <DiscoverDetailModal
        item={detailItem}
        inLibrarySeriesId={detailItem ? seriesIdFor(detailItem) : null}
        rootFolders={rootFolders}
        onClose={() => setDetailItem(null)}
      />
    </>
  )
}
