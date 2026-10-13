import type { ChangeEvent, CSSProperties, HTMLAttributes, ReactNode, Ref } from 'react'
import {
  ActionIcon,
  Badge,
  FOCUS_CLASS_NAMES,
  Group,
  Loader,
  Stack,
  Switch,
  Text,
  UnstyledButton,
  rem,
  useMantineTheme,
  type MantineTheme,
  type VariantColorResolverResult,
} from '@mantine/core'
import { Link } from 'react-router-dom'

/*
 * Hook-free stand-ins for the Mantine components a chapter row is made of. They write the same
 * markup, classes and CSS variables the real ones do, minus the per-instance props, styles API and
 * context work, which made up most of a row's render on a page hundreds of rows long. Classes come
 * from each component's public `.classes` and colours from the theme's own resolver, so the theme
 * still applies. Only the props the rows use are supported.
 */

const resolved = new WeakMap<MantineTheme, Map<string, VariantColorResolverResult>>()

function variantColors(theme: MantineTheme, color: string, variant: string) {
  let byKey = resolved.get(theme)
  if (!byKey) resolved.set(theme, (byKey = new Map()))
  const key = `${variant}|${color}`
  let colors = byKey.get(key)
  if (!colors) {
    colors = theme.variantColorResolver({ color, theme, variant })
    byKey.set(key, colors)
  }
  return colors
}

const focusClass = (theme: MantineTheme) => theme.focusClassName || FOCUS_CLASS_NAMES[theme.focusRing]

const cx = (...names: (string | false | undefined)[]) => names.filter(Boolean).join(' ')

type Vars = CSSProperties & Record<`--${string}`, string | number | undefined>

type LightBadgeProps = Omit<HTMLAttributes<HTMLDivElement>, 'color'> & {
  color: string
  variant?: 'light' | 'filled' | 'outline'
  c?: string
  leftSection?: ReactNode
  rightSection?: ReactNode
  ref?: Ref<HTMLDivElement>
}

/** `<Badge size="sm">` with the theme's Badge defaults (radius sm, weight 600). */
export function LightBadge({ color, variant = 'light', c, leftSection, rightSection, className, children, ...rest }: LightBadgeProps) {
  const colors = variantColors(useMantineTheme(), color, variant)
  const style: Vars = {
    '--badge-height': 'var(--badge-height-sm)',
    '--badge-padding-x': 'var(--badge-padding-x-sm)',
    '--badge-fz': 'var(--badge-fz-sm)',
    '--badge-radius': 'var(--mantine-radius-sm)',
    '--badge-bg': colors.background,
    '--badge-color': colors.color,
    '--badge-bd': colors.border,
    fontWeight: 600,
    color: c,
  }
  return (
      <div
          {...rest}
          className={cx(className, Badge.classes.root, 'mantine-Badge-root')}
          data-variant={variant}
          data-size="sm"
          data-with-left-section={leftSection ? 'true' : undefined}
          data-with-right-section={rightSection ? 'true' : undefined}
          style={style}
      >
        {leftSection && (
            <span className={cx(Badge.classes.section, 'mantine-Badge-section')} data-position="left">
              {leftSection}
            </span>
        )}
        <span className={cx(Badge.classes.label, 'mantine-Badge-label')}>{children}</span>
        {rightSection && (
            <span className={cx(Badge.classes.section, 'mantine-Badge-section')} data-position="right">
              {rightSection}
            </span>
        )}
      </div>
  )
}

/** `<Text size="sm">`. */
export function LightText({ className, fw, c, lineClamp, children }: {
  className?: string
  fw?: number
  c?: string
  lineClamp?: number
  children?: ReactNode
}) {
  const style: Vars = {
    '--text-fz': 'var(--mantine-font-size-sm)',
    '--text-lh': 'var(--mantine-line-height-sm)',
    '--text-line-clamp': lineClamp === undefined ? undefined : String(lineClamp),
    fontWeight: fw,
    color: c,
  }
  return (
      <p
          className={cx(focusClass(useMantineTheme()), className, Text.classes.root, 'mantine-Text-root')}
          data-size="sm"
          data-line-clamp={lineClamp === undefined ? undefined : 'true'}
          style={style}
      >
        {children}
      </p>
  )
}

/** `<Group>` with `align="center"`. */
export function LightGroup({ gap, wrap, justify = 'flex-start', children }: {
  gap: number
  wrap: 'wrap' | 'nowrap'
  justify?: 'flex-start' | 'flex-end'
  children?: ReactNode
}) {
  const style: Vars = { '--group-gap': rem(gap), '--group-align': 'center', '--group-justify': justify, '--group-wrap': wrap }
  return <div className={cx(Group.classes.root, 'mantine-Group-root')} style={style}>{children}</div>
}

