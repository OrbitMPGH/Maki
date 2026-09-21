#!/usr/bin/env node
/**
 * Post-run check for an automated translation pass: every entry that changed against HEAD must
 * carry a fuzzy flag, because a machine wrote it and a person has not looked yet. validate.mjs
 * guards the other direction (a reviewed entry must not change); this guards the flag on what was
 * written. Also lists any changed path outside locales/, which no translating agent may touch.
 *
 * Usage: node scripts/i18n/check-written-fuzzy.mjs
 */

import { execFileSync } from 'node:child_process'
import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { ROOT, unescapePo } from './po.mjs'

const git = (...a) => execFileSync('git', a, { cwd: ROOT, encoding: 'utf8' })

const changed = git('diff', '--name-only', 'HEAD').split('\n').filter(Boolean)
const outside = changed.filter((f) => !f.startsWith('locales/'))
const catalogs = changed.filter((f) => /^locales\/[^/]+\/(client|server)\.po$/.test(f))

function joined(lines, field) {
  const start = lines.findIndex((l) => l.startsWith(`${field} `))
  if (start === -1) return null
  const first = lines[start].slice(field.length + 1).trim()
  let out = first.startsWith('"') ? unescapePo(first.slice(1, -1)) : ''
  for (let i = start + 1; i < lines.length; i++) {
    const line = lines[i].trim()
    if (!line.startsWith('"')) break
    out += unescapePo(line.slice(1, -1))
  }
  return out
}

function parse(text) {
  const map = new Map()
  for (const block of text.split(/\n\s*\n/)) {
    if (!block.trim() || /^#~/m.test(block)) continue
    const lines = block.split('\n')
    const msgid = joined(lines, 'msgid')
    if (msgid === null || msgid === '') continue
    const ctx = joined(lines, 'msgctxt')
    const key = ctx === null ? msgid : `${ctx}${msgid}`
    map.set(key, {
      msgid,
      value: joined(lines, 'msgstr') ?? '',
      fuzzy: lines.some((l) => /^#,.*\bfuzzy\b/.test(l)),
    })
  }
  return map
}

let problems = 0
for (const file of catalogs) {
  if (file.startsWith('locales/en/')) continue
  let before
  try {
    before = parse(git('show', `HEAD:${file}`))
  } catch {
    before = new Map()
  }
  const after = parse(readFileSync(join(ROOT, file), 'utf8'))
  for (const [key, entry] of after) {
    const old = before.get(key)
    if (old && old.value === entry.value) continue
    if (!entry.value.trim()) continue
    if (entry.fuzzy) continue
    problems++
    console.log(`${file}\n  ${entry.msgid}\n    written this run but carries no fuzzy flag`)
  }
}

for (const f of outside) {
  problems++
  console.log(`${f}\n    changed outside locales/`)
}

console.log(
  problems
    ? `${problems} problem${problems === 1 ? '' : 's'}.`
    : 'OK: every written entry is fuzzy, nothing outside locales/ changed.',
)
process.exit(problems ? 1 : 0)
