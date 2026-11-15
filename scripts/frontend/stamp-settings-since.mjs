// Settings registry entries added since the last release carry `since: 'next'`. The release commit
// stamps the real version onto them, so the New tab knows which release a setting came with.
//
//   node stamp-settings-since.mjs 0.37.0   replace every 'next' with 0.37.0
//   node stamp-settings-since.mjs --check  exit 1 while any 'next' is left (CI, PRs into main)

import { readFileSync, writeFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'

const registry = fileURLToPath(new URL('../../frontend/src/pages/settings/registry.ts', import.meta.url))
const pattern = /since: 'next'/g
const arg = process.argv[2]
const source = readFileSync(registry, 'utf8')
const count = source.match(pattern)?.length ?? 0

if (arg === '--check') {
  if (count > 0) {
    console.error(`${count} settings entr${count === 1 ? 'y' : 'ies'} still say since: 'next'. Run npm run settings:stamp-since -- <version> in the release commit.`)
    process.exit(1)
  }
  console.log("No since: 'next' left in the settings registry.")
  process.exit(0)
}

if (!/^\d+\.\d+\.\d+$/.test(arg ?? '')) {
  console.error('Usage: stamp-settings-since.mjs <major.minor.patch> | --check')
  process.exit(2)
}

writeFileSync(registry, source.replace(pattern, `since: '${arg}'`))
console.log(`Stamped ${count} settings entr${count === 1 ? 'y' : 'ies'} with ${arg}.`)
