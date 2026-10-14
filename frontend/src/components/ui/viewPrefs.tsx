import { type ComponentPropsWithRef, useMemo, useState } from 'react'
import { ActionIcon, Menu, SegmentedControl, Tooltip } from '@mantine/core'
import { IconCheck, IconDots, IconLayoutGrid, IconLayoutList } from '@tabler/icons-react'
import { msg } from '@lingui/core/macro'
import { useLingui as useLinguiMacro } from '@lingui/react/macro'
import { useLingui } from '@lingui/react'
import type { MessageDescriptor } from '@lingui/core'

/**
 * The grid/list and density controls shared by Library, Discover, Add series and the creator page.
 *
 * Library and Discover each grew their own copy of this, character for character, down to the
 * localStorage helpers. Adding a third copy for the Add page is what finally made it worth one
 * module. Storage keys are kept as each page already wrote them (`library-view`, `discover-density`
 * and so on), so nobody's stored preference resets on upgrade.
 */

export type ViewMode = 'grid' | 'list'
export type Density = 'compact' | 'default' | 'comfortable'

const DENSITIES_WITH_LABELS: { value: Density; label: MessageDescriptor }[] = [
  { value: 'compact', label: msg`Compact` },
  { value: 'default', label: msg`Default` },
  { value: 'comfortable', label: msg`Comfortable` },
]

/** The density list with its labels rendered, in the shape Mantine's `data` prop wants. */
export function useDensityOptions(): { value: Density; label: string }[] {
  const { _, i18n } = useLingui()
  return useMemo(
    () => DENSITIES_WITH_LABELS.map((o) => ({ ...o, label: _(o.label) })),
    [_, i18n.locale],
  )
}

/**
 * Poster columns per breakpoint at each density, as the Library grid has always counted them.
 *
 * The `lg` step matters more than it looks: without it a grid holds its `md` count from 62em all
 * the way to 88em, and a card on a 1400px window ends up half again as wide as the same card at
 * the same density in the Library. That gap is what made Discover's posters look like they sized
 * from the window rather than from this control.
 */
export const POSTER_COLS_BY_DENSITY: Record<Density, Record<string, number>> = {
  compact: { base: 3, xs: 4, sm: 5, md: 6, lg: 8, xl: 10 },
  default: { base: 2, xs: 3, sm: 4, md: 5, lg: 6, xl: 8 },
  comfortable: { base: 2, xs: 2, sm: 3, md: 4, lg: 5, xl: 6 },
}

const VIEW_MODES: readonly ViewMode[] = ['grid', 'list']
const DENSITIES: readonly Density[] = ['compact', 'default', 'comfortable']

export function readStored<T extends string>(key: string, valid: readonly T[], fallback: T): T {
  try {
    const v = localStorage.getItem(key)
    return valid.includes(v as T) ? (v as T) : fallback
  } catch {
    return fallback
  }
}

export function writeStored(key: string, value: string) {
  try {
    localStorage.setItem(key, value)
  } catch {
    /* private mode, quota, a browser with storage disabled: a lost preference is not an error */
  }
}

/**
 * View mode and density for one surface, persisted per `scope`. Storage rather than a user setting:
 * it is a per-device preference, and a round trip on first paint would make the grid jump.
 */
export function useViewPrefs(scope: string) {
  const viewKey = `${scope}-view`

  const [viewMode, setView] = useState<ViewMode>(() => readStored(viewKey, VIEW_MODES, 'grid'))
  const densityPrefs = useDensityPref(scope)

  const setViewMode = (mode: ViewMode) => {
    setView(mode)
    writeStored(viewKey, mode)
  }

  return { viewMode, setViewMode, ...densityPrefs }
}

/**
 * Density alone, for a surface with no grid/list choice of its own (Discover's expanded rail).
 * Same storage key as {@link useViewPrefs}, so a scope can be shared or kept to itself.
 */
