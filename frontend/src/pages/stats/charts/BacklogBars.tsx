import { Fragment } from 'react'
import { Plural } from '@lingui/react/macro'
import type { BacklogSeriesDto } from '../../../api/stats'
import { SeriesLink } from '../SeriesLink'

/**
 * One stacked read/unread bar per series, widest bar set by whichever series holds the most
 * chapters among the ones shown, so the bars compare size honestly rather than each filling its
 * own row.
 */
export function BacklogBars({ items }: { items: BacklogSeriesDto[] }) {
  const widest = Math.max(1, ...items.map((s) => s.read + s.unread))

  return (
    <div className="stats-stack">
      {items.map((s) => {
        const unread = s.unread
        return (
          <Fragment key={s.seriesId}>
            <div className="stats-stack-label" title={s.title}>
              <SeriesLink id={s.seriesId} title={s.title} />
            </div>
            <div className="stats-stack-bar">
              <span className="stats-stack-seg is-read" style={{ width: `${(s.read / widest) * 100}%` }} />
              <span className="stats-stack-seg is-unread" style={{ width: `${(unread / widest) * 100}%` }} />
            </div>
            <span className="stats-stack-value tnum">
              <Plural value={unread} one="# left" other="# left" />
            </span>
          </Fragment>
        )
      })}
    </div>
  )
}
