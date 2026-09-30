import { Skeleton } from '@mantine/core'
import { formatNumber } from '../../format'

export interface Figure {
  label: string
  value: number
  /** Only coloured while non-zero: an empty "Failed" is good news and should not look like a warning. */
  tone?: 'ok' | 'warn' | 'danger' | 'info'
}

/**
 * A row of labelled counts in one ruled strip, the operational counterpart of the hero figures.
 * `flush` drops the strip's own border and surface for when it already sits inside a panel.
 * `loading` keeps the labels and blanks the numbers, so a count never reads as zero before it arrives.
 */
export function FigureStrip({
  figures,
  flush,
  loading,
  className,
}: {
  figures: Figure[]
  flush?: boolean
  loading?: boolean
  className?: string
}) {
  return (
    <div className={className ? `figure-strip ${className}` : 'figure-strip'} data-flush={flush || undefined}>
      {figures.map((f) => (
        <div className="figure-strip-item" key={f.label} data-zero={(!loading && f.value === 0) || undefined}>
          {loading ? (
            <div className="figure-strip-n">
              <Skeleton h="0.8em" w="2.4em" my="0.125em" />
            </div>
          ) : (
            <span
              className="figure-strip-n tnum"
              style={f.tone && f.value > 0 ? { color: `var(--${f.tone})` } : undefined}
            >
              {formatNumber(f.value)}
            </span>
          )}
          <span className="figure-strip-l">{f.label}</span>
        </div>
      ))}
    </div>
  )
}
