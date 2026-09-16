#!/usr/bin/env node
/**
 * The acceptance gate for a translation run, and the thing that makes translation delegable at all.
 *
 * A model filling in 2,400 entries across thirteen languages will get some of them wrong, and nobody
 * here reads eleven of those languages. So the question is never "is this translation good", which
 * cannot be checked mechanically, but "is this translation structurally sound", which can: does it
 * carry the same placeholders, does it cover the plural categories its language actually requires,
 * does it parse as ICU at all.
 *
 * The plural check is the one that earns its keep. A model handed an English `one`/`other` pair
 * will cheerfully return the same two forms for Polish, which needs `one`/`few`/`many`/`other`. The
 * result is grammatically wrong for most numbers, reads fine to anyone who does not speak Polish,
 * and is completely invisible by eye.
 *
 * Usage: node scripts/i18n/validate.mjs [--locale sv] [--component client|server]
 */

import { execFileSync } from 'node:child_process'
import { createRequire } from 'node:module'
import { join } from 'node:path'
import {
  COMPONENTS,
  LOCALES,
  ROOT,
  SOURCE_LOCALE,
  catalogPath,
  locales,
  readCatalog,
  sourceTextFor,
} from './po.mjs'

/**
 * The ICU parser lives in `frontend/node_modules`, because that is where the only package.json in
 * the repo is and adding a second npm root for three scripts would be worse. Node resolves from the
 * importing file upward, which from here finds nothing, so point it at the frontend explicitly.
 */
const { parse: parseIcu } = createRequire(join(ROOT, 'frontend', 'package.json'))(
  '@messageformat/parser',
)

const args = process.argv.slice(2)
const only = (flag) => {
  const i = args.indexOf(flag)
  return i === -1 ? null : args[i + 1]
}

const wantedLocale = only('--locale')
const wantedComponent = only('--component')

const problems = []
const report = (locale, component, key, message) =>
  problems.push({ locale, component, key, message })

/**
 * Every argument name an ICU message refers to, including the selector of a plural or select block.
 * A translation must use exactly this set: a dropped one silently loses information, an invented one
 * throws at format time.
 */
function placeholders(ast, into = new Set()) {
  for (const token of ast) {
    if (token.type === 'argument' || token.type === 'function') into.add(token.arg)
    if (token.type === 'plural' || token.type === 'select' || token.type === 'selectordinal') {
      into.add(token.arg)
      for (const c of token.cases) placeholders(c.tokens, into)
    }
    if (token.type === 'octothorpe') continue
  }
  return into
}

/** The case keys of every plural block, so they can be checked against the locale's own rules. */
function pluralCases(ast, into = []) {
  for (const token of ast) {
    if (token.type === 'plural' || token.type === 'selectordinal') {
      into.push({
        arg: token.arg,
        type: token.type,
        keys: token.cases.map((c) => c.key),
      })
      for (const c of token.cases) pluralCases(c.tokens, into)
    }
    if (token.type === 'select') {
      for (const c of token.cases) pluralCases(c.tokens, into)
    }
  }
  return into
}

function requiredCategories(locale, type) {
  return new Set(
    new Intl.PluralRules(locale, {
      type: type === 'selectordinal' ? 'ordinal' : 'cardinal',
    }).resolvedOptions().pluralCategories,
  )
}

/**
 * Entries whose translation has been edited since HEAD while NOT carrying a fuzzy flag.
 *
 * The one write rule in this pipeline is "only ever fill an entry that is empty or fuzzy". A
 * non-fuzzy translation is one a human reviewed, or one Weblate pushed, and nothing automated may
 * touch it. Checking against git rather than against a state file means the rule survives a plain
 * pull of somebody else's commits.
 */
function clobberedEntries() {
  let changed = []
  try {
    const out = execFileSync('git', ['diff', '--name-only', 'HEAD', '--', 'locales'], {
      cwd: ROOT,
      encoding: 'utf8',
    })
    changed = out.split('\n').map((l) => l.trim()).filter(Boolean)
  } catch {
    // Not a git checkout, or git unavailable. The rest of the validation still stands.
    return []
  }

  const clobbered = []
  for (const file of changed) {
    const m = file.match(/locales\/([^/]+)\/(client|server)\.po$/)
    if (!m) continue
    const [, locale, component] = m
    if (locale === SOURCE_LOCALE) continue

    let before
    try {
      before = execFileSync('git', ['show', `HEAD:${file}`], { cwd: ROOT, encoding: 'utf8' })
    } catch {
      continue // new file
    }

    const after = readCatalog(locale, component)
    const previous = parseCatalogText(before)

    for (const [key, old] of previous) {
      if (!old.value || old.fuzzy) continue // was empty or needed review: fair game
      const now = after.get(key)
      if (!now) {
        clobbered.push({ locale, component, key, what: 'deleted' })
      } else if (now.value !== old.value) {
        clobbered.push({ locale, component, key, what: 'overwritten' })
      }
    }
  }
  return clobbered
}

