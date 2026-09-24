#!/usr/bin/env node
/**
 * Builds the design system's files from the code, so the tokens and component styles it shows can
 * never drift from what the app ships.
 *
 * Values come from the code: every CSS custom property in `frontend/src/theme.css` (per theme, with
 * the light and accent overrides applied), the Mantine palettes, radii, shadows, headings and primary
 * shade in `theme.ts`, and the theme list in `theme-context.tsx`. What the code cannot say lives in
 * `design-system/`: a usage note per token and the type styles (`tokens.notes.json`), the brand book
 * and component guidelines, the static previews, and `preview.css`, which stands in for the parts
 * Mantine draws at runtime.
 *
 * `components/bundle.css` is `preview.css` followed by the rules theme.css has for the components the
 * previews show, with Mantine's runtime variables mapped onto tokens.
 *
 * The result lands in `design-system/out/project/`, laid out as the design system artifact expects.
 * It warns about any token without a usage note, any note whose token is gone, and any variable the
 * bundle uses that nothing defines.
 *
 * The frontend changes daily and the design system does not need to follow each change, so the build
 * also says what differs from the last publish, recorded in `design-system/published.json`: which
 * tokens changed, were added or went, and which files would need sending. Nothing listed, nothing to
 * publish. After a publish, `--mark-published` records the build as the new baseline.
 *
 * Usage: node scripts/design-system/build.mjs [--mark-published]   (or `npm run ds:build` in frontend/)
 */

import { execFileSync } from 'node:child_process'
import { createHash } from 'node:crypto'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..')
const SRC = path.join(ROOT, 'frontend/src')
const DS = path.join(ROOT, 'design-system')
const OUT = path.join(DS, 'out/project')

const warnings = []
const warn = (msg) => warnings.push(msg)
const read = (p) => fs.readFileSync(p, 'utf8')

const themeCss = read(path.join(SRC, 'theme.css'))
const themeTs = read(path.join(SRC, 'theme.ts'))
const themeContext = read(path.join(SRC, 'theme-context.tsx'))
const notes = JSON.parse(read(path.join(DS, 'tokens.notes.json')))

// --- CSS parsing ------------------------------------------------------------------------------

