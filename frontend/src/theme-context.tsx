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
  { id: 'indigo', label: 'Indigo', accent: 'indigo', scheme: 'dark', swatch: '#6d7dff' },
  { id: 'rose', label: 'Rose', accent: 'rose', scheme: 'dark', swatch: '#f52069' },
  { id: 'emerald', label: 'Emerald', accent: 'emerald', scheme: 'dark', swatch: '#1bc97a' },
  { id: 'amber', label: 'Amber', accent: 'amber', scheme: 'dark', swatch: '#f0ad14' },
  { id: 'light', label: 'Light', accent: 'indigo', scheme: 'light', swatch: '#f4f5fa' },
]

const STORAGE_KEY = 'mangarr-theme'
const DEFAULT_ID = 'indigo'

function presetFor(id: string): ThemePreset {
  return THEME_PRESETS.find((p) => p.id === id) ?? THEME_PRESETS[0]
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
  const [themeId, setThemeIdState] = useState<string>(
    () => localStorage.getItem(STORAGE_KEY) ?? DEFAULT_ID,
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
