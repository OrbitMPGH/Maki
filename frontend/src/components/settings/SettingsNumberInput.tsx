import { useState } from 'react'
import { NumberInput, type NumberInputProps } from '@mantine/core'

type Props = Omit<NumberInputProps, 'value' | 'defaultValue' | 'onChange' | 'clampBehavior'> & {
  value: number
  onChange: (value: number) => void
  /**
 * A whole-number field for settings. The parent only ever sees a number, and only an in-range one:
 * a half-typed or out-of-range draft stays local until blur, which clamps it, and an emptied field
 * snaps back to the last value the parent was given. Mantine's own `strict` clamp rejects the first
 * digit of any value whose leading digit is below `min`, and an empty field would otherwise read as
 * 0, which several settings use to mean "off" or "no limit".
 */
  settle?: (value: number) => number
}

/**
 * A whole-number field for settings. The parent only ever sees a number, and only an in-range one:
 * a half-typed or out-of-range draft stays local until blur, which clamps it, and an emptied field
 * snaps back to the last value the parent was given. Mantine's own `strict` clamp rejects the first digit of any
 * value whose leading digit is below `min`, and an empty field would otherwise read as 0, which
 * several settings use to mean "off" or "no limit".
 */
export function SettingsNumberInput({ value, onChange, min, max, settle, onBlur, ...rest }: Props) {
  const [draft, setDraft] = useState<number | string>(value)
  const [seen, setSeen] = useState(value)
  if (value !== seen) {
    setSeen(value)
    if (draft !== value) setDraft(value)
  }

  const clamp = (n: number) => Math.min(max ?? Infinity, Math.max(min ?? -Infinity, n))

  return (
    <NumberInput
      allowDecimal={false}
      {...rest}
      min={min}
      max={max}
      clampBehavior="blur"
      value={draft}
      onChange={(next) => {
        setDraft(next)
        if (typeof next === 'number' && clamp(next) === next) onChange(next)
      }}
      onBlur={(event) => {
        const settled = typeof draft === 'number' ? (settle?.(clamp(draft)) ?? clamp(draft)) : value
        setDraft(settled)
        if (settled !== value) onChange(settled)
        onBlur?.(event)
      }}
    />
  )
}
