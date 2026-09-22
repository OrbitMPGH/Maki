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
 */
export function FigureStrip({
  figures,
  flush,
  className,
}: {
  figures: Figure[]
  flush?: boolean
  className?: string
}) {
  return (
    <div className={className ? `figure-strip ${className}` : 'figure-strip'} data-flush={flush || undefined}>
      {figures.map((f) => (
        <div className="figure-strip-item" key={f.label} data-zero={f.value === 0 || undefined}>
          <span
            className="figure-strip-n tnum"
            style={f.tone && f.value > 0 ? { color: `var(--${f.tone})` } : undefined}
          >
            {formatNumber(f.value)}
          </span>
          <span className="figure-strip-l">{f.label}</span>
        </div>
      ))}
    </div>
  )
}
