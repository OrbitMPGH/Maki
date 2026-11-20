import type { ComponentType, ReactNode } from 'react'
import { Button, Text } from '@mantine/core'

type IconComponent = ComponentType<{ size?: number; 'aria-hidden'?: boolean }>

/**
 * A card's summary of what lives in its modal: the label, one line on what is there now, and the
 * button that opens it, named by what it does. Rows side by side share a hairline.
 */
export function SettingsSummaryRow({
  label,
  summary,
  icon: Icon,
  action,
  onClick,
  disabled,
  ...attrs
}: {
  label: ReactNode
  summary: ReactNode
  icon: IconComponent
  /** A verb for the button: "Manage rules", "Choose languages". */
  action: ReactNode
  onClick: () => void
  disabled?: boolean
  id?: string
  'data-new-control'?: string
}) {
  return (
    <div className="settings-summary-row" {...attrs}>
      <div className="settings-summary">
        <Text size="sm" fw={500}>
          {label}
        </Text>
        <Text component="div" size="sm" c="var(--ink-3)">
          {summary}
        </Text>
      </div>
      <Button
        variant="default"
        size="xs"
        leftSection={<Icon size={16} aria-hidden />}
        disabled={disabled}
        onClick={onClick}
        className="settings-summary-action"
      >
        {action}
      </Button>
    </div>
  )
}