const stripComments = (s) => s.replace(/\/\*[\s\S]*?\*\//g, '')

/** Top-level `{ ... }` blocks as [prelude, body], nested blocks kept whole inside the body. */
function blocks(css) {
  const out = []
  let depth = 0
  let start = 0
  let open = -1
  for (let i = 0; i < css.length; i++) {
    if (css[i] === '{') {
      if (depth === 0) open = i
      depth++
    } else if (css[i] === '}') {
      depth--
      if (depth === 0) {
        out.push([css.slice(start, open).trim(), css.slice(open + 1, i)])
        start = i + 1
      }
    }
  }
  return out
}

/** `--name: value;` declarations of a block body, in order. Values may span lines. */
function customProps(body) {
  const out = new Map()
  let depth = 0
  let cur = ''
  for (const ch of body) {
    if (ch === '(') depth++
    if (ch === ')') depth--
    if (ch === ';' && depth === 0) {
      const m = /^\s*--([\w-]+)\s*:\s*([\s\S]+?)\s*$/.exec(cur)
      if (m) out.set(m[1], m[2].replace(/\s+/g, ' '))
      cur = ''
    } else cur += ch
  }
  return out
}

const top = blocks(stripComments(themeCss))
const propsOf = (selector) => {
  const merged = new Map()
  for (const [prelude, body] of top) if (prelude === selector) for (const [k, v] of customProps(body)) merged.set(k, v)
  return merged
}
const rootVars = propsOf(':root')
const lightVars = propsOf(":root[data-theme='light']")

// --- Themes (theme-context.tsx) -----------------------------------------------------------------

const presets = [...themeContext.matchAll(/\{\s*id:\s*'(\w+)',\s*label:\s*msg`([^`]+)`,\s*accent:\s*'(\w+)',\s*scheme:\s*'(\w+)'/g)]
  .map(([, id, label, accent, scheme]) => ({ id, label, accent, scheme }))
  .filter((p) => p.scheme !== 'system')
const themes = presets.map((p) => ({
  id: p.scheme === 'light' ? 'light' : p.accent === 'indigo' ? 'dark' : p.accent,
  name: p.label,
  accent: p.accent,
  scheme: p.scheme,
}))
themes.sort((a, b) => (a.id === 'dark' ? -1 : b.id === 'dark' ? 1 : a.id === 'light' ? -1 : b.id === 'light' ? 1 : 0))
if (!themes.length) throw new Error('No theme presets found in theme-context.tsx')

const varsFor = (theme) => {
  const v = new Map(rootVars)
  const over = theme.scheme === 'light' ? lightVars : theme.accent === 'indigo' ? new Map() : propsOf(`:root[data-accent='${theme.accent}']`)
  for (const [k, val] of over) v.set(k, val)
  return v
}
const themeVars = Object.fromEntries(themes.map((t) => [t.id, varsFor(t)]))

// --- theme.ts -----------------------------------------------------------------------------------

const palettes = Object.fromEntries(
  [...themeTs.matchAll(/const (\w+): MantineColorsTuple = \[([\s\S]*?)\]/g)].map(([, name, body]) => [
    name,
    [...body.matchAll(/'(#[0-9a-fA-F]{6})'/g)].map((m) => m[1].toLowerCase()),
  ]),
)
const accentsBody = /export const accents[^=]*=\s*\{([^}]*)\}/.exec(themeTs)?.[1] ?? ''
const accentPalette = Object.fromEntries(
  accentsBody.split(',').map((s) => s.trim()).filter(Boolean).map((s) => {
    const [k, v] = s.split(':').map((x) => x.trim())
    return [k, v ?? k]
  }),
)
const baseShade = /primaryShade:\s*\{\s*light:\s*(\d+),\s*dark:\s*(\d+)\s*\}/.exec(themeTs)
const shadeOverride = /accent === (\w+) \? \(\{\s*light:\s*(\d+),\s*dark:\s*(\d+)\s*\}/.exec(themeTs)
const autoContrast = /autoContrast:\s*true/.test(themeTs)
const luminanceThreshold = Number(/luminanceThreshold:\s*([\d.]+)/.exec(themeTs)?.[1] ?? 0.3)
const tsString = (key) => /* the value is single-quoted in theme.ts */ new RegExp(`${key}:\\s*'([^']+)'`).exec(themeTs)?.[1]
const tsBlock = (key) => new RegExp(`${key}:\\s*\\{([^}]*)\\}`).exec(themeTs)?.[1] ?? ''
const tsPairs = (body) => Object.fromEntries([...body.matchAll(/(\w+):\s*'([^']+)'/g)].map((m) => [m[1], m[2]]))
const tsRadius = tsPairs(tsBlock('radius'))
const tsShadows = tsPairs(tsBlock('shadows'))
const headingWeight = /headings:\s*\{\s*fontWeight:\s*'(\d+)'/.exec(themeTs)?.[1] ?? '700'
const headings = Object.fromEntries(
  [...themeTs.matchAll(/(h[1-6]):\s*\{\s*fontSize:\s*'([^']+)',\s*lineHeight:\s*'([^']+)'(?:,\s*fontWeight:\s*'(\d+)')?\s*\}/g)].map(
    ([, h, fontSize, lineHeight, fontWeight]) => [h, { fontSize, lineHeight: Number(lineHeight), fontWeight: Number(fontWeight ?? headingWeight) }],
  ),
)

// --- Colour helpers -----------------------------------------------------------------------------

const rgbOf = (hex) => {
  let h = hex.slice(1)
  if (h.length === 3) h = [...h].map((c) => c + c).join('')
  const n = parseInt(h.slice(0, 6), 16)
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255]
}
const luminance = (hex) =>
  rgbOf(hex)
    .map((c) => c / 255)
    .map((c) => (c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4))
    .reduce((a, c, i) => a + c * [0.2126, 0.7152, 0.0722][i], 0)

const isColourValue = (v) => /^(#[0-9a-fA-F]{3,8}\b|rgba?\(|hsla?\(|color-mix\(|var\(--)/.test(v)

/** A colour variable's value in one theme, as the design system writes it, or null. */
function colourIn(name, vars) {
  const v = vars.get(name)
  if (v == null) return null
  let m
  if ((m = /^var\(--([\w-]+)\)$/.exec(v))) return `{${m[1]}}`
  if ((m = /^color-mix\(in srgb, var\(--([\w-]+)\) (\d+(?:\.\d+)?)%, transparent\)$/.exec(v))) {
    let target = vars.get(m[1])
    while (target && /^var\(--/.test(target)) target = vars.get(/^var\(--([\w-]+)\)$/.exec(target)[1])
    if (!target || !target.startsWith('#')) return null
    const [r, g, b] = rgbOf(target)
    return `rgba(${r}, ${g}, ${b}, ${Number(m[2]) / 100})`
  }
  if (/^#[0-9a-fA-F]{3,8}$/.test(v)) return v.toLowerCase()
  if (/^rgba?\([\d\s.,]+\)$/.test(v)) return v.replace(/\s*,\s*/g, ', ')
  return null
}

/** Collapses per-theme values: one string when every theme agrees, else dark plus the differences. */
function perTheme(valueOf) {
  const vals = Object.fromEntries(themes.map((t) => [t.id, valueOf(t)]))
  const first = vals[themes[0].id]
  if (themes.every((t) => vals[t.id] === first)) return first
  const out = { [themes[0].id]: first }
  for (const t of themes.slice(1)) if (vals[t.id] !== first) out[t.id] = vals[t.id]
  return out
}

// --- Tokens -------------------------------------------------------------------------------------

const used = new Set()
const usageOf = (name) => {
  used.add(name)
  const u = notes.usage[name]
  if (!u) warn(`no usage note for token "${name}" (add it to design-system/tokens.notes.json)`)
  return u ?? ''
}
const token = (name, value) => ({ name, value, usage: usageOf(name) })

const SKIP = /^(mantine-|reader-|font-|ease$|dur-)/
const FAMILY = [
  ['fontSize', /^type-/],
  ['fontWeight', /^fw-/],
  ['radius', /^radius-/],
  ['shadow', /^shadow-/],
  ['layout', /^(content-|section-space$|control-height-)/],
]
const families = { fontSize: [], fontWeight: [], radius: [], shadow: [], layout: [] }
const colours = []

for (const [name, value] of rootVars) {
  if (SKIP.test(name)) continue
  const fam = FAMILY.find(([, re]) => re.test(name))?.[0]
  if (fam === 'shadow') families.shadow.push(token(name, perTheme((t) => themeVars[t.id].get(name))))
  else if (fam) families[fam].push(token(name, value))
  else if (isColourValue(value)) {
    const v = perTheme((t) => colourIn(name, themeVars[t.id]))
    if (v == null || (typeof v === 'object' && Object.values(v).some((x) => x == null))) warn(`colour "--${name}: ${value}" has no form the design system reads; skipped`)
    else colours.push(token(name, v))
  } else warn(`"--${name}: ${value}" fits no token family; skipped`)
}

// Mantine primary: the brand palette at primaryShade, one shade down for hover, and the label colour
// autoContrast picks for it.
const primaryOf = (t, step = 0) => {
  const pal = accentPalette[t.accent]
  const s = shadeOverride && shadeOverride[1] === pal ? { light: +shadeOverride[2], dark: +shadeOverride[3] } : { light: +baseShade[1], dark: +baseShade[2] }
  return palettes[pal][s[t.scheme === 'light' ? 'light' : 'dark'] + step]
}
const glowAt = colours.findIndex((c) => c.name === 'brand-glow')
colours.splice(
  glowAt + 1,
  0,
  token('primary', perTheme((t) => primaryOf(t))),
  token('primary-hover', perTheme((t) => primaryOf(t, 1))),
  token('primary-on', perTheme((t) => (autoContrast && luminance(primaryOf(t)) > luminanceThreshold ? '#000000' : '#ffffff'))),
)
// Colours no stylesheet declares (the brand mark's fixed inks) carry their own value and note.
for (const c of notes.extraColors) colours.push({ name: c.name, value: c.value, usage: c.usage })
for (const [accent, pal] of [...Object.entries(accentPalette), ['dark', 'dark']]) {
  palettes[pal].forEach((hex, i) => colours.push(token(`${accent}-${i}`, hex)))
}

const radiusPx = (v) => (/^\d+px$/.test(v) ? parseInt(v, 10) : Infinity)
families.radius.push(...Object.entries(tsRadius).map(([k, v]) => token(`radius-${k}`, v)))
families.radius.sort((a, b) => radiusPx(a.value) - radiusPx(b.value))
families.shadow.unshift(...Object.entries(tsShadows).map(([k, v]) => token(`shadow-${k}`, v)))

const lastArg = (v) => /^clamp\((.+)\)$/.exec(v)?.[1].split(',').pop().trim() ?? v
const typeGroups = notes.type.groups.map((g) => ({
  ...g,
  styles: g.styles.map(({ heading, fontSizeVar, ...s }) => {
    if (heading) {
      if (!headings[heading]) warn(`type style "${s.name}" names heading ${heading}, which theme.ts does not define`)
      return { name: s.name, ...headings[heading], ...s }
    }
    if (fontSizeVar) {
      const v = rootVars.get(fontSizeVar)
      if (!v) warn(`type style "${s.name}" reads --${fontSizeVar}, which theme.css does not define`)
      return { name: s.name, fontSize: lastArg(v ?? '1rem'), ...s }
    }
    return s
  }),
}))

for (const name of Object.keys(notes.usage)) if (!used.has(name)) warn(`usage note for "${name}" has no token any more`)

const git = (...args) => execFileSync('git', args, { cwd: ROOT, encoding: 'utf8' }).trim()
const dirty = git('status', '--porcelain', '--', 'frontend/src', 'design-system').split('\n').some((l) => l && !l.includes('design-system/out/'))
const commit = `${git('rev-parse', '--abbrev-ref', 'HEAD')}@${git('rev-parse', '--short=7', 'HEAD')}`
const ref = dirty ? `${commit} plus uncommitted changes` : commit

const tokens = {
  name: 'Maki',
  version: 1,
  meta: { ...notes.meta, ref, synced: new Date().toISOString().slice(0, 10) },
  color: { themes: themes.map(({ id, name }) => ({ id, name })), tokens: colours },
  type: {
    fonts: notes.type.fonts.map(({ source, ...f }) => f),
    families: {
      sans: tsString('fontFamily'),
      display: rootVars.get('font-display').replaceAll("'", '"'),
      mono: tsString('fontFamilyMonospace'),
    },
    groups: typeGroups,
  },
  fontSize: { note: notes.familyNotes.fontSize, tokens: families.fontSize },
  spacing: { note: notes.familyNotes.spacing, tokens: notes.spacing },
  radius: { tokens: families.radius },
  shadow: { note: notes.familyNotes.shadow, tokens: families.shadow },
  layout: { note: notes.familyNotes.layout, tokens: families.layout },
  fontWeight: { note: notes.familyNotes.fontWeight, tokens: families.fontWeight },
}

const allNames = [...colours, ...notes.spacing, ...Object.values(families).flat()].map((t) => t.name)
for (const n of allNames) if (!/^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$/.test(n)) warn(`token name "${n}" is not one the design system accepts`)
for (const n of new Set(allNames.filter((n, i) => allNames.indexOf(n) !== i))) warn(`token name "${n}" appears twice`)

// --- bundle.css ---------------------------------------------------------------------------------

const CLASSES = new RegExp(
  String.raw`(^|[\s,>+~(])\.(` +
    [
      String.raw`brand-mark\b`, 'brand-wordmark', String.raw`panel\b`, 'section-header', 'status-dot', 'empty-state',
      'figure-strip', 'stat-tile', 'stat-accent', String.raw`tag-chips?\b`, 'tag-chip-body', 'tag-dot',
      String.raw`cover-(card|poster|placeholder|scrim|corners?|badge|ring|meta|title|progress-row|bar|count|check)`,
      String.raw`tip\b`, String.raw`discover-(card|card-action|rating|corner|meta|reason|sub|sub-status|rail|rail-item)\b`,
      'engine-', 'series-row', String.raw`row-(check|cover|cover-placeholder|body|header|title|year|description|progress|bar)\b`,
      'surface-frame', 'table-panel', 'panel-table', 'ops-table', 'utility-modal', String.raw`tnum\b`,
    ].join('|') +
    ')',
)
const VAR_MAP = [
  [/var\(--mantine-radius-default\)/g, 'var(--radius-md)'],
  [/--mantine-radius-(xs|sm|md|lg|xl)\b/g, '--radius-$1'],
  [/--mantine-spacing-(xs|sm|md|lg|xl)\b/g, '--spacing-$1'],
  [/--mantine-shadow-(sm|md|lg)\b/g, '--shadow-$1'],
  [/--mantine-color-dimmed\b/g, '--ink-3'],
  [/--mantine-primary-color-filled\b/g, '--primary'],
  [/--mantine-primary-color-contrast\b/g, '--primary-on'],
  [/--mantine-color-brand-filled-hover\b/g, '--primary-hover'],
  [/--mantine-color-brand-filled\b/g, '--primary'],
  [/--mantine-font-family-monospace\b/g, '--font-mono'],
  [/var\(--mantine-color-white\)/g, '#ffffff'],
  [/var\(--mantine-color-black\)/g, '#000000'],
  // Mantine's own grays, which the tooltip copies.
  [/var\(--mantine-color-gray-9\)/g, '#212529'],
  [/var\(--mantine-color-gray-2\)/g, '#e9ecef'],
]
const SCHEME_MAP = [
  [/\[data-mantine-color-scheme='dark'\]/g, "[data-theme]:not([data-theme='light'])"],
  [/\[data-mantine-color-scheme='light'\]/g, "[data-theme='light']"],
  [/\[data-mantine-color-scheme\]/g, '[data-theme]'],
]

const splitSelectors = (s) => {
  const parts = []
  let depth = 0
  let cur = ''
  for (const ch of s) {
    if (ch === '(') depth++
    if (ch === ')') depth--
    if (ch === ',' && depth === 0) {
      parts.push(cur.trim())
      cur = ''
    } else cur += ch
  }
  parts.push(cur.trim())
  return parts
}

function keepRule(prelude, body) {
  if (!CLASSES.test(' ' + prelude)) return null
  let sel = prelude
  for (const [re, to] of SCHEME_MAP) sel = sel.replace(re, to)
  const parts = splitSelectors(sel).filter((p) => !/mantine/i.test(p))
  if (!parts.length) return null
  let decls = body
  for (const [re, to] of VAR_MAP) decls = decls.replace(re, to)
  decls = decls
    .split(/;(?![^(]*\))/)
    .filter((d) => {
      if (!/--mantine-/.test(d)) return true
      warn(`dropped a declaration theme.css only means for Mantine: "${prelude.slice(0, 50)} { ${d.trim()} }"`)
      return false
    })
    .join(';')
  return `${parts.join(',\n')} {${decls}}`
}

const kept = []
const animations = new Set()
for (const [prelude, body] of top) {
  if (prelude.startsWith('@media') || prelude.startsWith('@supports')) {
    const inner = blocks(body).map(([p, b]) => keepRule(p, b)).filter(Boolean)
    if (inner.length) kept.push(`${prelude} {\n${inner.join('\n')}\n}`)
  } else if (!prelude.startsWith('@')) {
    const rule = keepRule(prelude, body)
    if (rule) kept.push(rule)
  }
}
for (const rule of kept) for (const m of rule.matchAll(/animation(?:-name)?:\s*([\w-]+)/g)) animations.add(m[1])
for (const [prelude, body] of top) {
  const m = /^@keyframes\s+([\w-]+)/.exec(prelude)
  if (m && animations.has(m[1])) kept.push(`${prelude} {${body}}`)
}

const motionVars = [...rootVars].filter(([k]) => k === 'ease' || k.startsWith('dur-'))
const bundle = [
  '/* Generated by scripts/design-system/build.mjs from design-system/preview.css and frontend/src/theme.css. Do not edit. */',
  '',
  read(path.join(DS, 'preview.css')).trim(),
  '',
  '/* --- Motion (the design system has no motion token family) ----------------- */',
  `:root {\n${motionVars.map(([k, v]) => `  --${k}: ${v};`).join('\n')}\n}`,
  '',
  '/* --- From frontend/src/theme.css -------------------------------------------- */',
  kept.join('\n\n'),
  '',
].join('\n')

// Every variable the bundle or a preview reads must be a token, a motion variable, or declared locally.
const previewsDir = path.join(DS, 'components')
const previews = fs
  .readdirSync(previewsDir, { withFileTypes: true })
  .filter((d) => d.isDirectory())
  .map((d) => path.join(previewsDir, d.name, 'preview.html'))
  .filter((p) => fs.existsSync(p))
  .map(read)
  .join('\n')
const everything = bundle + previews
const defined = new Set([
  ...allNames,
  ...motionVars.map(([k]) => k),
  ...Object.keys(tokens.type.families).map((k) => `font-${k}`),
  ...[...everything.matchAll(/--([\w-]+)\s*:/g)].map((m) => m[1]),
])
const undefinedVars = new Set(
  [...everything.matchAll(/var\(--([\w-]+)\s*(,)?/g)].filter(([, n, fallback]) => !fallback && !defined.has(n)).map((m) => m[1]),
)
for (const n of undefinedVars) warn(`bundle or previews read --${n}, which nothing defines`)

// --- README -------------------------------------------------------------------------------------

const durNotes = motionVars.filter(([k]) => k.startsWith('dur-'))
for (const [k] of durNotes) if (!notes.motion[`--${k}`]) warn(`no motion note for --${k}`)
const motion = [
  `- One easing curve everywhere, \`--ease\`: \`${rootVars.get('ease')}\` (${notes.motion['--ease']}). The only exception is a bar that follows live progress, which moves linearly.`,
  `- ${['No', 'One', 'Two', 'Three', 'Four', 'Five', 'Six'][durNotes.length] ?? durNotes.length} durations: ${durNotes.map(([k, v]) => `\`--${k}\` ${v} (${notes.motion[`--${k}`] ?? ''})`).join(', ')}. Entrance animations keep their own choreographed timings.`,
].join('\n')
const readme = read(path.join(DS, 'README.md')).replace('{{REF}}', `\`${commit}\`${dirty ? ' plus uncommitted changes' : ''}`).replace('{{MOTION}}', motion)

// --- Write --------------------------------------------------------------------------------------

fs.rmSync(path.join(DS, 'out'), { recursive: true, force: true })
const write = (rel, content) => {
  const p = path.join(OUT, rel)
  fs.mkdirSync(path.dirname(p), { recursive: true })
  fs.writeFileSync(p, content)
}
const copyTree = (from, to) => {
  for (const e of fs.readdirSync(from, { withFileTypes: true })) {
    const src = path.join(from, e.name)
    if (e.isDirectory()) copyTree(src, path.join(to, e.name))
    else write(path.join(to, e.name), fs.readFileSync(src))
  }
}
write('README.md', readme)
write('tokens.json', JSON.stringify(tokens, null, 2) + '\n')
write('components/bundle.css', bundle)
copyTree(path.join(DS, 'components'), 'components')
copyTree(path.join(DS, 'assets'), 'assets')
for (const f of notes.type.fonts) write(f.file, fs.readFileSync(path.join(ROOT, f.source)))

const files = []
const walk = (d) => {
  for (const e of fs.readdirSync(d, { withFileTypes: true })) {
    const p = path.join(d, e.name)
    if (e.isDirectory()) walk(p)
    else files.push(path.relative(path.join(DS, 'out'), p).replaceAll('\\', '/'))
  }
}
walk(OUT)

// --- What changed since the last publish ---------------------------------------------------------

// The commit line and the build date differ on every build, so they are left out of the hashes: a
// rebuild of unchanged code has to come out as "nothing changed".
const hash = (data) => createHash('sha256').update(data).digest('hex').slice(0, 16)
const stableTokens = { ...tokens, meta: { ...tokens.meta, ref: undefined, synced: undefined } }
const stableReadme = read(path.join(DS, 'README.md')).replace('{{REF}}', '').replace('{{MOTION}}', motion)
const snapshot = {
  ref,
  publishedAt: new Date().toISOString().slice(0, 10),
  files: Object.fromEntries(
    files.sort().map((f) => {
      const rel = f.replace(/^project\//, '')
      if (rel === 'tokens.json') return [rel, hash(JSON.stringify(stableTokens))]
      if (rel === 'README.md') return [rel, hash(stableReadme)]
      return [rel, hash(fs.readFileSync(path.join(DS, 'out', f)))]
    }),
  ),
  tokens: Object.fromEntries(
    [...colours, ...Object.values(families).flat(), ...notes.spacing].map((t) => [t.name, hash(JSON.stringify([t.value, t.usage]))]),
  ),
}

const PUBLISHED = path.join(DS, 'published.json')
const published = fs.existsSync(PUBLISHED) ? JSON.parse(read(PUBLISHED)) : null
const listed = (names) => (names.length > 8 ? `${names.slice(0, 8).join(', ')} and ${names.length - 8} more` : names.join(', '))
const changes = []
if (published) {
  const was = published.tokens
  const now = snapshot.tokens
  const added = Object.keys(now).filter((n) => !(n in was))
  const removed = Object.keys(was).filter((n) => !(n in now))
  const changed = Object.keys(now).filter((n) => n in was && was[n] !== now[n])
  if (changed.length) changes.push(`${changed.length} token${changed.length === 1 ? '' : 's'} changed: ${listed(changed)}`)
  if (added.length) changes.push(`${added.length} added: ${listed(added)}`)
  if (removed.length) changes.push(`${removed.length} removed: ${listed(removed)}`)
  const fileNames = new Set([...Object.keys(published.files), ...Object.keys(snapshot.files)])
  const changedFiles = [...fileNames].filter((f) => published.files[f] !== snapshot.files[f])
  if (changedFiles.length) changes.push(`${changedFiles.length} file${changedFiles.length === 1 ? '' : 's'} to publish: ${listed(changedFiles)}`)
}

console.log(`Design system built from ${ref}: ${colours.length} colours in ${themes.length} themes, ${allNames.length} tokens in all, ${kept.length} rules from theme.css.`)
console.log(`${files.length} files in design-system/out/project/.`)
if (!published) console.log('\nNo record of a publish yet. After publishing, run the build again with --mark-published.')
else if (!changes.length) console.log(`\nNothing the design system shows has changed since the last publish (${published.ref}, ${published.publishedAt}).`)
else {
  console.log(`\nSince the last publish (${published.ref}, ${published.publishedAt}):`)
  for (const c of changes) console.log(`  - ${c}`)
}
if (process.argv.includes('--mark-published')) {
  fs.writeFileSync(PUBLISHED, JSON.stringify(snapshot, null, 2) + '\n')
  console.log(`\nRecorded this build as published in design-system/published.json. Commit it.`)
}
if (warnings.length) {
  console.log(`\n${warnings.length} warning${warnings.length === 1 ? '' : 's'}:`)
  for (const w of warnings) console.log(`  - ${w}`)
  process.exitCode = 1
}
