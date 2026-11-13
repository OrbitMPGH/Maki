import type { CSSProperties, ReactNode } from 'react'
import { ActionIcon } from '@mantine/core'
import { IconAlertOctagon, IconAlertTriangle, IconCheck, IconInfoCircle, IconX, type Icon } from '../../lib/icons'

export type CalloutTone = 'info' | 'warn' | 'danger' | 'ok' | 'neutral' | 'brand'

const CALLOUT_ICONS: Record<CalloutTone, Icon> = {
  info: IconInfoCircle,
  warn: IconAlertTriangle,
  danger: IconAlertOctagon,
  ok: IconCheck,
  neutral: IconInfoCircle,
  brand: IconInfoCircle,
}

/**
 * A tinted box for something the page wants noticed: tone icon, text, and optional buttons at the
 * end that wrap under the text on a narrow screen. Use Note for a line that needs no box.
 */
export function Callout({
  tone = 'warn',
  icon,
  title,
  actions,
  role,
  className,
  style,
  mt,
  mb,
  closeLabel,
  onClose,
  children,
}: {
  tone?: CalloutTone
  icon?: Icon
  title?: ReactNode
  actions?: ReactNode
  role?: 'status' | 'alert'
  className?: string
  style?: CSSProperties
  /** Margin above, as a Mantine spacing key, for a callout that follows content. */
  mt?: 'xs' | 'sm' | 'md'
  /** Margin below, as a Mantine spacing key. */
  mb?: 'xs' | 'sm' | 'md'
  /** With `onClose`, adds a dismiss button after the actions. */
  closeLabel?: string
  onClose?: () => void
  children: ReactNode
}) {
  const CalloutIcon = icon ?? CALLOUT_ICONS[tone]
  return (
    <div
      className={className ? `callout ${className}` : 'callout'} data-tone={tone}
      role={role}
      style={{
        ...(mt && { marginTop: `var(--mantine-spacing-${mt})` }),
        ...(mb && { marginBottom: `var(--mantine-spacing-${mb})` }),
        ...style,
      }}
    >
      <CalloutIcon size={16} className="callout-icon" aria-hidden />
      <div className="callout-body">
        {title && <div className="callout-title">{title}</div>}
        {children}
      </div>
      {actions && <div className="callout-actions">{actions}</div>}
      {closeLabel && onClose && (
        <ActionIcon variant="subtle" color="var(--ink-3)" size="sm" aria-label={closeLabel} onClick={onClose}>
          <IconX size={14} />
        </ActionIcon>
      )}
    </div>
  )
}
