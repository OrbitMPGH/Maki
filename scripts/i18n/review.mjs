#!/usr/bin/env node
/**
 * Builds a side-by-side review page for one language.
 *
 * The validator proves a translation is structurally sound: same placeholders, the plural categories
 * the language actually needs, valid ICU. It cannot tell you whether the Swedish is any good. That
 * needs a person who reads it, and the cheapest way to give them 300 entries is a linear read with
 * the English beside the translation and the source file in view, because the source file is what
 * decides the answer: the same English word is a navigation tab in `nav.ts` and a failure sentence
 * in `DownloadQueueService.cs`, and those do not translate the same way.
 *
 * Emits one self-contained HTML file. Publish it as an Artifact and the reviewer's verdicts persist,
 * which is what lets the corrections come back here rather than living in somebody's notes.
 *
 * Usage:
 *   node scripts/i18n/review.mjs --locale sv [--component client|server] [--out review.html]
 */

import { writeFileSync, readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import {
  COMPONENTS,
  SOURCE_LOCALE,
  locales,
  readCatalog,
  sourceTextFor,
} from './po.mjs'

const HERE = dirname(fileURLToPath(import.meta.url))

const args = process.argv.slice(2)
const flag = (name) => {
  const i = args.indexOf(name)
  return i === -1 ? null : args[i + 1]
}

const locale = flag('--locale')
const wantedComponent = flag('--component')
const out = flag('--out') ?? `review-${locale}.html`

if (!locale || locale === SOURCE_LOCALE || !locales().includes(locale)) {
  console.error('Usage: node scripts/i18n/review.mjs --locale <code> [--component client|server] [--out FILE]')
  console.error(`Languages: ${locales().filter((l) => l !== SOURCE_LOCALE).join(', ')}`)
  process.exit(1)
}

const components = wantedComponent ? [wantedComponent] : COMPONENTS
const entries = []

for (const component of components) {
  const source = readCatalog(SOURCE_LOCALE, component)
  const catalog = readCatalog(locale, component)

  for (const [key, entry] of catalog) {
    const translation = entry.value.trim()
    if (!translation) continue // nothing to review; todo.mjs is where those live

    const english = sourceTextFor(key, entry, source, component)
    entries.push({
      component,
      key,
      english,
      translation: entry.value,
      // Fuzzy means nobody has confirmed it. Clearing that flag is the point of this review, so an
      // entry that has already lost it is shown as settled rather than asked about again.
      pending: entry.fuzzy,
      // Left in English on purpose, usually a product or feature name from the glossary. Worth
      // surfacing: it reads as an oversight unless you know it was a decision.
      kept: entry.value.trim() === english.trim(),
      file: entry.references[0] ?? '(no source reference)',
      notes: entry.comments.filter((c) => !c.startsWith('English: ')),
    })
  }
}

entries.sort((a, b) => a.file.localeCompare(b.file) || a.english.localeCompare(b.english))

const template = readFileSync(join(HERE, 'review-template.html'), 'utf8')
const payload = JSON.stringify({ locale, entries }).replace(/</g, '\\u003c')

writeFileSync(out, template.replace('"__REVIEW_DATA__"', payload), 'utf8')

const pending = entries.filter((e) => e.pending).length
console.log(`${entries.length} translated entries for ${locale} (${pending} awaiting review) -> ${out}`)
