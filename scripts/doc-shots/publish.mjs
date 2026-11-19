// Copies the shots the docs site uses from out/ into site/src/assets/ui/ as WebP, and removes the
// ones no page references any more. Run it after `npm run shots`.
import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs'
import { mkdir, rm, writeFile } from 'node:fs/promises'
import { dirname, join, relative, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import sharp from 'sharp'

const here = dirname(fileURLToPath(import.meta.url))
const repo = resolve(here, '..', '..')
const docs = join(repo, 'site', 'src', 'content', 'docs')
const assets = join(repo, 'site', 'src', 'assets', 'ui')
const out = join(here, 'out')

function walk(dir, match) {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name)
    return statSync(path).isDirectory() ? walk(path, match) : match.test(name) ? [path] : []
  })
}

// A docs page names a shot as .../assets/ui/<group>/<name>.webp, the same path as out/<group>/<name>.png.
const used = new Set()
for (const page of walk(docs, /\.mdx?$/)) {
  for (const m of readFileSync(page, 'utf8').matchAll(/assets\/ui\/([\w-]+\/[\w-]+)\.webp/g)) used.add(m[1])
}

const missing = [...used].filter((name) => !existsSync(join(out, `${name}.png`)))
if (missing.length) {
  console.error(`The docs use shots that out/ does not have. Run npm run shots first:\n  ${missing.join('\n  ')}`)
  process.exit(1)
}

// A scanner or indexer on Windows can hold a file that was just written for a moment.
async function writeWithRetry(path, data) {
  for (let attempt = 1; ; attempt++) {
    try {
      return await writeFile(path, data)
    } catch (err) {
      if (attempt >= 6) throw err
      await new Promise((r) => setTimeout(r, 400 * attempt))
    }
  }
}

let bytes = 0
for (const name of [...used].sort()) {
  const target = join(assets, `${name}.webp`)
  await mkdir(dirname(target), { recursive: true })
  // Encoded in memory and written by Node: sharp's own file writer trips over Windows file locks.
  const webp = await sharp(join(out, `${name}.png`)).webp({ quality: 82, effort: 5 }).toBuffer()
  await writeWithRetry(target, webp)
  bytes += webp.length
}

let removed = 0
if (existsSync(assets)) {
  for (const file of walk(assets, /\.webp$/)) {
    const name = relative(assets, file).replaceAll('\\', '/').replace(/\.webp$/, '')
    if (!used.has(name)) {
      await rm(file)
      removed++
    }
  }
}

console.log(`${used.size} images written to site/src/assets/ui (${(bytes / 1048576).toFixed(1)} MB)${removed ? `, ${removed} unused removed` : ''}.`)
