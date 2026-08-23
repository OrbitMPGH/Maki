import { useState } from 'react'
import { Button, SegmentedControl } from '@mantine/core'
import { IconLayoutGrid, IconLayoutList } from '@tabler/icons-react'

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

export const DENSITY_OPTIONS = [
  { value: 'compact', label: 'Compact' },
  { value: 'default', label: 'Default' },
  { value: 'comfortable', label: 'Comfortable' },
]

/** Poster columns per breakpoint at each density. */
export const POSTER_COLS_BY_DENSITY: Record<Density, Record<string, number>> = {
  compact: { base: 3, xs: 4, sm: 5, md: 6, xl: 8 },
  default: { base: 2, xs: 3, sm: 4, md: 5, xl: 6 },
  comfortable: { base: 2, xs: 2, sm: 3, md: 4, xl: 5 },
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
  const densityKey = `${scope}-density`

  const [viewMode, setView] = useState<ViewMode>(() => readStored(viewKey, VIEW_MODES, 'grid'))
  const [density, setDensityState] = useState<Density>(() =>
    readStored(densityKey, DENSITIES, 'default'),
  )

  const setViewMode = (mode: ViewMode) => {
    setView(mode)
    writeStored(viewKey, mode)
  }

  const setDensity = (value: Density) => {
    setDensityState(value)
    writeStored(densityKey, value)
  }

  return { viewMode, setViewMode, density, setDensity, cols: POSTER_COLS_BY_DENSITY[density] }
}

export type ViewPrefs = ReturnType<typeof useViewPrefs>

/** The grid/list buttons and the density segmented control. Layout only; it owns no state. */
export function ViewPrefsControls({
  prefs,
  size = 'sm',
}: {
  prefs: ViewPrefs
  size?: string
}) {
  return (
    <>
      <Button.Group>
        <Button
          variant={prefs.viewMode === 'grid' ? 'filled' : 'default'}
          size={size}
          onClick={() => prefs.setViewMode('grid')}
          aria-label="Grid view"
        >
          <IconLayoutGrid size={16} />
        </Button>
        <Button
          variant={prefs.viewMode === 'list' ? 'filled' : 'default'}
          size={size}
          onClick={() => prefs.setViewMode('list')}
          aria-label="List view"
        >
          <IconLayoutList size={16} />
        </Button>
      </Button.Group>
      <SegmentedControl
        size={size}
        value={prefs.density}
        onChange={(v) => prefs.setDensity(v as Density)}
        data={DENSITY_OPTIONS}
      />
    </>
  )
}
