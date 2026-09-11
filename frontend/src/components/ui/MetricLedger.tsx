import type { ReactNode } from 'react'
import type { Icon } from '@tabler/icons-react'

export type MetricLedgerTone = 'brand' | 'ok' | 'warn' | 'info' | 'danger' | 'neutral'

export type MetricLedgerItem = {
  label: string
  value: ReactNode
  icon?: Icon
  tone?: MetricLedgerTone
  detail?: ReactNode
}

const TONE_COLOR: Record<MetricLedgerTone, string> = {
  brand: 'var(--brand)',
  ok: 'var(--ok)',
  warn: 'var(--warn)',
  info: 'var(--info)',
  danger: 'var(--danger)',
  neutral: 'var(--neutral)',
}

export function MetricLedger({
  items,
  ariaLabel = 'Metrics',
  className,
}: {
  items: readonly MetricLedgerItem[]
  ariaLabel?: string
  className?: string
}) {
  const classes = ['metric-ledger', className].filter(Boolean).join(' ')

  return (
    <div className={classes} role="list" aria-label={ariaLabel}>
      {items.map((item, index) => {
        const IconCmp = item.icon
        const tone = TONE_COLOR[item.tone ?? 'neutral']

        return (
          <div className="metric-ledger-item" role="listitem" key={`${item.label}-${index}`}>
            <span className="metric-ledger-mark" style={{ color: tone }} aria-hidden="true">
              {IconCmp ? <IconCmp size={16} stroke={1.8} /> : <span className="metric-ledger-dot" />}
            </span>
            <div className="metric-ledger-label">{item.label}</div>
            <div className="metric-ledger-value tnum">{item.value}</div>
            {item.detail && <div className="metric-ledger-detail">{item.detail}</div>}
          </div>
        )
      })}
    </div>
  )
}