/** `<Stack align="flex-start">`. */
export function LightStack({ gap, children }: { gap: number; children?: ReactNode }) {
  const style: Vars = { '--stack-gap': rem(gap), '--stack-align': 'flex-start', '--stack-justify': 'flex-start' }
  return <div className={cx(Stack.classes.root, 'mantine-Stack-root')} style={style}>{children}</div>
}

type LightActionIconProps = {
  color: string
  variant: 'subtle' | 'light'
  'aria-label': string
  children: ReactNode
} & ({ to: string; onClick?: never; loading?: never } | { to?: never; onClick: () => void; loading?: boolean })

/** `<ActionIcon>` at its default size, as a button or, given `to`, a router link. */
export function LightActionIcon({ color, variant, to, onClick, loading, children, ...rest }: LightActionIconProps) {
  const theme = useMantineTheme()
  const colors = variantColors(theme, color, variant)
  const props = {
    ...rest,
    className: cx(
        focusClass(theme),
        !loading && theme.activeClassName,
        ActionIcon.classes.root,
        'mantine-ActionIcon-root',
        UnstyledButton.classes.root,
        'mantine-UnstyledButton-root',
    ),
    'data-variant': variant,
    style: {
      '--ai-bg': colors.background,
      '--ai-hover': colors.hover,
      '--ai-hover-color': colors.hoverColor,
      '--ai-color': colors.color,
      '--ai-bd': colors.border,
    } as Vars,
  }
  const icon = (
      <span className={cx(ActionIcon.classes.icon, 'mantine-ActionIcon-icon')} data-loading={loading || undefined}>
        {children}
      </span>
  )
  if (to !== undefined) return <Link {...props} to={to}>{icon}</Link>
  return (
      <button
          {...props}
          type="button"
          onClick={onClick}
          disabled={loading}
          aria-busy={loading || undefined}
          data-loading={loading || undefined}
      >
        {loading && (
            <span className={cx(ActionIcon.classes.loader, 'mantine-ActionIcon-loader')} aria-hidden>
              <Loader color="var(--ai-color)" size="calc(var(--ai-size) * 0.55)" />
            </span>
        )}
        {icon}
      </button>
  )
}

// Switch's own root class, which sets these, is not public: `Switch.classes.root` is the shared
// inline-input root. Values are Mantine's xs size and defaults.
const SWITCH_XS: Vars = {
  '--switch-height': 'calc(1rem * var(--mantine-scale))',
  '--switch-width': 'calc(2rem * var(--mantine-scale))',
  '--switch-thumb-size': 'calc(0.75rem * var(--mantine-scale))',
  '--switch-label-font-size': 'calc(0.3125rem * var(--mantine-scale))',
  '--switch-track-label-padding': 'calc(0.125rem * var(--mantine-scale))',
  '--switch-radius': 'calc(62.5rem * var(--mantine-scale))',
  '--switch-color': 'var(--mantine-primary-color-filled)',
  '--switch-disabled-color': 'var(--mantine-color-disabled)',
  '--label-fz': 'var(--mantine-font-size-xs)',
  '--label-lh': 'var(--label-lh-xs)',
  position: 'relative',
}

/** `<Switch size="xs">` with no label. */
export function LightSwitch({ checked, disabled, onChange, 'aria-label': ariaLabel }: {
  checked: boolean
  disabled: boolean
  onChange: (event: ChangeEvent<HTMLInputElement>) => void
  'aria-label': string
}) {
  const state = String(checked)
  return (
      <div
          className={cx(Switch.classes.root, 'mantine-Switch-root')}
          data-size="xs"
          data-label-position="right"
          data-checked={state}
          style={SWITCH_XS}
      >
        <label className={cx(Switch.classes.body, 'mantine-Switch-body')}>
          <input
              type="checkbox"
              role="switch"
              className={cx(Switch.classes.input, 'mantine-Switch-input')}
              checked={checked}
              data-checked={state}
              disabled={disabled}
              aria-label={ariaLabel}
              onChange={onChange}
          />
          <span
              className={cx(Switch.classes.track, 'mantine-Switch-track')}
              data-label-position="right"
              data-without-labels="true"
              aria-hidden="true"
          >
            <span
                className={cx(Switch.classes.thumb, 'mantine-Switch-thumb')}
                data-reduce-motion="true"
                data-with-thumb-indicator="true"
            />
            <span className={cx(Switch.classes.trackLabel, 'mantine-Switch-trackLabel')} />
          </span>
          <div className={cx(Switch.classes.labelWrapper, 'mantine-Switch-labelWrapper')} data-disabled={disabled || undefined} />
        </label>
      </div>
  )
}
