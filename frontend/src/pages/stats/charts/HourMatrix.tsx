import { Fragment } from 'react'
import { Trans, useLingui } from '@lingui/react/macro'
import { formatHour, weekdayName } from '../../../format'

const HOURS = Array.from({ length: 24 }, (_, h) => h)
const WEEKDAYS = Array.from({ length: 7 }, (_, d) => d)

/**
 * Weekday-by-hour heat grid. `secondsByWeekdayHour` is 168 buckets, Monday first, index
 * `weekday * 24 + hour`. Intensity is a `color-mix` of `--brand` into `--surface-2` rather than a
 * fixed palette, so it tracks the theme's own brand colour instead of a hardcoded scale.
 */
export function HourMatrix({ secondsByWeekdayHour }: { secondsByWeekdayHour: number[] }) {
  const { t } = useLingui()
  const max = Math.max(1, ...secondsByWeekdayHour)

  return (
    <div className="stats-matrix-scroll">
      <div className="stats-matrix">
        <span className="stats-matrix-label" aria-hidden />
        {HOURS.map((hour) => (
          <span key={hour} className="stats-matrix-hour">
            {hour % 6 === 0 ? formatHour(hour) : ''}
          </span>
        ))}
        {WEEKDAYS.map((day) => (
          <Fragment key={day}>
            <span className="stats-matrix-label">{weekdayName(day, 'short')}</span>
            {HOURS.map((hour) => {
              const seconds = secondsByWeekdayHour[day * 24 + hour] ?? 0
              const minutes = Math.round(seconds / 60)
              const weekday = weekdayName(day, 'long')
              const hourLabel = formatHour(hour)
              const share = seconds > 0 ? 20 + (80 * seconds) / max : 0
              return (
                <span
                  key={hour}
                  className="stats-matrix-cell"
                  style={
                    seconds > 0
                      ? { background: `color-mix(in srgb, var(--brand) ${share}%, var(--surface-2))` }
                      : undefined
                  }
                  title={t`${weekday} ${hourLabel}: ${minutes} min`}
                />
              )
            })}
          </Fragment>
        ))}
      </div>
      <div className="stats-matrix-scale">
        <Trans>Less</Trans>
        <span style={{ background: 'var(--surface-2)' }} />
        <span style={{ background: 'color-mix(in srgb, var(--brand) 60%, var(--surface-2))' }} />
        <span style={{ background: 'var(--brand)' }} />
        <Trans>More</Trans>
      </div>
    </div>
  )
}
