import { useMemo, useState } from 'react'
import { Group, Title, ThemeIcon } from '@mantine/core'
import { IconAffiliate } from '@tabler/icons-react'
import { useRootFolders, useSeries, useSeriesRelated, type RecommendationItem } from '../api/hooks'
import { DiscoverDetailModal } from './DiscoverDetailModal'
import { DiscoverRailRow } from '../pages/DiscoverPage'

/**
 * Sequels/prequels/spin-offs/side stories of this series that aren't already in the library —
 * "for easy adding" per the backlog item. Reuses Discover's rail + detail-modal add flow so the
 * card look and the Add affordance stay one thing, not two.
 */
export function RelatedSeriesSection({ seriesId }: { seriesId: number }) {
  const { data: related } = useSeriesRelated(seriesId)
  const { data: library } = useSeries()
  const { data: rootFolders } = useRootFolders()
  const [detailItem, setDetailItem] = useState<RecommendationItem | null>(null)

  const seriesIdByMangaBaka = useMemo(() => {
    const map = new Map<number, number>()
    for (const s of library ?? []) {
      if (s.mangaBakaId != null) map.set(s.mangaBakaId, s.id)
    }
    return map
  }, [library])
  const seriesIdFor = (item: RecommendationItem) =>
    seriesIdByMangaBaka.get(Number(item.providerId)) ?? null

  if (!related || related.length === 0) return null

  return (
    <>
      <Group gap="xs" mb="sm" mt="xl" wrap="nowrap">
        <ThemeIcon variant="light" color="brand" size="md" radius="md">
          <IconAffiliate size={16} />
        </ThemeIcon>
        <Title order={4}>Related series</Title>
      </Group>
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
