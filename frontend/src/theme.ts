import {
  Badge,
  Button,
  Card,
  type MantineColorsTuple,
  type MantineThemeOverride,
  Modal,
  Paper,
  Table,
  createTheme,
  defaultVariantColorsResolver,
} from '@mantine/core'

/**
 * Maki design system.
 *
 * Content-first dark UI for a self-hosted collection manager. The dark scale
 * stays quiet so cover art and reading progress carry the visual weight;
 * `brand` is the single interface accent. Semantic status hues live in
 * ./status.ts.
 */

const brand: MantineColorsTuple = [
  '#f2effb',
  '#e6def6',
  '#d6caff',
  '#c4b4f2',
  '#b4a2ec',
  '#a693e6',
  '#6652ad',
  '#554297',
  '#44357b',
  '#352961',
]

const rose: MantineColorsTuple = [
  '#ffe9f0',
  '#ffd0de',
  '#ff9fbd',
  '#ff6a99',
  '#ff3d7c',
  '#ed6b8b',
  '#b72f56',
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
  '#55c995',
  '#237e58',
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
  '#dda94f',
  '#8a601c',
  '#a5730a',
  '#7f590c',
  '#674709',
]

/** Selectable accent palettes; the CSS-variable side lives in theme.css under [data-accent]. */
export const accents: Record<string, MantineColorsTuple> = { indigo: brand, rose, emerald, amber }

// Near-black elevation ramp. 7 = app body, 6 = cards, 5 = elevated (modals),
// 4 = borders, 2 = dimmed text, 0 = primary text.
const dark: MantineColorsTuple = [
  '#ded8d0',
  '#b8b0a7',
  '#a29990',
  '#615a53',
  '#3a3530',
  '#292521',
  '#1e1b18',
  '#151311',
  '#0e0d0c',
  '#090908',
]

/** Builds the Mantine theme for a given accent palette (defaults to indigo). */
export function createAppTheme(accent: MantineColorsTuple = brand) {
  return createTheme({ ...themeBase, colors: { brand: accent, dark } })
}

const themeBase: MantineThemeOverride = {
  primaryColor: 'brand',
  primaryShade: { light: 6, dark: 5 },
  colors: { brand, dark },
  defaultRadius: 'sm',
  // Apply the custom accent only to brand variants. Semantic actions retain
  // Mantine's red, green and yellow palettes, including disabled states.
  variantColorResolver: (input) => {
    const resolved = defaultVariantColorsResolver(input)
    if ((input.color ?? input.theme.primaryColor) !== 'brand') return resolved
    if (input.variant === 'filled') {
      return { ...resolved, background: 'var(--brand)', hover: 'var(--brand-hover)', color: 'var(--brand-on)' }
    }
    if (input.variant === 'light') {
      return { ...resolved, background: 'color-mix(in srgb, var(--brand) 12%, transparent)', hover: 'color-mix(in srgb, var(--brand) 20%, transparent)', color: 'var(--brand-fg)' }
    }
    return resolved
  },
  fontFamily:
    'ui-sans-serif, -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif',
  fontFamilyMonospace:
    'ui-monospace, "JetBrains Mono", "SFMono-Regular", "Cascadia Code", Menlo, monospace',
  headings: {
    fontFamily: 'var(--font-display)',
    fontWeight: '700',
    sizes: {
      h1: { fontSize: '2rem', lineHeight: '1.08', fontWeight: '800' },
      h2: { fontSize: '1.55rem', lineHeight: '1.16', fontWeight: '750' },
      h3: { fontSize: '1.2rem', lineHeight: '1.24' },
      h4: { fontSize: '1rem', lineHeight: '1.4' },
    },
  },
  radius: {
    xs: '4px',
    sm: '5px',
    md: '7px',
    lg: '9px',
    xl: '14px',
  },
  shadows: {
    sm: '0 1px 2px rgba(0,0,0,.32)',
    md: '0 8px 22px -10px rgba(0,0,0,.55)',
    lg: '0 18px 42px -18px rgba(0,0,0,.7)',
  },
  cursorType: 'pointer',
  components: {
    Card: Card.extend({
      defaultProps: { radius: 'md', withBorder: true },
    }),
    Paper: Paper.extend({
      defaultProps: { radius: 'md' },
    }),
    Button: Button.extend({
      defaultProps: { radius: 'sm' },
    }),
    Badge: Badge.extend({
      defaultProps: { radius: 'xs', fw: 600 },
    }),
    Modal: Modal.extend({
      defaultProps: { radius: 'md', centered: true, overlayProps: { blur: 3, backgroundOpacity: 0.55 } },
    }),
    Table: Table.extend({
      defaultProps: { verticalSpacing: 'sm', horizontalSpacing: 'md' },
    }),
  },
}

/** Default (indigo) theme, kept as a named export for any non-dynamic consumers. */
export const theme = createAppTheme()
