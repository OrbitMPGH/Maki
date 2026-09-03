import { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react'
import { MantineProvider } from '@mantine/core'
import { accents, createAppTheme } from './theme'

/**
 * User-selectable themes. Each preset pairs an accent palette (drives Mantine's `brand`
 * colour and the CSS `--brand*` variables via `[data-accent]` in theme.css) with a colour
 * scheme. The choice persists in localStorage and is applied before first paint.
 */
export interface ThemePreset {
  id: string
  label: string
  /** Accent palette key in theme.ts `accents`. */
  accent: keyof typeof accents
  scheme: 'dark' | 'light'
  /** Swatch shown in the settings picker (the accent's primary shade). */
  swatch: string
}

export const THEME_PRESETS: ThemePreset[] = [
  { id: 'crimson', label: 'Crimson', accent: 'crimson', scheme: 'dark', swatch: '#b3302a' },
  { id: 'rose', label: 'Rose', accent: 'rose', scheme: 'dark', swatch: '#b02f56' },
  { id: 'plum', label: 'Plum', accent: 'plum', scheme: 'dark', swatch: '#8b3f8e' },
  { id: 'indigo', label: 'Iris', accent: 'indigo', scheme: 'dark', swatch: '#5a56cf' },
  { id: 'cobalt', label: 'Cobalt', accent: 'cobalt', scheme: 'dark', swatch: '#2c6ab5' },
  { id: 'teal', label: 'Teal', accent: 'teal', scheme: 'dark', swatch: '#0f7a75' },
  { id: 'light', label: 'Light', accent: 'crimson', scheme: 'light', swatch: '#f7f5f2' },
]

const STORAGE_KEY = 'maki-theme'
const DEFAULT_ID = 'crimson'

/**
 * Retired preset ids, pointed at their nearest survivor. Green and amber accents were dropped
 * because mint is the "on disk" colour and gold is the warning colour plus the rating stars, so
 * an accent in either turns a meaningful colour into decoration.
 *
 * Ids are what live in localStorage, so a retired one has to land somewhere deliberate rather
 * than silently falling through to the default: `emerald` was the only cool accent those users
 * chose, and teal is its closest replacement.
 *
 * `indigo` is deliberately absent. It kept its id and only changed value (to iris), so anyone
 * who picked it stays picked.
 */
const RETIRED_IDS: Record<string, string> = { emerald: 'teal', amber: 'crimson' }

function presetFor(id: string): ThemePreset {
  const resolved = RETIRED_IDS[id] ?? id
  return THEME_PRESETS.find((p) => p.id === resolved) ?? THEME_PRESETS[0]
}

interface ThemeContextValue {
  themeId: string
  setThemeId: (id: string) => void
  presets: ThemePreset[]
}

const ThemeContext = createContext<ThemeContextValue | null>(null)

export function useThemeChoice(): ThemeContextValue {
  const ctx = useContext(ThemeContext)
  if (!ctx) throw new Error('useThemeChoice must be used within AppThemeProvider')
  return ctx
}

/** Wraps MantineProvider, swapping the accent palette and colour scheme to match the choice. */
export function AppThemeProvider({ children }: { children: React.ReactNode }) {
  // Normalised through presetFor on read, not just when resolving the palette: the settings
  // picker matches on `themeId`, so a stored retired id left as-is would render with nothing
  // selected while the app was visibly running its replacement.
  const [themeId, setThemeIdState] = useState<string>(
    () => presetFor(localStorage.getItem(STORAGE_KEY) ?? DEFAULT_ID).id,
  )
  const preset = presetFor(themeId)

  const setThemeId = useCallback((id: string) => {
    setThemeIdState(id)
    localStorage.setItem(STORAGE_KEY, id)
  }, [])

  // The custom CSS in theme.css reads `[data-accent]` / `[data-theme]` on the root element.
  useEffect(() => {
    const root = document.documentElement
    root.dataset.accent = preset.accent
    root.dataset.theme = preset.scheme

    // Keep the browser and OS chrome in step with the choice: Android's address bar, and the
    // status bar of an installed (standalone) window. Read back from `--app-bg` rather than
    // duplicating the hex here, so the two can't drift: a light preset would otherwise leave a
    // near-black bar above a white app. `getComputedStyle` after the attribute write reflects it.
    const bg = getComputedStyle(root).getPropertyValue('--app-bg').trim()
    const meta = document.querySelector('meta[name="theme-color"]')
    if (bg && meta) meta.setAttribute('content', bg)
  }, [preset.accent, preset.scheme])

  const mantineTheme = useMemo(() => createAppTheme(accents[preset.accent]), [preset.accent])
  const value = useMemo(
    () => ({ themeId, setThemeId, presets: THEME_PRESETS }),
    [themeId, setThemeId],
  )

  return (
    <ThemeContext.Provider value={value}>
      <MantineProvider theme={mantineTheme} forceColorScheme={preset.scheme}>
        {children}
      </MantineProvider>
    </ThemeContext.Provider>
  )
}
