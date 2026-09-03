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
 * Content-first dark UI for a self-hosted collection manager. The dark scale is
 * overridden to a cohesive near-black elevation ramp so every Mantine surface picks
 * up the look for free, and the six semantic palettes below are overridden so a
 * `<Badge color="teal">` lands on the token that means "on disk" rather than on
 * Mantine's stock teal. The full rules live in .claude/rules/design-system.md.
 *
 * Shade 5 of every accent is the accent's own hex, because `primaryShade.dark` is 5:
 * that keeps Mantine's filled colour and the CSS `--brand` token identical.
 */

const crimson: MantineColorsTuple = [
  '#fceeed', '#f6d4d2', '#eaa7a2', '#de7c74', '#cc5148',
  '#b3302a', '#9b2822', '#80211c', '#661a16', '#4d1310',
]

const rose: MantineColorsTuple = [
  '#fceef2', '#f7d3de', '#eda5bb', '#e07a99', '#cc5077',
  '#b02f56', '#98274a', '#7e203d', '#641930', '#4b1324',
]

const plum: MantineColorsTuple = [
  '#f9eefa', '#f0d6f1', '#dfabe1', '#cd82cf', '#b25ab4',
  '#8b3f8e', '#78357a', '#632c65', '#4e2350', '#3a1a3b',
]

/** Iris. Keyed `indigo` because that id is what sits in the `maki-theme` localStorage key. */
const iris: MantineColorsTuple = [
  '#eeeefc', '#d8d7f7', '#b3b1ee', '#8f8ce4', '#6f6cd9',
  '#5a56cf', '#4b47b3', '#3d3a94', '#2f2d74', '#232156',
]

const cobalt: MantineColorsTuple = [
  '#eaf2fc', '#cfe0f6', '#a1c2ec', '#71a2e0', '#4884cd',
  '#2c6ab5', '#255a9a', '#1e4a7f', '#183a64', '#122c4b',
]

const teal: MantineColorsTuple = [
  '#e8f7f6', '#c9ecea', '#93d8d3', '#5cc3bd', '#2ca49e',
  '#0f7a75', '#0c6864', '#0a5653', '#084442', '#063331',
]

/**
 * Selectable accent palettes; the CSS-variable side lives in theme.css under [data-accent].
 * `emerald` and `amber` are retired as choices (mint is the "on disk" colour and gold is the
 * warning colour plus the rating stars), but their keys stay so a stored preset still resolves
 * while theme-context migrates it.
 */
export const accents: Record<string, MantineColorsTuple> = {
  crimson,
  rose,
  plum,
  indigo: iris,
  cobalt,
  teal,
  emerald: teal,
  amber: crimson,
}

// Near-black elevation ramp. 7 = app body, 6 = cards, 5 = elevated (modals),
// 4 = borders, 2 = dimmed text, 0 = primary text.
const dark: MantineColorsTuple = [
  '#f2efe9', '#e2dfd8', '#a9a6a0', '#76767d', '#26262a',
  '#16161a', '#141416', '#0c0c0d', '#0a0a0b', '#060607',
]

/**
 * The six semantic slots, mapped onto the Mantine palette names `status.tsx` returns. Shade 4
 * is the token's text colour, which is what the `light` variant reads in dark mode.
 *
 * Every status colour in the app comes from one of these six. Nothing else may: a page file
 * writing `color="grape"` on a Badge is a bug, and the fix is a visual in status.tsx.
 */
const semantic: Record<string, MantineColorsTuple> = {
  // --ok: on disk, linked, read, synced, completed.
  teal: [
    '#e7f9f0', '#c6f0dc', '#9ae5c0', '#74d9a6', '#5fc98c',
    '#48b678', '#3aa066', '#2e8452', '#23673f', '#194b2e',
  ],
  // --warn: needs a decision from the user.
  yellow: [
    '#fdf6e6', '#f9e9c4', '#f3d894', '#ecc665', '#e0a93a',
    '#c8912a', '#a87720', '#875f19', '#674812', '#48320c',
  ],
  // --info: in flight, in progress.
  blue: [
    '#eef2fb', '#d5def5', '#b3c3ec', '#a0b3e6', '#8fa6e0',
    '#6f89d2', '#5a76bd', '#485f9b', '#374a79', '#273558',
  ],
  // --danger: failed, missing from disk, destructive.
  red: [
    '#fdeeed', '#f8d4d1', '#efaba5', '#e79389', '#e08078',
    '#cf5c52', '#b64840', '#963a34', '#752d28', '#54201d',
  ],
  // --watched: watched, not read.
  violet: [
    '#f4eefc', '#e4d7f7', '#cbb4ef', '#bfa4ec', '#b39ae8',
    '#9a78dd', '#8460c6', '#6c4da3', '#543c80', '#3d2b5d',
  ],
  // Neutral: known but inert. Missing, queued, a source name.
  gray: [
    '#f4f4f5', '#e3e3e6', '#c9c9ce', '#b4b4bb', '#a0a0a8',
    '#8a8a93', '#74747d', '#5e5e66', '#48484f', '#333338',
  ],
}

/** Builds the Mantine theme for a given accent palette (defaults to crimson). */
export function createAppTheme(accent: MantineColorsTuple = crimson) {
  return createTheme({ ...themeBase, colors: { ...semantic, brand: accent, dark } })
}

const themeBase: MantineThemeOverride = {
  primaryColor: 'brand',
  primaryShade: { light: 6, dark: 5 },
  colors: { ...semantic, brand: crimson, dark },
  defaultRadius: 'md',
  // Inter ships as a variable woff2 under this exact family name; see the @font-face block in
  // theme.css. The fallbacks are what CJK titles resolve through, since Inter has no CJK.
  fontFamily:
    'Inter, ui-sans-serif, system-ui, -apple-system, "Segoe UI", Roboto, Helvetica, Arial, sans-serif',
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
  // 6 pill, 8 icon action, 9 control, 11 primary button, 14 panel, 15 overlay.
  radius: {
    xs: '5px',
    sm: '6px',
    md: '9px',
    lg: '14px',
    xl: '20px',
  },
  // Shadow is what separates an overlay from the page, so panels get none and `lg` is sized for
  // menus and modals rather than for cards.
  shadows: {
    sm: '0 1px 2px rgba(0,0,0,.4)',
    md: '0 4px 16px -4px rgba(0,0,0,.5)',
    lg: '0 30px 70px -20px rgba(0,0,0,.9)',
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

/** Default (crimson) theme, kept as a named export for any non-dynamic consumers. */
export const theme = createAppTheme()
