import { Fragment } from 'react'
import type { RatedSeriesDto } from '../../../api/stats'
import { formatSignedDecimal } from '../../../format'

const SCALE_MAX = 10

/** Your rating against the community's, on a shared 0-10 track, one row per series. */
export function RatingDumbbell({ series }: { series: RatedSeriesDto[] }) {
  if (series.length === 0) return null

  return (
    <div className="stats-dumbbell">
      {series.map((s) => {
        const yoursPct = (Math.min(SCALE_MAX, Math.max(0, s.yours)) / SCALE_MAX) * 100
        const communityPct = (Math.min(SCALE_MAX, Math.max(0, s.community)) / SCALE_MAX) * 100
        const left = Math.min(yoursPct, communityPct)
        const width = Math.abs(yoursPct - communityPct)
        const gap = Math.round((s.yours - s.community) * 10) / 10
        const tone = gap > 0 ? 'pos' : gap < 0 ? 'neg' : undefined
        const gapLabel = formatSignedDecimal(gap)
        return (
          <Fragment key={s.seriesId}>
            <span className="stats-dumbbell-title" title={s.title}>
              {s.title}
            </span>
            <span className="stats-dumbbell-track">
              <span
                className="stats-dumbbell-line"
                style={{ left: `${left}%`, width: `${width}%` }}
              />
              <span
                className="stats-dumbbell-dot"
                style={{ left: `${communityPct}%` }}
                aria-hidden
              />
              <span
                className="stats-dumbbell-dot is-you"
                style={{ left: `${yoursPct}%` }}
                aria-hidden
              />
            </span>
            <span className={`stats-dumbbell-gap ${tone ?? ''}`}>{gapLabel}</span>
          </Fragment>
        )
      })}
    </div>
  )
}
