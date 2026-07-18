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
} from '@mantine/core'

/**
 * Maki design system.
 *
 * Content-first, cinematic dark UI for a self-hosted collection manager. The
 * dark scale is overridden to a cohesive near-black elevation ramp so every
 * Mantine surface picks up the look for free; `brand` (indigo/periwinkle) is
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
export const accents: Record<string, MantineColorsTuple> = { indigo: brand, rose, emerald, amber }

// Near-black elevation ramp. 7 = app body, 6 = cards, 5 = elevated (modals),
// 4 = borders, 2 = dimmed text, 0 = primary text.
const dark: MantineColorsTuple = [
  '#c7cad4',
  '#a9adba',
  '#8b90a0',
  '#5d6373',
  '#2b303b',
  '#1f232d',
  '#161922',
  '#0f121a',
  '#0a0c12',
  '#06070b',
]

/** Builds the Mantine theme for a given accent palette (defaults to indigo). */
export function createAppTheme(accent: MantineColorsTuple = brand) {
  return createTheme({ ...themeBase, colors: { brand: accent, dark } })
}

const themeBase: MantineThemeOverride = {
  primaryColor: 'brand',
  primaryShade: { light: 6, dark: 5 },
  colors: { brand, dark },
  defaultRadius: 'md',
  fontFamily:
    'InterVariable, Inter, ui-sans-serif, -apple-system, "Segoe UI", Roboto, Helvetica, Arial, sans-serif',
  fontFamilyMonospace:
    'ui-monospace, "JetBrains Mono", "SFMono-Regular", "Cascadia Code", Menlo, monospace',
  headings: {
    fontWeight: '700',
    sizes: {
      h1: { fontSize: '1.9rem', lineHeight: '1.2', fontWeight: '800' },
      h2: { fontSize: '1.5rem', lineHeight: '1.25', fontWeight: '750' },
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
    Modal: Modal.extend({
      defaultProps: { radius: 'lg', centered: true, overlayProps: { blur: 3, backgroundOpacity: 0.55 } },
    }),
    Table: Table.extend({
      defaultProps: { verticalSpacing: 'sm', horizontalSpacing: 'md' },
    }),
  },
}

/** Default (indigo) theme, kept as a named export for any non-dynamic consumers. */
export const theme = createAppTheme()
