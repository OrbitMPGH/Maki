#!/usr/bin/env node
/**
 * Prints what still needs translating in one language, as a self-contained worklist.
 *
 * The selection rule is the whole pipeline in one line: **an entry is eligible when its translation
 * is empty, or when it carries a fuzzy flag.** Everything else has been reviewed by a person, or
 * pushed by Weblate, and nothing automated may touch it. Because that is read off the file itself
 * rather than a state file kept alongside it, pulling somebody else's commits cannot put the two out
 * of step.
 *
 * Each entry ships its source references, because they are worth more than they look: knowing a
 * string lives in `SettingsPage.tsx` rather than `DownloadQueueService.cs` is the difference between
 * translating a button label and translating a failure sentence.
 *
 * Usage:
 *   node scripts/i18n/todo.mjs --locale sv [--limit 200] [--component client|server] [--count]
 */

import {
  COMPONENTS,
  SOURCE_LOCALE,
  locales,
  readCatalog,
  sourceTextFor,
} from './po.mjs'

const args = process.argv.slice(2)
const flag = (name) => {
  const i = args.indexOf(name)
  return i === -1 ? null : args[i + 1]
}

const locale = flag('--locale')
const limit = Number(flag('--limit') ?? 0) || Infinity
const wantedComponent = flag('--component')
const countOnly = args.includes('--count')

if (!locale) {
  console.error('Usage: node scripts/i18n/todo.mjs --locale <code> [--limit N] [--component client|server]')
  console.error(`Languages: ${locales().filter((l) => l !== SOURCE_LOCALE).join(', ')}`)
  process.exit(1)
}
if (locale === SOURCE_LOCALE) {
  console.error(`${SOURCE_LOCALE} is the source language; there is nothing to translate into it.`)
  process.exit(1)
}
if (!locales().includes(locale)) {
  console.error(`Unknown language "${locale}". Known: ${locales().join(', ')}`)
  process.exit(1)
}

const components = wantedComponent ? [wantedComponent] : COMPONENTS
const pending = []
let total = 0

for (const component of components) {
  const source = readCatalog(SOURCE_LOCALE, component)
  const catalog = readCatalog(locale, component)

  for (const [key, entry] of catalog) {
    const eligible = !entry.value.trim() || entry.fuzzy
    if (!eligible) continue
    total++
    pending.push({
      component,
      key,
      english: sourceTextFor(key, entry, source, component),
      current: entry.value,
      fuzzy: entry.fuzzy,
      // The `#. English: ...` line is the sync script's own bookkeeping, not a translator note.
      notes: entry.comments.filter((c) => !c.startsWith('English: ')),
      references: entry.references,
    })
  }
}

if (countOnly || total === 0) {
  console.log(`${total} entr${total === 1 ? 'y' : 'ies'} pending in ${locale}.`)
  process.exit(0)
}

// Grouped by source file so the tone stays consistent within one screen of the app.
pending.sort((a, b) => {
  const fa = a.references[0] ?? ''
  const fb = b.references[0] ?? ''
  return fa.localeCompare(fb) || a.key.localeCompare(b.key)
})

const shown = pending.slice(0, limit === Infinity ? pending.length : limit)

console.log(`# ${shown.length} of ${total} pending entries for ${locale}`)
console.log('#')
console.log('# Fill each msgstr. Keep the fuzzy flag on everything you write.')
console.log('# Read scripts/i18n/TRANSLATING-AGENT.md before starting.')

let lastFile = null
for (const entry of shown) {
  const file = entry.references[0] ?? '(no source reference)'
  if (file !== lastFile) {
    console.log(`\n\n## ${file}`)
    lastFile = file
  }

  console.log('')
  console.log(`key:     ${entry.key}`)
  console.log(`file:    ${entry.component}.po`)
  console.log(`english: ${entry.english}`)
  if (entry.notes.length) console.log(`note:    ${entry.notes.join(' ')}`)
  if (entry.fuzzy && entry.current) {
    console.log(`current: ${entry.current}   <- needs review, replace or confirm`)
  }
}

if (shown.length < total) {
  console.log(`\n\n# ${total - shown.length} more not shown. Commit these, then run again.`)
}
