import type { ReactNode } from 'react'
import { Group } from '@mantine/core'
import type { Icon } from '../../lib/icons'
import { Note } from '../ui/Note'

/**
 * The quiet line under the series tabs that asks one question (rate it, mark it completed, archive
 * it): a neutral Note with compact-sm actions right after the text and a dismiss. `below` holds
 * anything that hangs under the line, such as the rating words.
 */
export function SeriesHint({
  icon,
  actions,
  dismissLabel,
  onDismiss,
  below,
  children,
}: {
  icon: Icon
  actions?: ReactNode
  dismissLabel: string
  onDismiss: () => void
  below?: ReactNode
  children: ReactNode
}) {
  return (
    <div className="series-hint">
      <Note
        tone="neutral"
        icon={icon}
        action={actions && <Group gap="xs" wrap="wrap">{actions}</Group>}
        closeLabel={dismissLabel}
        onClose={onDismiss}
      >
        {children}
      </Note>
      {below}
    </div>
  )
}
