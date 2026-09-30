import { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react'
import { MantineProvider } from '@mantine/core'
import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { accents, createAppTheme, grounds } from './theme'

/**
 * User-selectable themes. Each preset pairs an accent palette (drives Mantine's `brand`
 * colour and the CSS `--brand*` variables via `[data-accent]` in theme.css) with a colour
 * scheme. The choice persists in localStorage and is applied before first paint.
 *
 * `label` is a descriptor, not a string: this table is built once when the module loads, so a
 * rendered string here would be stuck in whichever language was active at that moment. Render
 * with `useLabel()`. `id` is persisted in localStorage and must stay exactly as it is.
 */
export interface ThemePreset {
  id: string
  label: MessageDescriptor
  /** Accent palette key in theme.ts `accents`. */
  accent: keyof typeof accents
  /** `system` follows the OS light/dark setting and changes with it live. */
  scheme: 'dark' | 'light' | 'system'
  /** Surface ramp key in theme.ts `grounds`; omitted means the default night ground. */
  ground?: keyof typeof grounds
  /** Swatch shown in the settings picker (the accent's primary shade). Any CSS background. */
  swatch: string
}

export const THEME_PRESETS: ThemePreset[] = [
  { id: 'indigo', label: msg`Indigo`, accent: 'indigo', scheme: 'dark', swatch: '#6d7dff' },
  { id: 'blush', label: msg`Blush`, accent: 'blush', scheme: 'dark', swatch: '#ee7fa4' },
  { id: 'rose', label: msg`Rose`, accent: 'rose', scheme: 'dark', swatch: '#f52069' },
  {
    id: 'nori',
    label: msg`Nori`,
    accent: 'rose',
    scheme: 'dark',
    ground: 'nori',
    swatch: 'linear-gradient(135deg, #f52069 50%, #14201a 50%)',
  },
  {
    id: 'nori-salmon',
    label: msg`Nori and salmon`,
    accent: 'salmon',
    scheme: 'dark',
    ground: 'nori',
    swatch: 'linear-gradient(135deg, #ef4f3d 50%, #14201a 50%)',
  },
  {
    id: 'charcoal',
    label: msg`Charcoal`,
    accent: 'rose',
    scheme: 'dark',
    ground: 'charcoal',
    swatch: 'linear-gradient(135deg, #f52069 50%, #181818 50%)',
  },
  {
    id: 'moss',
    label: msg`Moss`,
    accent: 'rose',
    scheme: 'dark',
    ground: 'moss',
    swatch: 'linear-gradient(135deg, #f52069 50%, #1b1e1b 50%)',
  },
  {
    id: 'charcoal-lit',
    label: msg`Charcoal, lit`,
    accent: 'rose',
    scheme: 'dark',
    ground: 'charcoal-lit',
    swatch: 'linear-gradient(135deg, #f52069 50%, #181818 50%)',
  },
  {
    id: 'tinted',
    label: msg`Tinted black`,
    accent: 'rose',
    scheme: 'dark',
    ground: 'tinted',
    swatch: 'linear-gradient(135deg, #f52069 50%, #21181b 50%)',
  },
  { id: 'emerald', label: msg`Emerald`, accent: 'emerald', scheme: 'dark', swatch: '#1bc97a' },
  { id: 'amber', label: msg`Amber`, accent: 'amber', scheme: 'dark', swatch: '#f0ad14' },
  { id: 'light', label: msg`Light`, accent: 'indigo', scheme: 'light', swatch: '#f4f5fa' },
  {
    id: 'system',
    label: msg`Match system`,
    accent: 'indigo',
    scheme: 'system',
    swatch: 'linear-gradient(135deg, #f4f5fa 50%, #0b0d13 50%)',
  },
]

const STORAGE_KEY = 'maki-theme'
// Nothing stored means the user never picked, so they follow the default. A stored id is kept.
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

const DARK_QUERY = '(prefers-color-scheme: dark)'

/** The OS light/dark setting, kept current so a `system` preset flips along with it. */
function useSystemScheme(): 'dark' | 'light' {
  const [scheme, setScheme] = useState<'dark' | 'light'>(() =>
    window.matchMedia(DARK_QUERY).matches ? 'dark' : 'light',
  )
  useEffect(() => {
    const query = window.matchMedia(DARK_QUERY)
    const onChange = (e: MediaQueryListEvent) => setScheme(e.matches ? 'dark' : 'light')
    query.addEventListener('change', onChange)
    return () => query.removeEventListener('change', onChange)
  }, [])
  return scheme
}

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
  const systemScheme = useSystemScheme()
  const scheme = preset.scheme === 'system' ? systemScheme : preset.scheme

  const setThemeId = useCallback((id: string) => {
    setThemeIdState(id)
    localStorage.setItem(STORAGE_KEY, id)
  }, [])

  // The custom CSS in theme.css reads `[data-accent]` / `[data-theme]` on the root element.
  useEffect(() => {
    const root = document.documentElement
    root.dataset.accent = preset.accent
    root.dataset.ground = preset.ground ?? 'night'
    root.dataset.theme = scheme

    // Keep the browser and OS chrome in step with the choice: Android's address bar, and the
    // status bar of an installed (standalone) window. Read back from `--app-bg` rather than
    // duplicating the hex here, so the two can't drift: a light preset would otherwise leave a
    // near-black bar above a white app. `getComputedStyle` after the attribute write reflects it.
    const bg = getComputedStyle(root).getPropertyValue('--app-bg').trim()
    const meta = document.querySelector('meta[name="theme-color"]')
    if (bg && meta) meta.setAttribute('content', bg)
  }, [preset.accent, preset.ground, scheme])

  const mantineTheme = useMemo(
    () => createAppTheme(accents[preset.accent], scheme, preset.ground ? grounds[preset.ground] : undefined),
    [preset.accent, preset.ground, scheme],
  )
  const value = useMemo(
    () => ({ themeId, setThemeId, presets: THEME_PRESETS }),
    [themeId, setThemeId],
  )

  return (
    <ThemeContext.Provider value={value}>
      <MantineProvider theme={mantineTheme} forceColorScheme={scheme}>
        {children}
      </MantineProvider>
    </ThemeContext.Provider>
  )
}