/** readCatalog, but over text rather than a path, for reading a blob out of git. */
function parseCatalogText(text) {
  const entries = new Map()
  for (const block of text.split(/\n\s*\n/)) {
    if (!block.trim() || /^#~/m.test(block)) continue
    const lines = block.split('\n')
    const idAt = lines.findIndex((l) => l.startsWith('msgid '))
    const strAt = lines.findIndex((l) => l.startsWith('msgstr '))
    if (idAt === -1 || strAt === -1) continue

    const read = (start, field) => {
      const first = lines[start].slice(field.length + 1).trim()
      let out = first.startsWith('"') ? unescape(first.slice(1, -1)) : ''
      for (let i = start + 1; i < lines.length; i++) {
        const l = lines[i].trim()
        if (!l.startsWith('"')) break
        out += unescape(l.slice(1, -1))
      }
      return out
    }
    const unescape = (s) =>
      s.replace(/\\n/g, '\n').replace(/\\t/g, '\t').replace(/\\"/g, '"').replace(/\\\\/g, '\\')

    const key = read(idAt, 'msgid')
    if (!key) continue
    entries.set(key, {
      value: read(strAt, 'msgstr'),
      fuzzy: lines.some((l) => /^#,.*\bfuzzy\b/.test(l)),
    })
  }
  return entries
}

// ---------------------------------------------------------------------------

const components = wantedComponent ? [wantedComponent] : COMPONENTS
const targets = (wantedLocale ? [wantedLocale] : locales()).filter((l) => l !== SOURCE_LOCALE)

for (const component of components) {
  const source = readCatalog(SOURCE_LOCALE, component)

  for (const locale of targets) {
    const catalog = readCatalog(locale, component)

    const extra = [...catalog.keys()].filter((k) => !source.has(k))
    for (const key of extra) {
      report(locale, component, key, 'key is not in the English catalogue')
    }
    const absent = [...source.keys()].filter((k) => !catalog.has(k))
    for (const key of absent) {
      report(locale, component, key, 'key is missing (it may be empty, but it must be present)')
    }

    for (const [key, entry] of catalog) {
      const translation = entry.value
      // Empty is the normal state for most entries and falls back to English at runtime. Only a
      // translation that exists has to be correct.
      if (!translation.trim()) continue

      const english = sourceTextFor(key, entry, source, component)

      let sourceAst
      let targetAst
      try {
        sourceAst = parseIcu(english)
      } catch {
        report(locale, component, key, 'the ENGLISH source does not parse as ICU, fix that first')
        continue
      }
      try {
        targetAst = parseIcu(translation)
      } catch (err) {
        report(locale, component, key, `does not parse as ICU: ${err.message}`)
        continue
      }

      const want = placeholders(sourceAst)
      const got = placeholders(targetAst)
      const missing = [...want].filter((p) => !got.has(p))
      const invented = [...got].filter((p) => !want.has(p))
      if (missing.length || invented.length) {
        const bits = []
        if (missing.length) bits.push(`dropped {${missing.join('}, {')}}`)
        if (invented.length) bits.push(`invented {${invented.join('}, {')}}`)
        report(locale, component, key, `placeholders differ from English: ${bits.join('; ')}`)
      }

      for (const block of pluralCases(targetAst, [])) {
        const required = requiredCategories(locale, block.type)
        const supplied = new Set(block.keys.filter((k) => !String(k).startsWith('=')))
        const uncovered = [...required].filter((c) => !supplied.has(c))
        if (uncovered.length) {
          report(
            locale,
            component,
            key,
            `plural on {${block.arg}} is missing the ${uncovered.join(', ')} ` +
              `form${uncovered.length > 1 ? 's' : ''}, which ${locale} requires ` +
              `(needs ${[...required].join(', ')})`,
          )
        }
        const unknown = [...supplied].filter((c) => !required.has(c) && c !== 'other')
        if (unknown.length) {
          report(locale, component, key, `plural on {${block.arg}} has forms ${locale} does not use: ${unknown.join(', ')}`)
        }
      }

      if (translation.includes('—')) {
        report(locale, component, key, 'contains an em dash, which the house style forbids')
      }
      if (/```/.test(translation)) {
        report(locale, component, key, 'contains a markdown code fence, which is model output leaking in')
      }
      if (english.trim() === english && translation.trim() !== translation) {
        report(locale, component, key, 'has leading or trailing whitespace the English does not')
      }
    }
  }
}

for (const c of clobberedEntries()) {
  report(
    c.locale,
    c.component,
    c.key,
    `a reviewed (non-fuzzy) translation was ${c.what}. Only empty or fuzzy entries may be written.`,
  )
}

if (problems.length === 0) {
  const scope = wantedLocale ? `${wantedLocale}` : 'all languages'
  console.log(`Catalogues are structurally sound (${scope}).`)
  process.exit(0)
}

const byLocale = new Map()
for (const p of problems) {
  const list = byLocale.get(`${p.locale}/${p.component}`) ?? []
  list.push(p)
  byLocale.set(`${p.locale}/${p.component}`, list)
}

for (const [where, list] of [...byLocale].sort()) {
  console.error(`\n${where}.po`)
  for (const p of list) console.error(`  ${p.key}\n    ${p.message}`)
}
console.error(`\n${problems.length} problem${problems.length > 1 ? 's' : ''}.`)
process.exit(1)
