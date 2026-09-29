import {
  Badge,
  Button,
  Card,
  type MantineColorScheme,
  type MantineColorsTuple,
  type MantineThemeOverride,
  Modal,
  Paper,
  Table,
  type VariantColorsResolver,
  createTheme,
  defaultVariantColorsResolver,
} from '@mantine/core'
import type { ReactNode } from 'react'

const ModalPassthrough = ({ children }: { children?: ReactNode }) => children

/**
 * Maki design system.
 *
 * Content-first, cinematic dark UI for a self-hosted collection manager. The
 * dark scale is overridden to a cohesive near-black elevation ramp so every
 * Mantine surface picks up the look for free; `brand` (blush by default) is
 * the single accent. Semantic status hues live in ./status.ts.
 */

const brand: MantineColorsTuple = [
  '#eef1ff',
  '#dde2ff',
  '#b8c1ff',
  '#8f9cff',
  '#6d7dff',
  '#5566f5',
  '#4553e6',
  '#3742c4',
  '#2d38a0',
  '#232c80',
]

const blush: MantineColorsTuple = [
  '#fff0f4',
  '#ffe0e9',
  '#f9c3d3',
  '#f2a2bb',
  '#eb90ad',
  '#e2768f',
  '#c4476e',
  '#b8436a',
  '#953556',
  '#752a45',
]

const rose: MantineColorsTuple = [
  '#ffe9f0',
  '#ffd0de',
  '#ff9fbd',
  '#ff6a99',
  '#ff3d7c',
  '#f52069',
  '#e11060',
  '#be0a52',
  '#970c45',
  '#7a0f3b',
]

const emerald: MantineColorsTuple = [
  '#e6fcf1',
  '#c9f7e0',
  '#96efc4',
  '#5fe6a6',
  '#33dd8d',
  '#1bc97a',
  '#0fb46c',
  '#08935a',
  '#0a7449',
  '#0a5d3c',
]

const amber: MantineColorsTuple = [
  '#fff8e1',
  '#ffecb3',
  '#ffdf85',
  '#ffd257',
  '#ffc531',
  '#f0ad14',
  '#d1930a',
  '#a5730a',
  '#7f590c',
  '#674709',
]

/** Selectable accent palettes; the CSS-variable side lives in theme.css under [data-accent]. */
export const accents: Record<string, MantineColorsTuple> = { blush, indigo: brand, rose, emerald, amber }

// Warm near-black elevation ramp. 7 = app body, 6 = cards, 5 = elevated (modals),
// 4 = borders, 2 = dimmed text, 0 = primary text.
const dark: MantineColorsTuple = [
  '#c4bfb3',
  '#a8a398',
  '#8c877d',
  '#5c5a52',
  '#2a2f2a',
  '#1f231f',
  '#181b18',
  '#0e100e',
  '#0a0c0a',
  '#060706',
]

// Rose's shade 5 only reaches 4:1 under white text; one shade down clears 4.5:1.
// Blush is pale in dark, so it fills at shade 3 with dark text; light takes shade 6 with white.
const primaryShades = new Map<MantineColorsTuple, { light: number; dark: number }>([
  [rose, { light: 6, dark: 6 }],
  [blush, { light: 6, dark: 3 }],
])

/**
 * Text on a brand fill comes from `--brand-on` (theme.css), which theme.css already sets per accent
 * and per scheme. Mantine's own resolver judges lightness from the light-scheme shade whatever
 * scheme is active, so blush's pale dark fill would otherwise get white labels.
 */
const variantColorResolver: VariantColorsResolver = (input) => {
  const resolved = defaultVariantColorsResolver(input)
  const isBrand = !input.color || input.color === 'brand' || input.color === input.theme.primaryColor
  if (input.variant === 'filled' && isBrand) return { ...resolved, color: 'var(--brand-on)' }
  return resolved
}

/**
 * Builds the Mantine theme for a given accent palette (defaults to blush) and the active scheme.
 * The primary shade is pinned to a single number for that scheme: Mantine reads the light shade
 * when it picks contrast colours for checkboxes, radios and pagination, so a per-scheme object
 * would judge the dark fill by the light one.
 */
export function createAppTheme(accent: MantineColorsTuple = blush, scheme: MantineColorScheme = 'dark') {
  const shades = primaryShades.get(accent) ?? (themeBase.primaryShade as { light: number; dark: number })
  const primaryShade = (scheme === 'light' ? shades.light : shades.dark) as 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9
  return createTheme({ ...themeBase, primaryShade, variantColorResolver, colors: { brand: accent, dark } })
}

const themeBase: MantineThemeOverride = {
  primaryColor: 'brand',
  primaryShade: { light: 6, dark: 5 },
  // Emerald and amber fills are too light for white labels; this flips them to black.
  autoContrast: true,
  colors: { brand, dark },
  defaultRadius: 'md',
  fontFamily:
    '"Inter Variable", InterVariable, Inter, ui-sans-serif, -apple-system, "Segoe UI", Roboto, Helvetica, Arial, sans-serif',
  fontFamilyMonospace:
    'ui-monospace, "JetBrains Mono", "SFMono-Regular", "Cascadia Code", Menlo, monospace',
  headings: {
    fontWeight: '700',
    sizes: {
      h1: { fontSize: '1.9rem', lineHeight: '1.2', fontWeight: '800' },
      h2: { fontSize: '1.5rem', lineHeight: '1.25', fontWeight: '800' },
      h3: { fontSize: '1.2rem', lineHeight: '1.3' },
      h4: { fontSize: '1rem', lineHeight: '1.4' },
    },
  },
  radius: {
    xs: '4px',
    sm: '6px',
    md: '9px',
    lg: '13px',
    xl: '20px',
  },
  shadows: {
    sm: '0 1px 2px rgba(0,0,0,.4)',
    md: '0 4px 16px -4px rgba(0,0,0,.5)',
    lg: '0 12px 40px -8px rgba(0,0,0,.6)',
  },
  cursorType: 'pointer',
  components: {
    Card: Card.extend({
      defaultProps: { radius: 'lg', withBorder: true },
    }),
    Paper: Paper.extend({
      defaultProps: { radius: 'lg' },
    }),
    Button: Button.extend({
      defaultProps: { radius: 'md' },
    }),
    Badge: Badge.extend({
      defaultProps: { radius: 'sm', fw: 600 },
    }),
    /**
     * The utility tier: every ordinary dialog gets the raised card, the sectioned header over a
     * hairline and a body that scrolls under it, without touching the call site. The immersive
     * Discover modal opts out by passing `padding={0} title={null} withCloseButton={false}` (no
     * header renders at all) and its own content styles.
     *
     * No scroll wrapper around header and body: Mantine's default wraps both, so the scrollbar ran
     * past the title and the content box could overflow on top of it. The content is a flex column
     * instead and only `.utility-modal-body` scrolls (theme.css).
     */
    Modal: Modal.extend({
      defaultProps: {
        radius: 'lg',
        padding: 'lg',
        centered: true,
        scrollAreaComponent: ModalPassthrough,
        overlayProps: { blur: 3, backgroundOpacity: 0.55 },
        classNames: {
          content: 'utility-modal-content',
          header: 'utility-modal-header',
          title: 'utility-modal-title',
          close: 'utility-modal-close',
          body: 'utility-modal-body',
        },
      },
    }),
    Table: Table.extend({
      defaultProps: { verticalSpacing: 'sm', horizontalSpacing: 'md' },
    }),
  },
}

/** Default (blush) theme, kept as a named export for any non-dynamic consumers. */
export const theme = createAppTheme()
