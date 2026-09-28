// Self-hosted instances are usually opened over plain HTTP on a LAN address, which browsers treat
// as an insecure context: these APIs are undefined there and throw at the call site. Each has a
// helper in frontend/src/lib that falls back; calling the raw API elsewhere ships a bug that never
// reproduces on localhost. crypto.randomUUID did this in 0.30.0 and again in 0.31.0.
import { readdirSync, readFileSync } from 'node:fs'
import { join, relative, sep } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = join(fileURLToPath(new URL('.', import.meta.url)), '..', '..', 'frontend', 'src')

const banned = [
  { pattern: /\bcrypto\.randomUUID\b/, use: "randomUUID() from 'lib/uuid'", home: 'lib/uuid.ts' },
  { pattern: /\bnavigator\.clipboard\b/, use: "copyText() from 'lib/clipboard'", home: 'lib/clipboard.ts' },
  { pattern: /\bcrypto\.subtle\b/, use: 'a JS fallback, subtle is undefined outside secure contexts', home: null },
]

const files = []
const walk = (dir) => {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const path = join(dir, entry.name)
    if (entry.isDirectory()) walk(path)
    else if (/\.(ts|tsx)$/.test(entry.name)) files.push(path)
  }
}
walk(root)

const failures = []
for (const file of files) {
  const rel = relative(root, file).split(sep).join('/')
  readFileSync(file, 'utf8')
    .split('\n')
    .forEach((line, i) => {
      if (/^\s*(\/\/|\*|\/\*)/.test(line)) return
      for (const { pattern, use, home } of banned) {
        if (home === rel || !pattern.test(line)) continue
        failures.push(`frontend/src/${rel}:${i + 1}: ${pattern.source.replace(/\b/g, '')} is undefined over plain HTTP. Use ${use}.`)
      }
    })
}

if (failures.length) {
  console.error(failures.join('\n'))
  process.exit(1)
}
console.log('No secure-context-only APIs outside their helpers.')
