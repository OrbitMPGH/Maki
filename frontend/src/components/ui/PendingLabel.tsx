import type { ReactNode } from 'react'

/**
 * Button children that switch to `pending` while the Button is `loading`. Both labels share one grid
 * cell, so the button keeps the width of the longer one and nothing jumps. The swap and the dots are
 * pure CSS keyed off Mantine's `data-loading` (theme.css, Pending buttons).
 */
export function PendingLabel({ pending, children }: { pending: ReactNode; children: ReactNode }) {
  return (
    <span className="pending-label">
      <span className="pending-label-idle">{children}</span>
      <span className="pending-label-busy">{pending}</span>
    </span>
  )
}
