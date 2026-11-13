import { useId, type ComponentType, type ReactNode } from 'react'
import { Button, NumberInput, Slider, Switch, UnstyledButton, VisuallyHidden, type NumberInputProps } from '@mantine/core'
import { useLingui } from '@lingui/react/macro'
import { IconCheck, IconMinus } from '../../lib/icons'
import { SearchInput } from './SearchInput'
import { DrillField, type DrillChoice } from './DrillPanel'

/**
 * More pieces of the drill grammar (DrillPanel.tsx): lists you pick several from, an include and
 * leave-out list, a search box over a long list, range fields, a switch row, a group of rows under
 * a heading, and a footer pinned under the panes.
 */

type IconComponent = ComponentType<{ size?: number; className?: string; 'aria-hidden'?: boolean }>

/** Rows of a drill panel's first page under a plain heading. */
export function DrillGroup({ heading, children }: { heading: ReactNode; children: ReactNode }) {
  const id = useId()
  return (
    <div role="group" aria-labelledby={id} className="drill-group">
      <div id={id} className="drill-choices-heading">
        {heading}
      </div>
      {children}
    </div>
  )
}

/** Any number of choices from a list. Each one is a checkbox, drawn as a box in the check slot. */
export function DrillMultiChoices({
  label,
  heading,
  value,
  options,
  onChange,
  disabled,
}: {
  label: string
  heading?: ReactNode
  value: string[]
  options: DrillChoice[]
  onChange: (value: string[]) => void
  disabled?: boolean
}) {
  const headingId = useId()
  return (
    <div
      role="group"
      aria-label={heading ? undefined : label}
      aria-labelledby={heading ? headingId : undefined}
      className="drill-choices"
    >
      {heading && (
        <div id={headingId} className="drill-choices-heading">
          {heading}
        </div>
      )}
      {options.map((o) => {
        const checked = value.includes(o.value)
        return (
          <UnstyledButton
            key={o.value}
            role="checkbox"
            aria-checked={checked}
            data-checked={checked || undefined}
            disabled={disabled || o.disabled}
            className="drill-choice"
            onClick={() => onChange(checked ? value.filter((v) => v !== o.value) : [...value, o.value])}
          >
            <span className="drill-choice-check drill-box" aria-hidden>
              {checked && <IconCheck size={12} />}
            </span>
            <span className="drill-choice-text">
              <span className="drill-choice-label">{o.label}</span>
              {o.hint && <span className="drill-choice-hint">{o.hint}</span>}
            </span>
          </UnstyledButton>
        )
      })}
    </div>
  )
}

/**
 * Choices that can be included or left out. One tap includes, a second leaves out, a third clears.
 * Left out is a hollow box with a minus, the label struck through and "Left out" under it; a
 * screen reader hears "mixed" plus the word.
 */
export function DrillTriChoices({
  label,
  include,
  exclude,
  options,
  onChange,
  disabled,
}: {
  label: string
  include: string[]
  exclude: string[]
  options: DrillChoice[]
  onChange: (include: string[], exclude: string[]) => void
  disabled?: boolean
}) {
  const { t } = useLingui()
  return (
    <div role="group" aria-label={label} className="drill-choices">
      {options.map((o) => {
        const state = include.includes(o.value) ? 'in' : exclude.includes(o.value) ? 'out' : 'off'
        const cycle = () => {
          if (state === 'off') onChange([...include, o.value], exclude)
          else if (state === 'in') onChange(include.filter((v) => v !== o.value), [...exclude, o.value])
          else onChange(include, exclude.filter((v) => v !== o.value))
        }
        return (
          <UnstyledButton
            key={o.value}
            role="checkbox"
            aria-checked={state === 'in' ? true : state === 'out' ? 'mixed' : false}
            data-checked={state === 'in' || undefined}
            data-state={state}
            disabled={disabled || o.disabled}
            className="drill-choice"
            onClick={cycle}
          >
            <span className="drill-choice-check drill-box" aria-hidden>
              {state === 'in' && <IconCheck size={12} />}
              {state === 'out' && <IconMinus size={12} />}
            </span>
            <span className="drill-choice-text">
              <span className="drill-choice-label">{o.label}</span>
              {state === 'in' && <VisuallyHidden>{t`Included`}</VisuallyHidden>}
              {state === 'out' ? (
                <span className="drill-choice-hint">{t`Left out`}</span>
              ) : (
                o.hint && <span className="drill-choice-hint">{o.hint}</span>
              )}
            </span>
          </UnstyledButton>
        )
      })}
    </div>
  )
}

/** The search box over a long list of choices. `label` names it, and is its placeholder unless one is given. */
export function DrillSearch({
  label,
  placeholder,
  value,
  onChange,
}: {
  label: string
  placeholder?: string
  value: string
  onChange: (value: string) => void
}) {
  return (
    <div className="drill-search">
      <SearchInput
        placeholder={placeholder ?? label}
        aria-label={label}
        value={value}
        onChange={(e) => onChange(e.currentTarget.value)}
        onClear={() => onChange('')}
      />
    </div>
  )
}

