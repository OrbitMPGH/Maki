import { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react'
import { MantineProvider } from '@mantine/core'
import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { accentSwatch, accents, createAppTheme, groundRamp, grounds } from './theme'

/**
 * Appearance is two independent choices. A background is a dark surface ramp, the light theme, or
 * the OS setting; an accent is the brand colour on top. Either can change without the other, and
 * both persist in localStorage.
 *
 * `label` is a descriptor, not a string: these tables are built once when the module loads, so a
 * rendered string here would be stuck in whichever language was active at that moment. Render
 * with `useLabel()`. `id` is persisted in localStorage and must stay exactly as it is.
 */
export interface BackgroundOption {
  id: string
  label: MessageDescriptor
  /** `system` follows the OS light/dark setting and changes with it live. */
  scheme: 'dark' | 'light' | 'system'
  /** Surface ramp key in theme.ts `grounds`, used while the scheme resolves to dark. */
  ground: keyof typeof grounds
  /** Swatch for the picker, given the active accent's hex: tinted surfaces follow the accent. */
  swatch: (accentHex: string) => string
}

export interface AccentOption {
  id: keyof typeof accents
  label: MessageDescriptor
  /** The accent's primary shade, also the hex that background swatches derive from. */
  swatch: string
}

const tintedSwatch = (accentHex: string) => `color-mix(in srgb, #1b1b1b 96%, ${accentHex})`

export const BACKGROUNDS: BackgroundOption[] = [
  { id: 'tinted', label: msg`Tinted black`, scheme: 'dark', ground: 'tinted', swatch: tintedSwatch },
  { id: 'night', label: msg`Night`, scheme: 'dark', ground: 'night', swatch: () => '#161922' },
  { id: 'charcoal', label: msg`Charcoal`, scheme: 'dark', ground: 'charcoal', swatch: () => '#181818' },
  { id: 'moss', label: msg`Moss`, scheme: 'dark', ground: 'moss', swatch: () => '#1b1e1b' },
  { id: 'light', label: msg`Light`, scheme: 'light', ground: 'tinted', swatch: () => '#f4f5fa' },
  {
    id: 'system',
    label: msg`Match system`,
    scheme: 'system',
    ground: 'tinted',
    swatch: (accentHex) => `linear-gradient(135deg, #f4f5fa 50%, ${tintedSwatch(accentHex)} 50%)`,
  },
]

export const ACCENTS: AccentOption[] = [
  { id: 'indigo', label: msg`Indigo`, swatch: accentSwatch('indigo') },
  { id: 'rose', label: msg`Rose`, swatch: accentSwatch('rose') },
  { id: 'blush', label: msg`Blush`, swatch: accentSwatch('blush') },
  { id: 'emerald', label: msg`Emerald`, swatch: accentSwatch('emerald') },
  { id: 'amber', label: msg`Amber`, swatch: accentSwatch('amber') },
]

const BACKGROUND_KEY = 'maki-background'
const ACCENT_KEY = 'maki-accent'
/** Pre-split storage: one preset id that bundled scheme and accent. Read once, then removed. */
const LEGACY_KEY = 'maki-theme'

// Nothing stored means the user never picked, so they follow the defaults. Stored ids are kept.
const DEFAULT_BACKGROUND = 'tinted'
const DEFAULT_ACCENT: AccentOption['id'] = 'indigo'

// A returning user keeps exactly what they had: the old presets all sat on the night ground.
const LEGACY_PRESETS: Record<string, [background: string, accent: AccentOption['id']]> = {
  indigo: ['night', 'indigo'],
  rose: ['night', 'rose'],
  blush: ['night', 'blush'],
  emerald: ['night', 'emerald'],
  amber: ['night', 'amber'],
  light: ['light', 'indigo'],
  system: ['system', 'indigo'],
}

function readStored(): { background: string; accent: AccentOption['id'] } {
  const legacy = localStorage.getItem(LEGACY_KEY)
  if (legacy !== null) {
    localStorage.removeItem(LEGACY_KEY)
    const mapped = LEGACY_PRESETS[legacy]
    if (mapped) {
      localStorage.setItem(BACKGROUND_KEY, mapped[0])
      localStorage.setItem(ACCENT_KEY, mapped[1])
    }
  }
  return {
    background: localStorage.getItem(BACKGROUND_KEY) ?? DEFAULT_BACKGROUND,
    accent: (localStorage.getItem(ACCENT_KEY) as AccentOption['id'] | null) ?? DEFAULT_ACCENT,
  }
}

function backgroundFor(id: string): BackgroundOption {
  return BACKGROUNDS.find((b) => b.id === id) ?? BACKGROUNDS[0]
}

function accentFor(id: string): AccentOption {
  return ACCENTS.find((a) => a.id === id) ?? ACCENTS[0]
}

interface ThemeContextValue {
  background: string
  setBackground: (id: string) => void
  accent: AccentOption['id']
  setAccent: (id: AccentOption['id']) => void
  backgrounds: BackgroundOption[]
  accents: AccentOption[]
}

const ThemeContext = createContext<ThemeContextValue | null>(null)

const DARK_QUERY = '(prefers-color-scheme: dark)'

/** The OS light/dark setting, kept current so a `system` background flips along with it. */
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

/** Wraps MantineProvider, swapping the accent palette, surface ramp and colour scheme to match. */
export function AppThemeProvider({ children }: { children: React.ReactNode }) {
  const [stored] = useState(readStored)
  const [backgroundId, setBackgroundState] = useState(stored.background)
  const [accentId, setAccentState] = useState(stored.accent)
  const background = backgroundFor(backgroundId)
  const accent = accentFor(accentId)
  const systemScheme = useSystemScheme()
  const scheme = background.scheme === 'system' ? systemScheme : background.scheme

  const setBackground = useCallback((id: string) => {
    setBackgroundState(id)
    localStorage.setItem(BACKGROUND_KEY, id)
  }, [])
  const setAccent = useCallback((id: AccentOption['id']) => {
    setAccentState(id)
    localStorage.setItem(ACCENT_KEY, id)
  }, [])

  // The custom CSS in theme.css reads `[data-accent]`, `[data-ground]` and `[data-theme]` on the
  // root element.
  useEffect(() => {
    const root = document.documentElement
    root.dataset.accent = accent.id
    root.dataset.ground = background.ground
    root.dataset.theme = scheme

    // Keep the browser and OS chrome in step with the choice: Android's address bar, and the
    // status bar of an installed (standalone) window. Read back from `--app-bg` rather than
    // duplicating the hex here, so the two can't drift: a light preset would otherwise leave a
    // near-black bar above a white app. `getComputedStyle` after the attribute write reflects it.
    const bg = getComputedStyle(root).getPropertyValue('--app-bg').trim()
    const meta = document.querySelector('meta[name="theme-color"]')
    if (bg && meta) meta.setAttribute('content', bg)
  }, [accent.id, background.ground, scheme])

  const mantineTheme = useMemo(
    () => createAppTheme(accents[accent.id], scheme, groundRamp(background.ground, accents[accent.id])),
    [accent.id, background.ground, scheme],
  )
  const value = useMemo(
    () => ({
      background: background.id,
      setBackground,
      accent: accent.id,
      setAccent,
      backgrounds: BACKGROUNDS,
      accents: ACCENTS,
    }),
    [background.id, accent.id, setBackground, setAccent],
  )

  return (
    <ThemeContext.Provider value={value}>
      <MantineProvider theme={mantineTheme} forceColorScheme={scheme}>
        {children}
      </MantineProvider>
    </ThemeContext.Provider>
  )
}
