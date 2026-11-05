import { Trans, Plural } from '@lingui/react/macro'
import type { MidwaySeriesDto } from '../../api/stats'
import { EmptyState } from '../../components/ui/EmptyState'
import { formatNumber, formatReadingTime } from '../../format'
import { SeriesLink, SeriesThumb } from './SeriesLink'

/** The right-hand small line: how much is left, and how long it would take at your own pace. */
function MidwayHint({ left, etaSeconds }: { left: number; etaSeconds: number | null }) {
  if (etaSeconds === null) {
    return <Plural value={left} one="# left" other="# left" />
  }
  const eta = formatReadingTime(etaSeconds)
  return (
    <Trans>
      <Plural value={left} one="# left" other="# left" />, about {eta}
    </Trans>
  )
}

/**
 * Series read partway through: how far in, and roughly how much is left. One list, shared because
 * "mid-way" only ever means read against downloaded on a meter, whatever panel it sits in.
 */
export function MidwayList({ items, emptyText }: { items: MidwaySeriesDto[]; emptyText: string }) {
  if (items.length === 0) {
    return <EmptyState compact mood="asleep" title={emptyText} />
  }

  return (
    <ul className="stats-rows">
      {items.map((item) => {
        const total = Math.max(item.read, item.held)
        const pct = total > 0 ? (item.read / total) * 100 : 0
        return (
          <li className="stats-row stats-row-midway" key={item.seriesId}>
            <SeriesThumb url={item.coverUrl} alt={item.title} seriesId={item.seriesId} />
            <div style={{ minWidth: 0 }}>
              <div className="stats-row-title">
                <SeriesLink id={item.seriesId} title={item.title} />
              </div>
              <div className="stats-row-meter">
                <span style={{ width: `${pct}%` }} />
              </div>
            </div>
            <div className="stats-row-value">
              {formatNumber(item.read)} / {formatNumber(total)}
              <small>
                <MidwayHint left={total - item.read} etaSeconds={item.etaSeconds} />
              </small>
            </div>
          </li>
        )
      })}
    </ul>
  )
}
