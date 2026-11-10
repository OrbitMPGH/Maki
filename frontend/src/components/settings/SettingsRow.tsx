import { useId, type ReactNode } from 'react'
import { Collapse, UnstyledButton } from '@mantine/core'
import { IconChevronDown } from '../../lib/icons'
import { StatusDot } from '../ui/StatusDot'

export interface RowStatus {
  /** A status token stem: `ok`, `info`, `warn`, `danger` or `neutral`. */
  tone: string
  label: string
}

/**
 * Hairline-divided rows that fill a settings panel edge to edge, instead of a card per item inside
 * the card. Give the section `panelProps={{ p: 0 }}` and wrap padded fields in `SettingsRowsBlock`.
 */
export function SettingsRows({ children }: { children: ReactNode }) {
  return <div className="settings-rows">{children}</div>
}

export function SettingsRowsBlock({ children }: { children: ReactNode }) {
  return <div className="settings-rows-block">{children}</div>
}

/**
 * One item in `SettingsRows`: its name, optional chips, a status as a dot and a word, a one-line
 * summary, and the item's own fields when opened. Without `onToggle` the row is a plain line.
 * `lazy` mounts the body only while open, so reopening starts from the saved values.
 */
export function SettingsRow({
  title,
  chips,
  status,
  summary,
  opened = false,
  onToggle,
  lazy,
  aside,
  children,
}: {
  title: ReactNode
  chips?: ReactNode
  status?: RowStatus | null
  summary?: ReactNode
  opened?: boolean
  onToggle?: () => void
  lazy?: boolean
  /** Controls at the end of a plain row (no `onToggle`). */
  aside?: ReactNode
  children?: ReactNode
}) {
  const bodyId = useId()
  const expandable = onToggle !== undefined && children !== undefined

  const text = (
    <span className="settings-row-text">
      <span className="settings-row-line">
        <span className="settings-row-title">{title}</span>
        {chips}
      </span>
      {summary && <span className="settings-row-summary">{summary}</span>}
    </span>
  )
  const state = status && (
    <span className="settings-row-status">
      <StatusDot tone={status.tone}>{status.label}</StatusDot>
    </span>
  )

  return (
    <div className="settings-row" data-open={(expandable && opened) || undefined}>
      {expandable ? (
        <UnstyledButton
          className="settings-row-head"
          aria-expanded={opened}
          aria-controls={bodyId}
          onClick={onToggle}
        >
          {text}
          {state}
          <IconChevronDown size={16} className="settings-row-chevron" aria-hidden />
        </UnstyledButton>
      ) : (
        <div className="settings-row-head">
          {text}
          {state}
          {aside}
        </div>
      )}
      {expandable && (
        <Collapse expanded={opened} id={bodyId}>
          <div className="settings-row-body">{lazy && !opened ? null : children}</div>
        </Collapse>
      )}
    </div>
  )
}
