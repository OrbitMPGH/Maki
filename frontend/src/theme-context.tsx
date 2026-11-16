import { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react'
import { MantineProvider } from '@mantine/core'
import { msg } from '@lingui/core/macro'
import { useLingui } from '@lingui/react'
import type { MessageDescriptor } from '@lingui/core'
import {
  accentSwatch,
  accents,
  createAppTheme,
  cssVariablesResolver,
  groundRamp,
  grounds,
  type ThemeLabels,
} from './theme'

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
  /** Colours for the picker's preview tile, taken from this ground's theme.css block. */
  preview: (accentHex: string) => BackgroundPreview
}

/** CSS backgrounds for a picker tile: page ground, sidebar strip, a card with its border and ink. */
export interface BackgroundPreview {
  ground: string
  side?: string
  sideEdge?: string
  surface: string
  border: string
  ink?: string
  accent: string
}

export interface AccentOption {
  id: keyof typeof accents
  label: MessageDescriptor
  /** The accent's primary shade, also the hex that background previews derive from. */
  swatch: string
}

const mix = (hex: string, pct: number, toward: string) => `color-mix(in srgb, ${hex} ${pct}%, ${toward})`
const between = (a: string, b: string) => `color-mix(in srgb, ${a}, ${b})`

/** A plain ramp, with the sidebar strip halfway between the page and the card. */
function ramp(ground: string, surface: string, border: string, ink: string, accent: string): BackgroundPreview {
  return { ground, side: between(ground, surface), surface, border, ink, accent }
}

const lightAccent = (accentHex: string) => mix(accentHex, 85, '#000000')

export const BACKGROUNDS: BackgroundOption[] = [
  {
    id: 'tinted',
    label: msg`Tinted black`,
    scheme: 'dark',
    ground: 'tinted',
    preview: (a) => ramp(mix('#0b0b0b', 96, a), mix('#1b1b1b', 96, a), mix('#303030', 96, a), '#dee2e6', a),
  },
  {
    id: 'cover',
    label: msg`Cover tint`,
    scheme: 'dark',
    ground: 'cover',
    preview: (a) => ({
      ground: 'linear-gradient(135deg, #2a1a17 0 33%, #161d29 33% 66%, #192417 66%)',
      side: 'rgba(0, 0, 0, 0.25)',
      surface: mix('#1b1b1b', 94, a),
      border: mix('#303030', 93, a),
      ink: '#dee2e6',
      accent: a,
    }),
  },
  {
    id: 'night',
    label: msg`Night`,
    scheme: 'dark',
    ground: 'night',
    preview: (a) => ramp('#0b0d13', '#161922', '#262b36', '#dee2e6', a),
  },
  {
    id: 'charcoal',
    label: msg`Charcoal`,
    scheme: 'dark',
    ground: 'charcoal',
    preview: (a) => ramp('#0f0f0f', '#181818', '#2a2a2a', '#e3e1dc', a),
  },
  {
    id: 'moss',
    label: msg`Moss`,
    scheme: 'dark',
    ground: 'moss',
    preview: (a) => ramp('#0e100e', '#1b1e1b', '#2e332e', '#dee2e6', a),
  },
  {
    id: 'oled',
    label: msg`OLED black`,
    scheme: 'dark',
    ground: 'ink',
    preview: (a) => {
      const border = mix('#2a2a2a', 88, a)
      return { ground: '#000000', side: '#000000', sideEdge: border, surface: '#000000', border, ink: '#dee2e6', accent: a }
    },
  },
  {
    id: 'light',
    label: msg`Light`,
    scheme: 'light',
    ground: 'tinted',
    preview: (a) => ramp('#f3f4fa', '#ffffff', '#e3e6f0', '#252a38', lightAccent(a)),
  },
  {
    id: 'paper',
    label: msg`Paper`,
    scheme: 'light',
    ground: 'paper',
    preview: (a) => ramp(mix('#efe9dd', 96, a), '#fbf8f1', '#e0d8c8', '#2c261f', lightAccent(a)),
  },
  {
    id: 'system',
    label: msg`Match system`,
    scheme: 'system',
    ground: 'tinted',
    preview: (a) => ({
      ground: `linear-gradient(135deg, #f3f4fa 50%, ${mix('#0b0b0b', 96, a)} 50%)`,
      surface: `linear-gradient(135deg, #ffffff 50%, ${mix('#1b1b1b', 96, a)} 50%)`,
      border: '#6b6f7a',
      accent: a,
    }),
  },
]

