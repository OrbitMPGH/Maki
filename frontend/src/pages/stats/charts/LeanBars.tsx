import { Fragment } from 'react'
import { useLingui } from '@lingui/react/macro'
import type { GenreLeanDto } from '../../../api/stats'
import { useNameLabel, GENRE_LABELS } from '../labels'

/**
 * Diverging bars: how much more (or less) of a genre gets read than is actually owned. Centred on
 * zero, right is read more than owned, left is owned more than read.
 */
export function LeanBars({ items, max = 8 }: { items: GenreLeanDto[]; max?: number }) {
  const { t } = useLingui()
  const label = useNameLabel()
  if (items.length === 0) return null

  const withDiff = items.map((item) => ({ ...item, diff: item.readShare - item.libraryShare }))
  const shown = [...withDiff]
    .sort((a, b) => Math.abs(b.diff) - Math.abs(a.diff))
    .slice(0, max)
  const maxAbs = Math.max(...shown.map((i) => Math.abs(i.diff)), 0.0001)

  return (
    <div className="stats-lean">
      {shown.map((item) => {
        const pts = Math.round(item.diff * 100)
        const abs = Math.abs(pts)
        const widthPct = (Math.abs(item.diff) / maxAbs) * 50
        const valueLabel = pts > 0 ? t`+${abs} pt` : pts < 0 ? t`−${abs} pt` : t`${abs} pt`
        const tone = pts > 0 ? 'pos' : pts < 0 ? 'neg' : undefined
        return (
          <Fragment key={item.name}>
            <span className="stats-lean-name">{label(GENRE_LABELS, item.name)}</span>
            <span className="stats-lean-track">
              {tone && <span className={`stats-lean-bar ${tone}`} style={{ width: `${widthPct}%` }} />}
            </span>
            <span className={`stats-lean-value ${tone ?? ''}`}>{valueLabel}</span>
          </Fragment>
        )
      })}
    </div>
  )
}
