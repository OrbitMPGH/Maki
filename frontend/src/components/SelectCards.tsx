import { SimpleGrid, Text, UnstyledButton } from '@mantine/core'

export interface SelectCardOption<T extends string> {
  value: T
  title: string
  subtitle: string
}

/**
 * Row of equal-width tiles for picking one of a small fixed set of options (embedding model,
 * content rating ceiling, ...). Shared so every picker in the app looks and behaves the same.
 */
export function SelectCards<T extends string>({
  options,
  value,
  onChange,
  disabled,
  cols = options.length,
  fillLeft = false,
}: {
  options: SelectCardOption<T>[]
  value: T
  onChange: (value: T) => void
  disabled?: boolean
  cols?: number
  fillLeft: boolean
})
{
  return (
    <SimpleGrid cols={cols} spacing="sm">
      {options.map((o, index) => {
        const active = value === o.value
        const selectedIndex = options.findIndex((opt) => opt.value === value)
        return (
          <UnstyledButton
            key={o.value}
            disabled={disabled}
            onClick={() => {
              onChange(o.value)
            }}
            aria-pressed={active}
            style={{
              padding: '12px 14px',
              borderRadius: 10,
              border: `1px ${(fillLeft && index < selectedIndex) ? 'dashed' : 'solid'} ${active || (fillLeft && index < selectedIndex) ? 'var(--brand)' : 'var(--border)'}`,
              background: active || (fillLeft && index < selectedIndex) ? 'var(--surface-hover)' : 'transparent',
              boxShadow: active ? '0 0 0 1px var(--brand)' : undefined,
              opacity: disabled && !active ? 0.6 : 1,
              cursor: disabled ? 'default' : 'pointer',
            }}
          >
            <Text size="sm" fw={active ? 700 : 600}>
              {o.title}
            </Text>
            <Text size="xs" c="dimmed" mt={2}>
              {o.subtitle}
            </Text>
          </UnstyledButton>
        )
      })}
    </SimpleGrid>
  )
}
