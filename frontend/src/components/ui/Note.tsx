import type { CSSProperties, ReactNode } from 'react'
import { ActionIcon } from '@mantine/core'
import { IconAlertOctagon, IconAlertTriangle, IconCheck, IconInfoCircle, IconX, type Icon } from '../../lib/icons'

export type NoteTone = 'warn' | 'info' | 'danger' | 'ok' | 'neutral'

const NOTE_ICONS: Record<NoteTone, Icon> = {
  warn: IconAlertTriangle,
  info: IconInfoCircle,
  danger: IconAlertOctagon,
  ok: IconCheck,
  neutral: IconInfoCircle,
}

/**
 * One quiet line with an icon in the tone's colour, where a boxed Callout would outweigh the
 * content. Dialogs, settings panels and Health all use it.
 */
export function Note({
  tone = 'warn',
  icon,
  action,
  closeLabel,
  onClose,
  role,
  mt,
  mb,
  children,
}: {
  tone?: NoteTone
  icon?: Icon
  /** Sits at the end of the line, wrapping under the text when it does not fit. */
  action?: ReactNode
  closeLabel?: string
  onClose?: () => void
  /** Static notes announce politely; pass `alert` for one that appears in response to an action. */
  role?: 'alert' | 'status'
  /** Margin above, for a note that follows content in a panel. */
  mt?: boolean
  /** Margin below, for a note that sits above the page body. */
  mb?: boolean
  children: ReactNode
}) {
  const NoteIcon = icon ?? NOTE_ICONS[tone]
  const style: CSSProperties | undefined =
    mt || mb
      ? {
          marginTop: mt ? 'var(--mantine-spacing-md)' : undefined,
          marginBottom: mb ? 'var(--mantine-spacing-lg)' : undefined,
        }
      : undefined
  return (
    <div
      className="note"
      data-tone={tone}
      role={role ?? (tone === 'danger' || tone === 'warn' ? 'status' : undefined)}
      style={style}
    >
      <NoteIcon size={16} className="note-icon" aria-hidden />
      <div className="note-body">{children}</div>
      {action && <div className="note-action">{action}</div>}
      {closeLabel && onClose && (
        <ActionIcon variant="subtle" color="var(--ink-3)" size="sm" aria-label={closeLabel} onClick={onClose}>
          <IconX size={14} />
        </ActionIcon>
      )}
    </div>
  )
}
