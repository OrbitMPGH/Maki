import type { ReactNode } from 'react'
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
 *
 * `variant="panel"` is the larger, shadowed strip used as a page's headline row. It can carry a
 * `middle` node in its own divider cell, placed after the first `middleAfter` figures (default: centred).
 */
export function FigureStrip({
  figures,
  flush,
  loading,
  className,
  variant = 'default',
  middle,
  middleAfter,
}: {
  figures: Figure[]
  flush?: boolean
  loading?: boolean
  className?: string
  variant?: 'default' | 'panel'
  middle?: ReactNode
  middleAfter?: number
}) {
  const cells: ReactNode[] = figures.map((f) => (
    <div className="figure-strip-item" key={f.label} data-zero={(!loading && f.value === 0) || undefined}>
      {loading ? (
        <div className="figure-strip-n figure">
          <Skeleton h="0.8em" w="2.4em" my="0.125em" />
        </div>
      ) : (
        <span
          className="figure-strip-n figure"
          style={f.tone && f.value > 0 ? { color: `var(--${f.tone})` } : undefined}
        >
          {formatNumber(f.value)}
        </span>
      )}
      <span className="figure-strip-l">{f.label}</span>
    </div>
  ))
  if (middle) {
    const split = Math.min(Math.max(middleAfter ?? Math.ceil(figures.length / 2), 0), figures.length)
    cells.splice(
      split,
      0,
      <div className="figure-strip-middle" key="middle">
        {middle}
      </div>,
    )
  }

  return (
    <div
      className={className ? `figure-strip ${className}` : 'figure-strip'}
      data-flush={flush || undefined}
      data-variant={variant === 'panel' ? 'panel' : undefined}
    >
      {cells}
    </div>
  )
}
