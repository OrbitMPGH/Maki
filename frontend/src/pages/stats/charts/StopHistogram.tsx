import { useLingui } from '@lingui/react/macro'
import { formatNumber, formatPercent } from '../../../format'

/**
 * Ten buckets of how far unfinished series got before stalling. The bucket the median falls in is
 * called out in `--warn` with a small label, everything else in the ordinary brand colour.
 */
export function StopHistogram({
  buckets,
  medianFraction,
}: {
  buckets: number[]
  medianFraction: number | null
}) {
  const { t } = useLingui()
  const max = Math.max(1, ...buckets)
  const medianIndex =
    medianFraction === null ? null : Math.min(buckets.length - 1, Math.floor(medianFraction * buckets.length))

  return (
    <div className="stats-hist">
      {buckets.map((count, i) => {
        const isMedian = i === medianIndex
        return (
          <div className="stats-hist-col" key={i}>
            <span className="stats-hist-value">{formatNumber(count)}</span>
            <div
              className="stats-hist-bar"
              style={{
                height: `${(count / max) * 100}%`,
                background: isMedian ? 'var(--warn)' : undefined,
              }}
            />
            <span className="stats-hist-label">{formatPercent(i / buckets.length)}</span>
            {/* Every column keeps the line, so the axis labels stay level and the word, wider than
                one column on a phone, spills into the empty space under its neighbours. */}
            {medianIndex !== null && <span className="stats-hist-note">{isMedian && t`Median`}</span>}
          </div>
        )
      })}
    </div>
  )
}