export interface DrillBound {
  /** The box's accessible name ("Earliest year"). */
  label: string
  /** Shown while the box is empty ("From"). */
  placeholder: string
  value: number | null
  onChange: (value: number | null) => void
}

/** Two number boxes side by side, where an empty box means that end is open. */
export function DrillNumberRange({
  icon,
  title,
  help,
  from,
  to,
  inputProps,
  children,
}: {
  icon: IconComponent
  title: ReactNode
  help?: ReactNode
  from: DrillBound
  to: DrillBound
  inputProps?: Pick<NumberInputProps, 'min' | 'max' | 'allowDecimal' | 'allowNegative' | 'thousandSeparator'>
  /** A control above the boxes that changes what they count. */
  children?: ReactNode
}) {
  const box = (b: DrillBound) => (
    <NumberInput
      aria-label={b.label}
      className="field-default-value"
      placeholder={b.placeholder}
      {...inputProps}
      value={b.value ?? ''}
      onChange={(v) => b.onChange(toBound(v))}
    />
  )
  return (
    <DrillField icon={icon} title={title} help={help}>
      {children}
      <div className="drill-range">
        {box(from)}
        {box(to)}
      </div>
    </DrillField>
  )
}

interface SliderRangeProps {
  min: number
  max: number
  step: number
  value: [number, number]
  onChange: (value: [number, number]) => void
  /** Each thumb's accessible name ("Rating from"). */
  fromLabel: string
  toLabel: string
  format: (n: number) => string
}

/**
 * A range on two single sliders, From over To, each with its value read out beside it. Moving one
 * past the other pushes the other along, so the range never turns inside out. For a field that
 * holds other controls too; `DrillSliderRange` is the field with only these.
 */
export function DrillSliderLines({ min, max, step, value, onChange, fromLabel, toLabel, format }: SliderRangeProps) {
  const { t } = useLingui()
  const [lo, hi] = value
  const line = (name: string, thumb: string, v: number, set: (n: number) => void) => (
    <div className="drill-slider">
      <span className="drill-slider-name" aria-hidden>
        {name}
      </span>
      <Slider
        className="drill-slider-track"
        min={min}
        max={max}
        step={step}
        value={v}
        onChange={set}
        label={null}
        thumbLabel={thumb}
      />
      <span className="drill-slider-value tnum">{format(v)}</span>
    </div>
  )
  return (
    <div className="drill-sliders">
      {line(t`From`, fromLabel, lo, (n) => onChange([n, Math.max(n, hi)]))}
      {line(t`To`, toLabel, hi, (n) => onChange([Math.min(lo, n), n]))}
    </div>
  )
}

/** A range field: icon and name, the From and To sliders, and a line of help. */
export function DrillSliderRange({
  icon,
  title,
  help,
  ...range
}: SliderRangeProps & { icon: IconComponent; title: ReactNode; help?: ReactNode }) {
  return (
    <DrillField icon={icon} title={title} help={help}>
      <DrillSliderLines {...range} />
    </DrillField>
  )
}

/** A setting that is on or off, with its hint under the name and the switch at the end. */
export function DrillSwitch({
  label,
  hint,
  checked,
  onChange,
  size,
}: {
  label: ReactNode
  hint?: ReactNode
  checked: boolean
  onChange: (checked: boolean) => void
  /** `xs` for a panel set in small type, like the reader's. */
  size?: 'xs' | 'sm' | 'md'
}) {
  return (
    <Switch
      size={size}
      className="drill-switch"
      classNames={{ body: 'drill-switch-body', labelWrapper: 'drill-switch-text' }}
      labelPosition="left"
      label={label}
      description={hint}
      checked={checked}
      onChange={(e) => onChange(e.currentTarget.checked)}
    />
  )
}

/**
 * The footer under a drill panel that fills a drawer: Reset on the left, a live status (a result
 * count) and Done on the right. It stays put while the pages slide and scroll above it.
 */
export function DrillFooter({
  onReset,
  resetDisabled,
  status,
  onDone,
}: {
  onReset: () => void
  resetDisabled?: boolean
  status?: ReactNode
  onDone: () => void
}) {
  const { t } = useLingui()
  return (
    <div className="drill-footer">
      <Button className="drill-footer-reset" variant="subtle" color="var(--neutral)" onClick={onReset} disabled={resetDisabled}>
        {t`Reset`}
      </Button>
      <div className="drill-footer-end">
        {status != null && (
          <span className="drill-footer-status" role="status">
            {status}
          </span>
        )}
        <Button onClick={onDone}>{t`Done`}</Button>
      </div>
    </div>
  )
}

/**
 * An empty (or half-typed, e.g. a lone "-") box means "unbounded", not zero: a 0 lower bound would
 * silently filter nothing while looking like a bound.
 */
function toBound(v: string | number): number | null {
  const n = typeof v === 'number' ? v : Number(v)
  return v === '' || Number.isNaN(n) ? null : n
}
