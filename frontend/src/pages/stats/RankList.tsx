import { Text } from '@mantine/core'
import type { Icon } from '../../lib/icons'
import { Panel } from '../../components/ui/Panel'
import { PanelTitle } from '../../components/ui/PanelTitle'
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
}: {
  icon: Icon
  title: string
  items: RankItem[]
  emptyText: string
}) {
  // An empty ranking is one line: a full-height card around one sentence left tall blank boxes
  // beside the lists that had something.
  if (items.length === 0) {
    return (
      <Panel p="sm" px="md" className="stats-rank-empty">
        <PanelTitle icon={RankIcon}>{title}</PanelTitle>
        <Text size="sm" c="var(--ink-3)" className="stats-rank-empty-text">{emptyText}</Text>
      </Panel>
    )
  }
  return (
    <Panel p="md">
      <PanelTitle icon={RankIcon}>{title}</PanelTitle>
      <ul className="stats-rows" style={{ marginTop: 8 }}>
        {items.map((item, i) => (
          <li className="stats-row stats-row-ranked" key={`${item.seriesId ?? item.title}-${i}`}>
            <span className="tnum stats-row-rank">{i + 1}</span>
            <SeriesThumb url={item.coverUrl} alt={item.title} seriesId={item.seriesId} />
            <span className="stats-row-title">
              <SeriesLink id={item.seriesId} title={item.title} />
            </span>
            <span className="stats-row-value">{item.value}</span>
          </li>
        ))}
      </ul>
    </Panel>
  )
}
