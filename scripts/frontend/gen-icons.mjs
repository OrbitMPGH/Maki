// Run by hand: npx -p @resvg/resvg-js node scripts/frontend/gen-icons.mjs
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs'
import { delimiter, dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { createRequire } from 'node:module'

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..')
const out = join(root, 'frontend', 'public', 'icons')
mkdirSync(out, { recursive: true })

function loadResvg() {
  try {
    return createRequire(import.meta.url)('@resvg/resvg-js')
  } catch {
    const bins = (process.env.PATH ?? '').split(delimiter).filter((p) => /node_modules[\\/]\.bin$/.test(p))
    for (const bin of bins) {
      try {
        return createRequire(join(bin, '..', '..', 'noop.js'))('@resvg/resvg-js')
      } catch {}
    }
    throw new Error('@resvg/resvg-js not found, run: npx -p @resvg/resvg-js node scripts/frontend/gen-icons.mjs')
  }
}
const { Resvg } = loadResvg()

const source = readFileSync(join(root, 'frontend', 'public', 'favicon.svg'), 'utf8')
const inner = source.replace(/^[\s\S]*?<svg[^>]*>/, '').replace(/<\/svg>\s*$/, '')

function compose(size, scale, background, body = inner) {
  const mark = size * scale
  const offset = (size - mark) / 2
  const bg = background ? `<rect width="${size}" height="${size}" fill="${background}"/>` : ''
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" viewBox="0 0 ${size} ${size}">${bg}<svg x="${offset}" y="${offset}" width="${mark}" height="${mark}" viewBox="17 17 62 62" fill="none">${body}</svg></svg>`
}

function write(name, svg, size) {
  const png = new Resvg(svg, { fitTo: { mode: 'width', value: size } }).render().asPng()
  writeFileSync(join(out, name), png)
  console.log(name, png.length)
}

const dark = '#0b0b0b'
write('icon-192.png', compose(192, 1), 192)
write('icon-512.png', compose(512, 1), 512)
write('icon-maskable-512.png', compose(512, 0.6, dark), 512)
write('apple-touch-icon-180.png', compose(180, 0.8, dark), 180)

// Android badges use alpha only, so the outlines, eyes and smile become holes in a white roll.
const cut = { '#1a1a1a': '#000000', '#fff': '#000000' }
const maskBody = inner.replace(/#[0-9a-fA-F]{3,8}\b/g, (c) => cut[c.toLowerCase()] ?? '#ffffff')
const badge = `<mask id="m" maskUnits="userSpaceOnUse" x="17" y="17" width="62" height="62">${maskBody}</mask><rect x="17" y="17" width="62" height="62" fill="#ffffff" mask="url(#m)"/>`
write('badge-96.png', compose(96, 0.9, null, badge), 96)
