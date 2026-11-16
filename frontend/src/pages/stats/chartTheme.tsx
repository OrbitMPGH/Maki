import type { CSSProperties } from 'react'

type ChartVars = CSSProperties & Record<`--${string}`, string>

/** Axis, grid and cursor look shared by every Mantine chart on Stats. Type size and the tooltip
 * surface live in theme.css under `.stats-chart` and `.mantine-ChartTooltip-tooltip`. */
const chartVars: ChartVars = {
  '--chart-grid-color': 'var(--border)',
  '--chart-text-color': 'var(--ink-3)',
  '--chart-cursor-fill': 'color-mix(in srgb, var(--ink) 6%, transparent)',
  '--chart-cursor-color': 'var(--border-strong)',
}

export const statsChartProps = {
  className: 'stats-chart',
  style: chartVars,
  tickLine: 'none',
  gridAxis: 'y',
  yAxisProps: { width: 64 },
} as const

/** Same series colours on every chart: the accent is you, grey is the comparison. */
export const statsSeriesColor = {
  you: 'var(--brand)',
  comparison: 'var(--neutral)',
  happening: 'var(--info)',
  done: 'var(--ok)',
  waiting: 'var(--warn)',
  failed: 'var(--danger)',
} as const

/** Thins the category labels to about eight so a long window does not print every bucket. */
export function statsXAxisProps(count: number) {
  return { scale: 'band' as const, interval: Math.max(0, Math.ceil(count / 8) - 1) }
}

export const statsGridProps = { syncWithTicks: true }