export const ACCENTS: AccentOption[] = [
  { id: 'blush', label: msg`Blush`, swatch: accentSwatch('blush') },
  { id: 'indigo', label: msg`Indigo`, swatch: accentSwatch('indigo') },
  { id: 'rose', label: msg`Rose`, swatch: accentSwatch('rose') },
  { id: 'emerald', label: msg`Emerald`, swatch: accentSwatch('emerald') },
  { id: 'amber', label: msg`Amber`, swatch: accentSwatch('amber') },
  { id: 'vermilion', label: msg`Vermilion`, swatch: accentSwatch('vermilion') },
  { id: 'teal', label: msg`Teal`, swatch: accentSwatch('teal') },
  { id: 'plum', label: msg`Plum`, swatch: accentSwatch('plum') },
]

const BACKGROUND_KEY = 'maki-background'
const ACCENT_KEY = 'maki-accent'
/** Pre-split storage: one preset id that bundled scheme and accent. Read once, then removed. */
const LEGACY_KEY = 'maki-theme'

// Nothing stored means the user never picked, so they follow the defaults. Stored ids are kept.
export const DEFAULT_BACKGROUND = 'tinted'
export const DEFAULT_ACCENT: AccentOption['id'] = 'blush'

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

// Storage can be blocked or throw (private windows, cleared site data); the theme must still render.
function storageGet(key: string): string | null {
  try {
    return localStorage.getItem(key)
  } catch {
    return null
  }
}

function storageSet(key: string, value: string) {
  try {
    localStorage.setItem(key, value)
  } catch {
    // The choice still applies for this session.
  }
}

function readStored(): { background: string; accent: AccentOption['id'] } {
  const legacy = storageGet(LEGACY_KEY)
  if (legacy !== null) {
    try {
      localStorage.removeItem(LEGACY_KEY)
    } catch {
      // Nothing to clean up if storage is unavailable.
    }
    const mapped = Object.hasOwn(LEGACY_PRESETS, legacy) ? LEGACY_PRESETS[legacy] : undefined
    if (mapped) {
      storageSet(BACKGROUND_KEY, mapped[0])
      storageSet(ACCENT_KEY, mapped[1])
    }
  }
  return {
    background: storageGet(BACKGROUND_KEY) ?? DEFAULT_BACKGROUND,
    accent: (storageGet(ACCENT_KEY) as AccentOption['id'] | null) ?? DEFAULT_ACCENT,
  }
}

/** The resolved `--app-bg` as a colour a `theme-color` meta accepts; the raw property can be a `color-mix()` string. */
function resolveAppBackground(): string | null {
  const probe = document.createElement('div')
  probe.style.cssText = 'display:none;background-color:var(--app-bg)'
  document.body.appendChild(probe)
  const color = getComputedStyle(probe).backgroundColor
  probe.remove()
  if (/^(#|rgb)/.test(color)) return color
  try {
    const ctx = document.createElement('canvas').getContext('2d')
    if (!ctx) return null
    ctx.fillStyle = '#010203'
    ctx.fillStyle = color
    if (ctx.fillStyle === '#010203') return null
    ctx.fillRect(0, 0, 1, 1)
    const [r, g, b] = ctx.getImageData(0, 0, 1, 1).data
    return `rgb(${r}, ${g}, ${b})`
  } catch {
    return null
  }
}

function backgroundFor(id: string): BackgroundOption {
  return BACKGROUNDS.find((b) => b.id === id) ?? BACKGROUNDS[0]
}

function accentFor(id: string): AccentOption {
  return ACCENTS.find((a) => a.id === id) ?? ACCENTS.find((a) => a.id === DEFAULT_ACCENT) ?? ACCENTS[0]
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
    storageSet(BACKGROUND_KEY, id)
  }, [])
  const setAccent = useCallback((id: AccentOption['id']) => {
    setAccentState(id)
    storageSet(ACCENT_KEY, id)
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
    // near-black bar above a white app. Resolved to a real colour because the tinted ground's
    // `--app-bg` is a `color-mix()` string.
    const bg = resolveAppBackground()
    const meta = document.querySelector('meta[name="theme-color"]')
    if (bg && meta) meta.setAttribute('content', bg)
  }, [accent.id, background.ground, scheme])

  const { _ } = useLingui()
  const labels = useMemo<ThemeLabels>(
    () => ({
      close: _(msg`Close`),
      firstPage: _(msg`First page`),
      previousPage: _(msg`Previous page`),
      nextPage: _(msg`Next page`),
      lastPage: _(msg`Last page`),
    }),
    [_],
  )
  const mantineTheme = useMemo(
    () => createAppTheme(accents[accent.id], scheme, groundRamp(background.ground, accents[accent.id]), labels),
    [accent.id, background.ground, scheme, labels],
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
      <MantineProvider theme={mantineTheme} forceColorScheme={scheme} cssVariablesResolver={cssVariablesResolver}>
        {children}
      </MantineProvider>
    </ThemeContext.Provider>
  )
}
