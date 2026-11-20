import { forwardRef, type ComponentPropsWithoutRef, type ReactNode } from 'react'
import { Trans, useLingui } from '@lingui/react/macro'
import { Menu, UnstyledButton, VisuallyHidden } from '@mantine/core'
import { IconChevronDown, IconSortAscending, IconSortDescending, type Icon } from '../lib/icons'
import { formatNumber } from '../format'

export type SortDirection = 'asc' | 'desc'

export interface SortOption<T extends string> {
  value: T
  label: string
  /** Which way this option orders, for the icon when the list has no separate direction. */
  direction?: SortDirection
}

/**
 * The one sort control every list uses: a filter-bar button showing the order in force, with the
 * direction as its icon, opening the options. `onReverse` adds a reverse item for a list whose
 * direction is its own setting.
 */
export function SortControl<T extends string>({
  value,
  options,
  onChange,
  direction,
  onReverse,
  className,
}: {
  value: T
  options: SortOption<T>[]
  onChange: (value: T) => void
  direction?: SortDirection
  onReverse?: () => void
  className?: string
}) {
  const { t } = useLingui()
  const current = options.find((o) => o.value === value)
  const dir = direction ?? current?.direction ?? 'desc'
  const DirIcon = dir === 'asc' ? IconSortAscending : IconSortDescending
  return (
    <Menu position="bottom-end" withinPortal>
      <Menu.Target>
        <UnstyledButton className={className ? `filter-bar-button ${className}` : 'filter-bar-button'}>
          <DirIcon size={16} aria-hidden className="filter-bar-button-icon" />
          <span className="filter-bar-button-value">
            <VisuallyHidden>{t`Sort by`}: </VisuallyHidden>
            {current?.label ?? value}
          </span>
          <IconChevronDown size={14} aria-hidden className="filter-bar-button-chevron" />
        </UnstyledButton>
      </Menu.Target>
      <Menu.Dropdown>
        <Menu.Label>
          <Trans>Sort by</Trans>
        </Menu.Label>
        <Menu.RadioGroup value={value} onChange={(v) => onChange(v as T)}>
          {options.map((o) => (
            <Menu.RadioItem key={o.value} value={o.value} closeMenuOnClick>
              {o.label}
            </Menu.RadioItem>
          ))}
        </Menu.RadioGroup>
        {onReverse && (
          <>
            <Menu.Divider />
            <Menu.Item
              leftSection={<DirIcon size={16} />}
              onClick={onReverse}
              rightSection={<span className="list-control-hint">{dir === 'asc' ? t`Ascending` : t`Descending`}</span>}
            >
              <Trans>Reverse sort order</Trans>
            </Menu.Item>
          </>
        )}
      </Menu.Dropdown>
    </Menu>
  )
}

export interface ChoiceOption {
  value: string
  label: string
  count?: number
}

function ChoiceLabel({ option }: { option: ChoiceOption }) {
  return (
    <>
      {option.label}
      {option.count != null && <span className="tag-chip-count tnum">{formatNumber(option.count)}</span>}
    </>
  )
}

interface FilterMenuBase {
  icon: Icon
  /** The button's name while nothing is picked, and its accessible prefix once something is. */
  label: string
  options: ChoiceOption[]
  /** Reports the menu opening and closing, for a dialog that has to hold its own Escape meanwhile. */
  onOpenChange?: (opened: boolean) => void
  disabled?: boolean
  className?: string
}

/** Menu.Target hands the face its ref and handlers, so both pass through. */
const FilterMenuFace = forwardRef<
  HTMLButtonElement,
  {
    icon: Icon
    label: string
    value: ReactNode
    disabled?: boolean
    className?: string
  } & Omit<ComponentPropsWithoutRef<'button'>, 'value'>
>(function FilterMenuFace({ icon: FaceIcon, label, value, disabled, className, ...rest }, ref) {
  const set = value != null
  return (
    <UnstyledButton
      {...rest}
      ref={ref}
      className={className ? `filter-bar-button ${className}` : 'filter-bar-button'}
      data-active={set || undefined}
      disabled={disabled}
      data-disabled={disabled || undefined}
    >
      <FaceIcon size={16} aria-hidden className="filter-bar-button-icon" />
      {set ? (
        <span className="filter-bar-button-value">
          <VisuallyHidden>{label}: </VisuallyHidden>
          {value}
        </span>
      ) : (
        <span>{label}</span>
      )}
      <IconChevronDown size={14} aria-hidden className="filter-bar-button-chevron" />
    </UnstyledButton>
  )
})

/**
 * A filter with too many choices for a chip row, as a filter-bar button opening one list. Picking
 * closes it; `anyLabel` adds a first item that clears the filter, for a filter that can be unset.
 */
export function FilterMenuButton({
  icon,
  label,
  options,
  value,
  onChange,
  anyLabel,
  onOpenChange,
  disabled,
  className,
}: FilterMenuBase & {
  value: string | null
  onChange: (value: string | null) => void
  anyLabel?: string
}) {
  const picked = options.find((o) => o.value === value)
  return (
    <Menu position="bottom-start" withinPortal onChange={onOpenChange} disabled={disabled}>
      <Menu.Target>
        <FilterMenuFace
          icon={icon}
          label={label}
          value={anyLabel != null && value == null ? null : (picked?.label ?? value)}
          disabled={disabled}
          className={className}
        />
      </Menu.Target>
      <Menu.Dropdown className="list-control-menu">
        <Menu.RadioGroup value={value ?? ''} onChange={(v) => onChange(v === '' ? null : v)}>
          {anyLabel != null && (
            <Menu.RadioItem value="" closeMenuOnClick>
              {anyLabel}
            </Menu.RadioItem>
          )}
          {options.map((o) => (
            <Menu.RadioItem key={o.value} value={o.value} closeMenuOnClick>
              <ChoiceLabel option={o} />
            </Menu.RadioItem>
          ))}
        </Menu.RadioGroup>
      </Menu.Dropdown>
    </Menu>
  )
}

/** The multi-select form of {@link FilterMenuButton}: each item toggles, and the menu stays open. */
export function FilterMultiMenuButton({
  icon,
  label,
  options,
  value,
  onChange,
  onOpenChange,
  disabled,
  className,
}: FilterMenuBase & {
  value: string[]
  onChange: (value: string[]) => void
}) {
  const { t } = useLingui()
  const names = value.map((v) => options.find((o) => o.value === v)?.label ?? v)
  return (
    <Menu position="bottom-start" withinPortal onChange={onOpenChange} disabled={disabled}>
      <Menu.Target>
        <FilterMenuFace
          icon={icon}
          label={label}
          value={value.length === 0 ? null : names.join(', ')}
          disabled={disabled}
          className={className}
        />
      </Menu.Target>
      <Menu.Dropdown className="list-control-menu">
        {options.map((o) => {
          const on = value.includes(o.value)
          return (
            <Menu.CheckboxItem
              key={o.value}
              checked={on}
              onChange={() => onChange(on ? value.filter((v) => v !== o.value) : [...value, o.value])}
            >
              <ChoiceLabel option={o} />
            </Menu.CheckboxItem>
          )
        })}
        {value.length > 0 && (
          <>
            <Menu.Divider />
            <Menu.Item onClick={() => onChange([])}>{t`Clear`}</Menu.Item>
          </>
        )}
      </Menu.Dropdown>
    </Menu>
  )
}
