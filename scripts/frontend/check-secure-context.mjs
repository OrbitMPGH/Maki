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

// Mantine's clipboard helpers call navigator.clipboard inside the library, so the pattern check
// above never sees them. They set an error and copy nothing, with no feedback to the user.
const bannedImports = [
  { name: 'CopyButton', from: '@mantine/core' },
  { name: 'useClipboard', from: '@mantine/hooks' },
]
const importBlock = /import\s+(?:type\s+)?\{([^}]*)\}\s*from\s*['"](@mantine\/[\w-]+)['"]/g

const failures = []
for (const file of files) {
  const rel = relative(root, file).split(sep).join('/')
  const source = readFileSync(file, 'utf8')
  for (const match of source.matchAll(importBlock)) {
    const names = match[1].split(',').map((n) => n.trim().split(/\s+as\s+/)[0].replace(/^type\s+/, ''))
    for (const { name, from } of bannedImports) {
      if (match[2] !== from || !names.includes(name)) continue
      const line = source.slice(0, match.index).split('\n').length
      failures.push(`frontend/src/${rel}:${line}: ${name} from ${from} copies nothing over plain HTTP. Use useCopyText() from 'components/ui/useCopyText'.`)
    }
  }
  source
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
