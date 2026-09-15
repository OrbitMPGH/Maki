#!/usr/bin/env node
/**
 * Mirrors the key set of `locales/en/server.po` into the other languages.
 *
 * The English catalogue is hand-authored alongside the C# that names its keys, because there is no
 * C# extractor and writing one is not worth it for a few hundred entries. That leaves the thirteen
 * other catalogues to keep in step, which is exactly the kind of thing nobody does correctly by hand
 * across thirteen files. `LocalizationCatalogTests` fails the build when they drift, and this is what
 * fixes it.
 *
 * Rules:
 *   - a key in English and missing elsewhere is appended with an empty msgstr, which is what
 *     "untranslated" looks like and what the runtime falls back to English for
 *   - a key no longer in English is dropped, so nobody is asked to translate a dead string
 *   - an existing translation is never touched, including its fuzzy flag
 *   - the English source is copied into a `#.` comment, so a translator working in the file alone
 *     can see what they are translating
 *
 * Usage: node scripts/i18n/sync-server-keys.mjs [--check]
 *        --check reports drift and exits non-zero instead of writing. For CI.
 */

import { readFileSync, writeFileSync, existsSync, readdirSync } from 'node:fs'
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..', '..')
const LOCALES = join(ROOT, 'locales')
const SOURCE = 'en'

/**
 * Splits a PO file into its header and a list of entries.
 *
 * Deliberately a small hand-rolled reader. The only structure that matters here is "blocks separated
 * by blank lines, each with a msgid", and a dependency would have to be installed at the repo root,
 * which has no package.json of its own.
 */
function parse(text) {
  const blocks = text.split(/\n\s*\n/).filter((b) => b.trim().length > 0)
  const header = blocks.shift() ?? ''

  const entries = []
  for (const block of blocks) {
    const match = block.match(/^msgid\s+"((?:[^"\\]|\\.)*)"/m)
    if (!match) continue
    // Obsolete entries are the extractor's own bookkeeping, not live keys.
    if (/^#~/m.test(block)) continue
    entries.push({ key: unescapePo(match[1]), block: block.trimEnd() })
  }
  return { header, entries }
}

function unescapePo(s) {
  return s.replace(/\\n/g, '\n').replace(/\\"/g, '"').replace(/\\\\/g, '\\')
}

function escapePo(s) {
  return s.replace(/\\/g, '\\\\').replace(/"/g, '\\"').replace(/\n/g, '\\n')
}

/** The msgstr of a block, joined across folded lines. */
function translationOf(block) {
  const lines = block.split('\n')
  const start = lines.findIndex((l) => l.startsWith('msgstr '))
  if (start === -1) return ''

  let value = lines[start].slice('msgstr '.length).trim()
  let out = value.startsWith('"') ? unescapePo(value.slice(1, -1)) : ''
  for (let i = start + 1; i < lines.length; i++) {
    const line = lines[i].trim()
    if (!line.startsWith('"')) break
    out += unescapePo(line.slice(1, -1))
  }
  return out
}

function localeDirs() {
  return readdirSync(LOCALES, { withFileTypes: true })
    .filter((d) => d.isDirectory() && d.name !== SOURCE)
    .map((d) => d.name)
    .sort()
}

const check = process.argv.includes('--check')

const sourcePath = join(LOCALES, SOURCE, 'server.po')
if (!existsSync(sourcePath)) {
  console.error(`No source catalogue at ${sourcePath}`)
  process.exit(1)
}

const source = parse(readFileSync(sourcePath, 'utf8'))
const sourceOrder = source.entries.map((e) => e.key)
const sourceText = new Map(source.entries.map((e) => [e.key, translationOf(e.block)]))

let drifted = false

for (const locale of localeDirs()) {
  const path = join(LOCALES, locale, 'server.po')
  if (!existsSync(path)) {
    console.error(`${locale}: no server.po. Create it before syncing.`)
    drifted = true
    continue
  }

  const existing = parse(readFileSync(path, 'utf8'))
  const byKey = new Map(existing.entries.map((e) => [e.key, e.block]))

  const added = sourceOrder.filter((k) => !byKey.has(k))
  const removed = [...byKey.keys()].filter((k) => !sourceText.has(k))

  if (added.length === 0 && removed.length === 0) continue

  drifted = true
  const detail = [
    added.length ? `+${added.length}` : null,
    removed.length ? `-${removed.length}` : null,
  ].filter(Boolean).join(' ')
  console.log(`${locale}: ${detail}`)

  if (check) continue

  // Rebuilt in the English catalogue's order, so the two files read the same way side by side.
  const blocks = sourceOrder.map((key) => {
    if (byKey.has(key)) return byKey.get(key)
    const english = sourceText.get(key) ?? ''
    return [
      `#. English: ${english.replace(/\n/g, ' ')}`,
      `msgid "${escapePo(key)}"`,
      'msgstr ""',
    ].join('\n')
  })

  writeFileSync(path, `${existing.header.trimEnd()}\n\n${blocks.join('\n\n')}\n`, 'utf8')
}

if (check && drifted) {
  console.error('\nServer catalogues have drifted from English. Run: npm run i18n:sync')
  process.exit(1)
}

if (!drifted) console.log('Server catalogues are in step with English.')
