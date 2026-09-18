#!/usr/bin/env node
/**
 * Asserts that no non-English catalogue is loaded before the app renders.
 *
 * English is bundled eagerly on purpose: it is the fallback for every untranslated entry, and
 * waiting for a round trip to have it would mean a frame of message ids. The other thirteen are
 * dynamic imports, so a browser fetches only the language it is showing.
 *
 * A single static `import` of one of those thirteen anywhere in the tree quietly undoes that. The
 * build still succeeds, the app still works, every test still passes, and the bundle has grown by
 * a megabyte of Korean that nobody asked for. There is no error to notice, which is exactly why
 * this needs a check rather than a convention.
 *
 * What counts as "before the app renders" is the entry module plus everything the entry preloads,
 * since Vite emits a `modulepreload` link for each statically reachable chunk. A catalogue that
 * appears in that set is eager. A catalogue in some other chunk is lazily reachable and fine.
 *
 * Usage: node scripts/i18n/check-bundle.mjs [dist-dir]
 */

import { readFileSync, readdirSync, existsSync } from 'node:fs'
import { join, dirname, basename } from 'node:path'
import { fileURLToPath } from 'node:url'

const HERE = dirname(fileURLToPath(import.meta.url))
const ROOT = join(HERE, '..', '..')
const DIST = process.argv[2] ?? join(ROOT, 'frontend', 'dist')
const LOCALES = join(ROOT, 'locales')

const indexPath = join(DIST, 'index.html')
if (!existsSync(indexPath)) {
  console.error(`No build at ${DIST}. Run npm run build first.`)
  process.exit(1)
}

const html = readFileSync(indexPath, 'utf8')

/** The entry module and every chunk it preloads: what a browser fetches before first paint. */
const eager = new Set()
for (const m of html.matchAll(/(?:src|href)="\/assets\/([^"]+\.js)"/g)) eager.add(m[1])

if (eager.size === 0) {
  console.error('Found no entry scripts in index.html. The build output shape has changed.')
  process.exit(1)
}

/**
 * A string that identifies one locale's compiled catalogue.
 *
 * Taken from the catalogue itself rather than hardcoded, so it cannot drift: the longest
 * translation in the file, which is the least likely to collide with ordinary code or with another
 * language. Skipped when a locale has nothing long enough to be distinctive.
 */
function fingerprint(locale) {
  const po = join(LOCALES, locale, 'client.po')
  if (!existsSync(po)) return null

  let best = ''
  let inMsgstr = false
  let current = ''
  for (const raw of readFileSync(po, 'utf8').split('\n')) {
    const line = raw.trim()
    if (line.startsWith('#~')) continue
    if (line.startsWith('msgstr ')) {
      inMsgstr = true
      current = line.slice(8, -1)
    } else if (inMsgstr && line.startsWith('"')) {
      current += line.slice(1, -1)
    } else {
      if (inMsgstr && current.length > best.length) best = current
      inMsgstr = false
      current = ''
    }
  }
  if (inMsgstr && current.length > best.length) best = current

  // Placeholders and markup anchors are rewritten by the compiler, so a fragment containing one
  // will not be found verbatim. Take the longest run of plain text instead.
  const plain = best.split(/\{[^}]*\}|<\d+>|<\/\d+>/).sort((a, b) => b.length - a.length)[0] ?? ''
  const trimmed = plain.trim()
  return trimmed.length >= 40 ? trimmed : null
}

const locales = readdirSync(LOCALES, { withFileTypes: true })
  .filter((d) => d.isDirectory() && d.name !== 'en')
  .map((d) => d.name)
  .sort()

const eagerSource = [...eager]
  .map((f) => ({ file: f, text: readFileSync(join(DIST, 'assets', f), 'utf8') }))

const problems = []
let checked = 0

for (const locale of locales) {
  const mark = fingerprint(locale)
  if (!mark) {
    console.log(`${locale}: no distinctive string long enough to fingerprint, skipped`)
    continue
  }
  checked++
  for (const { file, text } of eagerSource) {
    if (text.includes(mark)) {
      problems.push(
        `${locale} is bundled into ${basename(file)}, which loads before the app renders.\n` +
        `    Something imports its catalogue statically instead of with a dynamic import().\n` +
        `    Matched on: ${mark.slice(0, 60)}...`,
      )
      break
    }
  }
}

if (problems.length > 0) {
  console.error('\nNon-English catalogues are in the eager bundle:\n')
  for (const p of problems) console.error(`  ${p}\n`)
  process.exit(1)
}

console.log(
  `Only English loads eagerly. Checked ${checked} of ${locales.length} other languages ` +
  `against ${eager.size} eagerly-loaded chunk${eager.size === 1 ? '' : 's'}.`,
)
