import type { ReactNode } from 'react'
import { Skeleton, Tooltip } from '@mantine/core'
import { formatNumber, formatSignedPercent } from '../../format'

export interface Figure {
  label: string
  /** A count, formatted here, or a value the caller already formatted (a duration, a percentage, "3 / 1"). */
  value: number | ReactNode
  /** Only coloured while non-zero: an empty "Failed" is good news and should not look like a warning. */
  tone?: 'ok' | 'warn' | 'danger' | 'info'
  /** Whether a formatted value stands for nothing, so it greys out like a zero count does. */
  zero?: boolean
  /** Change against the previous period as a fraction (0.12 prints +12%), as a chip beside the value. */
  delta?: number
  /** A dimmed line under the label, for a related count the figure does not headline. */
  hint?: ReactNode
  /** What the label means, in plain words, as a tooltip on the label. */
  tip?: string
}

/**
 * A row of labelled counts in one ruled strip, the operational counterpart of the hero figures.
 * `flush` drops the strip's own border and surface for when it already sits inside a panel.
 * `loading` keeps the labels and blanks the numbers, so a count never reads as zero before it arrives.
 * `quietWhenZero` shrinks the strip to one short line when every count is zero, so a row of large
 * zeros does not outweigh the empty state under it.
 * `deltaLabel` names what the deltas compare against; it is the chips' tooltip.
 */
export function FigureStrip({
  figures,
  flush,
  loading,
  quietWhenZero,
  deltaLabel,
  className,
}: {
  figures: Figure[]
  flush?: boolean
  loading?: boolean
  quietWhenZero?: boolean
  deltaLabel?: string
  className?: string
}) {
  const isZero = (f: Figure) => f.zero ?? f.value === 0
  const quiet = quietWhenZero && !loading && figures.every(isZero)
  return (
    <div
      className={className ? `figure-strip ${className}` : 'figure-strip'}
      data-flush={flush || undefined}
      data-quiet={quiet || undefined}
    >
      {figures.map((f) => {
        const zero = isZero(f)
        const n = (
          <span
            className="figure-strip-n tnum"
            style={f.tone && !zero ? { color: `var(--${f.tone})` } : undefined}
          >
            {typeof f.value === 'number' ? formatNumber(f.value) : f.value}
          </span>
        )
        return (
          <div className="figure-strip-item" key={f.label} data-zero={(!loading && zero) || undefined}>
            {loading ? (
              <div className="figure-strip-n">
                <Skeleton h="0.8em" w="2.4em" my="0.125em" />
              </div>
            ) : f.delta !== undefined && !zero ? (
              // "-100%" beside a 0 would repeat the 0 as an alarm, hence the zero check.
              <div className="figure-strip-value">
                {n}
                <span
                  className="figure-strip-delta tnum"
                  data-tone={Math.round(f.delta * 100) === 0 ? 'flat' : f.delta > 0 ? 'up' : 'down'}
                  title={deltaLabel}
                >
                  {formatSignedPercent(f.delta)}
                </span>
              </div>
            ) : (
              n
            )}
            {f.tip ? (
              <Tooltip label={f.tip} withArrow multiline maw={260} events={{ hover: true, focus: true, touch: true }}>
                <span className="figure-strip-l column-hint" tabIndex={0}>
                  {f.label}
                </span>
              </Tooltip>
            ) : (
              <span className="figure-strip-l">{f.label}</span>
            )}
            {f.hint && !loading && <span className="figure-strip-hint">{f.hint}</span>}
          </div>
        )
      })}
    </div>
  )
}
