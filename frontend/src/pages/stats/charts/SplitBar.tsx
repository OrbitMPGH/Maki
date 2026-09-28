import { useLingui } from '@lingui/react/macro'
import { formatPercent } from '../../../format'

export interface SplitBarItem {
  key: string
  /** Already rendered: a translated label, or a name that is data. */
  label: string
  value: number
  color?: string
}

const COLORS = ['var(--brand)', 'var(--info)', 'var(--warn)', 'var(--danger)', 'var(--neutral)']

/**
 * A 100% stacked bar with its legend. Every share is printed as text beside its name, so the
 * colours only group the bar with the legend and never carry the number alone.
 *
 * `max` caps the segments (default five, one per colour); the rest fold into "Other".
 */
export function SplitBar({ items, max = COLORS.length }: { items: SplitBarItem[]; max?: number }) {
  const { t } = useLingui()
  const positive = items.filter((i) => i.value > 0).sort((a, b) => b.value - a.value)
  const total = positive.reduce((sum, i) => sum + i.value, 0)
  if (total === 0) return null

  let shown = positive
  if (positive.length > max) {
    const rest = positive.slice(max - 1).reduce((sum, i) => sum + i.value, 0)
    shown = [...positive.slice(0, max - 1), { key: '__rest', label: t`Other`, value: rest }]
  }
  const segments = shown.map((item, i) => ({
    ...item,
    color: item.color ?? COLORS[i % COLORS.length],
    share: item.value / total,
  }))

  return (
    <div>
      <div className="stats-split" aria-hidden>
        {segments.map((s) => (
          <span
            key={s.key}
            className="stats-split-seg"
            style={{ flexGrow: s.value, flexBasis: 0, background: s.color }}
          />
        ))}
      </div>
      <ul className="stats-split-legend">
        {segments.map((s) => (
          <li key={s.key}>
            <span className="stats-legend-swatch" style={{ background: s.color }} aria-hidden />
            <b className="tnum">{formatPercent(s.share)}</b> {s.label}
          </li>
        ))}
      </ul>
    </div>
  )
}