export function useDensityPref(scope: string) {
  const densityKey = `${scope}-density`

  const [density, setDensityState] = useState<Density>(() =>
    readStored(densityKey, DENSITIES, 'default'),
  )

  const setDensity = (value: Density) => {
    setDensityState(value)
    writeStored(densityKey, value)
  }

  return { density, setDensity, cols: POSTER_COLS_BY_DENSITY[density] }
}

export type DensityPref = ReturnType<typeof useDensityPref>

/** The Compact / Default / Comfortable segmented control on its own. */
export function DensityControl({
  value,
  onChange,
  size = 'sm',
}: {
  value: Density
  onChange: (density: Density) => void
  size?: string
}) {
  const { t } = useLinguiMacro()
  const options = useDensityOptions()
  return (
    <SegmentedControl
      aria-label={t`Density`}
      size={size}
      value={value}
      onChange={(v) => onChange(v as Density)}
      data={options}
    />
  )
}

export type ViewPrefs = ReturnType<typeof useViewPrefs>

/** Grid or list, as one two-segment control. The icons are named by their tooltips and labels. */
export function ViewModeToggle({
  value,
  onChange,
  size = 'sm',
}: {
  value: ViewMode
  onChange: (mode: ViewMode) => void
  size?: string
}) {
  const { t } = useLinguiMacro()
  return (
    <SegmentedControl
      className="view-mode-toggle"
      size={size}
      value={value}
      onChange={(v) => onChange(v as ViewMode)}
      aria-label={t`View`}
      data={[
        {
          value: 'grid',
          label: (
            <Tooltip label={t`Grid view`} withArrow>
              <span className="view-mode-option" aria-label={t`Grid view`}>
                <IconLayoutGrid size={16} />
              </span>
            </Tooltip>
          ),
        },
        {
          value: 'list',
          label: (
            <Tooltip label={t`List view`} withArrow>
              <span className="view-mode-option" aria-label={t`List view`}>
                <IconLayoutList size={16} />
              </span>
            </Tooltip>
          ),
        },
      ]}
    />
  )
}

/** Density as a labelled group inside a menu, the current one ticked. */
export function DensityMenuItems({ value, onChange }: { value: Density; onChange: (density: Density) => void }) {
  const { t } = useLinguiMacro()
  const options = useDensityOptions()
  return (
    <>
      <Menu.Label>{t`Density`}</Menu.Label>
      {options.map((o) => (
        <Menu.Item
          key={o.value}
          onClick={() => onChange(o.value)}
          rightSection={o.value === value ? <IconCheck size={14} /> : undefined}
          aria-current={o.value === value || undefined}
        >
          {o.label}
        </Menu.Item>
      ))}
    </>
  )
}

/**
 * The "..." button that opens a toolbar's overflow menu. Passes the rest of its props through, so
 * it can sit directly inside a Menu.Target (which hands it a ref and the click handler).
 */
export function ToolbarMenuButton({
  size = 'input-sm',
  ...rest
}: { size?: string | number } & ComponentPropsWithRef<'button'>) {
  const { t } = useLinguiMacro()
  return (
    <Tooltip label={t`More options`} withArrow>
      <ActionIcon {...rest} variant="default" size={size} aria-label={t`More options`}>
        <IconDots size={16} />
      </ActionIcon>
    </Tooltip>
  )
}

/** Grid/list toggle plus a menu holding density, the pattern every poster grid shares. */
export function ViewPrefsControls({
  prefs,
  size = 'sm',
}: {
  prefs: ViewPrefs
  size?: string
}) {
  return (
    <>
      <ViewModeToggle size={size} value={prefs.viewMode} onChange={prefs.setViewMode} />
      <Menu position="bottom-end" withinPortal>
        <Menu.Target>
          <ToolbarMenuButton size={`input-${size}`} />
        </Menu.Target>
        <Menu.Dropdown>
          <DensityMenuItems value={prefs.density} onChange={prefs.setDensity} />
        </Menu.Dropdown>
      </Menu>
    </>
  )
}
