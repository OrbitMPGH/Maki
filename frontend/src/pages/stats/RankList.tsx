import { Group, Stack, Text } from '@mantine/core'
import type { Icon } from '../../lib/icons'
import { EmptyState } from '../../components/ui/EmptyState'
import { Panel } from '../../components/ui/Panel'
import { SeriesLink, SeriesThumb } from './SeriesLink'

export interface RankItem {
  seriesId: number | null
  title: string
  coverUrl: string | null
  /** Pre-formatted, because the three lists measure different things (chapters, hours). */
  value: string
}

/**
 * A ranked series list: cover, position, title, value.
 *
 * One component for all three rankings (most read, time spent, barely touched): they only ever
 * differed in how the right-hand cell was formatted, and keeping two near-identical tables around
 * is how they drift apart.
 */
export function RankList({
  icon: RankIcon,
  title,
  items,
  emptyText,
  emptyMood = 'asleep',
}: {
  icon: Icon
  title: string
  items: RankItem[]
  emptyText: string
  /** `pleased` when an empty list is good news ("Barely touched"). */
  emptyMood?: 'asleep' | 'pleased'
}) {
  return (
    <Panel p="md">
      <Group gap={8} mb="xs" wrap="nowrap">
        <RankIcon size={16} style={{ color: 'var(--brand)', flexShrink: 0 }} />
        <Text fw={650}>{title}</Text>
      </Group>
      {items.length === 0 ? (
        <EmptyState compact mood={emptyMood} title={emptyText} />
      ) : (
        <Stack gap={0}>
          {items.map((item, i) => (
            <div className="stats-rank-row" key={`${item.seriesId ?? item.title}-${i}`}>
              <Text c="var(--ink-3)" fw={700} size="sm" className="tnum stats-rank-num">
                {i + 1}
              </Text>
              <SeriesThumb url={item.coverUrl} alt={item.title} seriesId={item.seriesId} />
              <Text size="sm" truncate style={{ flex: 1, minWidth: 0 }}>
                <SeriesLink id={item.seriesId} title={item.title} />
              </Text>
              <Text size="sm" fw={600} className="tnum" style={{ flexShrink: 0 }}>
                {item.value}
              </Text>
            </div>
          ))}
        </Stack>
      )}
    </Panel>
  )
}
