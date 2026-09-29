// Builds the import fixture for docs/release-checklist.md: a root folder of series folders
// covering every layout the library import accepts (cbz, zip, cbr, cb7/7z, cbt, loose images,
// pdf) plus the awkward cases (duplicate chapter in two formats, a 7z named .cbz, a truncated
// archive, a unicode folder, a title that matches nothing). Pages are generated, so nothing
// copyrighted is involved and the output is the same on every machine.
//
//   node scripts/release-test/make-import.mjs [outDir]
//
// outDir defaults to .release-test/template/import and is replaced if it exists. The .cb7/.7z
// cases need 7-Zip; without it they are skipped with a warning.
import { execFileSync } from 'node:child_process'
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { crc32, deflateSync } from 'node:zlib'

const repoRoot = resolve(fileURLToPath(new URL('.', import.meta.url)), '..', '..')
const outDir = resolve(process.argv[2] ?? join(repoRoot, '.release-test', 'template', 'import'))

const WIDTH = 720
const HEIGHT = 1080
const PAGES = 4
const MTIME = new Date(2024, 0, 1, 12, 0, 0)

const FONT = {
  '0': ['01110', '10001', '10011', '10101', '11001', '10001', '01110'],
  '1': ['00100', '01100', '00100', '00100', '00100', '00100', '01110'],
  '2': ['01110', '10001', '00001', '00010', '00100', '01000', '11111'],
  '3': ['11110', '00001', '00001', '01110', '00001', '00001', '11110'],
  '4': ['00010', '00110', '01010', '10010', '11111', '00010', '00010'],
  '5': ['11111', '10000', '11110', '00001', '00001', '10001', '01110'],
  '6': ['00110', '01000', '10000', '11110', '10001', '10001', '01110'],
  '7': ['11111', '00001', '00010', '00100', '01000', '01000', '01000'],
  '8': ['01110', '10001', '10001', '01110', '10001', '10001', '01110'],
  '9': ['01110', '10001', '10001', '01111', '00001', '00010', '01100'],
  '.': ['00000', '00000', '00000', '00000', '00000', '01100', '01100'],
  '/': ['00001', '00010', '00010', '00100', '01000', '01000', '10000'],
  C: ['01110', '10001', '10000', '10000', '10000', '10001', '01110'],
  H: ['10001', '10001', '10001', '11111', '10001', '10001', '10001'],
  L: ['10000', '10000', '10000', '10000', '10000', '10000', '11111'],
  O: ['01110', '10001', '10001', '10001', '10001', '10001', '01110'],
  P: ['11110', '10001', '10001', '11110', '10000', '10000', '10000'],
  V: ['10001', '10001', '10001', '10001', '10001', '01010', '00100'],
  ' ': ['00000', '00000', '00000', '00000', '00000', '00000', '00000'],
}

// ---- pages ------------------------------------------------------------------------------------

function renderPage(hue, label, page, pages) {
  const px = Buffer.alloc(WIDTH * HEIGHT * 3)
  const bg = hsl(hue, 0.45, 0.82)
  const ink = hsl(hue, 0.6, 0.22)
  for (let i = 0; i < WIDTH * HEIGHT; i++) bg.copy(px, i * 3)
  const rect = (x0, y0, w, h, color) => {
    for (let y = Math.max(0, y0); y < Math.min(HEIGHT, y0 + h); y++)
      for (let x = Math.max(0, x0); x < Math.min(WIDTH, x0 + w); x++) color.copy(px, (y * WIDTH + x) * 3)
  }
  const text = (s, cy, scale) => {
    const w = s.length * 6 * scale - scale
    let x = Math.round((WIDTH - w) / 2)
    const y = Math.round(cy - (7 * scale) / 2)
    for (const ch of s) {
      const glyph = FONT[ch] ?? FONT[' ']
      glyph.forEach((row, gy) => {
        for (let gx = 0; gx < 5; gx++) if (row[gx] === '1') rect(x + gx * scale, y + gy * scale, scale, scale, ink)
      })
      x += 6 * scale
    }
  }
  rect(0, 0, WIDTH, 24, ink)
  rect(0, HEIGHT - 24, WIDTH, 24, ink)
  // A bar whose length is the page's position, so out-of-order pages are obvious at a glance.
  rect(40, HEIGHT - 120, Math.round(((WIDTH - 80) * page) / pages), 40, ink)
  text(label, HEIGHT * 0.3, 14)
  text(`P ${page}/${pages}`, HEIGHT * 0.55, 20)
  return px
}

function hsl(h, s, l) {
  const k = (n) => (n + h / 30) % 12
  const a = s * Math.min(l, 1 - l)
  const f = (n) => Math.round(255 * (l - a * Math.max(-1, Math.min(k(n) - 3, 9 - k(n), 1))))
  return Buffer.from([f(0), f(8), f(4)])
}

function png(px) {
  const rows = Buffer.alloc((WIDTH * 3 + 1) * HEIGHT)
  for (let y = 0; y < HEIGHT; y++) px.copy(rows, y * (WIDTH * 3 + 1) + 1, y * WIDTH * 3, (y + 1) * WIDTH * 3)
  const chunk = (type, data) => {
    const body = Buffer.concat([Buffer.from(type, 'ascii'), data])
    const out = Buffer.alloc(body.length + 8)
    out.writeUInt32BE(data.length, 0)
    body.copy(out, 4)
    out.writeUInt32BE(crc32(body), body.length + 4)
    return out
  }
  const ihdr = Buffer.alloc(13)
  ihdr.writeUInt32BE(WIDTH, 0)
  ihdr.writeUInt32BE(HEIGHT, 4)
  ihdr.set([8, 2, 0, 0, 0], 8)
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr),
    chunk('IDAT', deflateSync(rows)),
    chunk('IEND', Buffer.alloc(0)),
  ])
}

function chapterPages(hue, label) {
  return Array.from({ length: PAGES }, (_, i) => ({
    name: `${String(i + 1).padStart(3, '0')}.png`,
    data: png(renderPage(hue, label, i + 1, PAGES)),
  }))
}

function comicInfo({ series, number, volume, title }) {
  return {
    name: 'ComicInfo.xml',
    data: Buffer.from(
      `<?xml version="1.0" encoding="utf-8"?>\n<ComicInfo>\n  <Series>${series}</Series>\n` +
        (volume ? `  <Volume>${volume}</Volume>\n` : '') +
        `  <Number>${number}</Number>\n` +
        (title ? `  <Title>${title}</Title>\n` : '') +
        `  <PageCount>${PAGES}</PageCount>\n  <LanguageISO>en</LanguageISO>\n</ComicInfo>\n`,
      'utf8',
    ),
  }
}

// ---- containers -------------------------------------------------------------------------------

function dosTime(d) {
  const time = (d.getHours() << 11) | (d.getMinutes() << 5) | (d.getSeconds() >> 1)
  const date = ((d.getFullYear() - 1980) << 9) | ((d.getMonth() + 1) << 5) | d.getDate()
  return { time, date }
}

function zip(files) {
  const { time, date } = dosTime(MTIME)
  const locals = []
  const centrals = []
  let offset = 0
  for (const f of files) {
    const name = Buffer.from(f.name, 'utf8')
    const crc = crc32(f.data)
    const local = Buffer.alloc(30)
    local.writeUInt32LE(0x04034b50, 0)
    local.writeUInt16LE(20, 4)
    local.writeUInt16LE(0x0800, 6)
    local.writeUInt16LE(0, 8)
    local.writeUInt16LE(time, 10)
    local.writeUInt16LE(date, 12)
    local.writeUInt32LE(crc, 14)
    local.writeUInt32LE(f.data.length, 18)
    local.writeUInt32LE(f.data.length, 22)
    local.writeUInt16LE(name.length, 26)
    locals.push(local, name, f.data)
    const central = Buffer.alloc(46)
    central.writeUInt32LE(0x02014b50, 0)
    central.writeUInt16LE(20, 4)
    central.writeUInt16LE(20, 6)
    central.writeUInt16LE(0x0800, 8)
    central.writeUInt16LE(0, 10)
    central.writeUInt16LE(time, 12)
    central.writeUInt16LE(date, 14)
    central.writeUInt32LE(crc, 16)
    central.writeUInt32LE(f.data.length, 20)
    central.writeUInt32LE(f.data.length, 24)
    central.writeUInt16LE(name.length, 28)
    central.writeUInt32LE(offset, 42)
    centrals.push(central, name)
    offset += 30 + name.length + f.data.length
  }
  const cd = Buffer.concat(centrals)
  const end = Buffer.alloc(22)
  end.writeUInt32LE(0x06054b50, 0)
  end.writeUInt16LE(files.length, 8)
  end.writeUInt16LE(files.length, 10)
  end.writeUInt32LE(cd.length, 12)
  end.writeUInt32LE(offset, 16)
  return Buffer.concat([...locals, cd, end])
}

// RAR 4.x with every file stored. Nothing on a stock machine writes RAR, and "store" is the one
// method simple enough to write by hand; readers treat it like any other RAR.
function rar(files) {
  const block = (type, flags, fields) => {
    const header = Buffer.alloc(7 + fields.length)
    header.writeUInt8(type, 2)
    header.writeUInt16LE(flags, 3)
    header.writeUInt16LE(header.length, 5)
    fields.copy(header, 7)
    header.writeUInt16LE(crc32(header.subarray(2)) & 0xffff, 0)
    return header
  }
  const { time, date } = dosTime(MTIME)
  const parts = [Buffer.from([0x52, 0x61, 0x72, 0x21, 0x1a, 0x07, 0x00]), block(0x73, 0, Buffer.alloc(6))]
  for (const f of files) {
    const name = Buffer.from(f.name, 'ascii')
    const fields = Buffer.alloc(25 + name.length)
    fields.writeUInt32LE(f.data.length, 0)
    fields.writeUInt32LE(f.data.length, 4)
    fields.writeUInt8(2, 8)
    fields.writeUInt32LE(crc32(f.data), 9)
    fields.writeUInt32LE(((date << 16) | time) >>> 0, 13)
    fields.writeUInt8(29, 17)
    fields.writeUInt8(0x30, 18)
    fields.writeUInt16LE(name.length, 19)
    fields.writeUInt32LE(0x20, 21)
    name.copy(fields, 25)
    parts.push(block(0x74, 0x8000, fields), f.data)
  }
  parts.push(block(0x7b, 0x4000, Buffer.alloc(0)))
  return Buffer.concat(parts)
}

function tar(files) {
  const parts = []
  for (const f of files) {
    const h = Buffer.alloc(512)
    const put = (s, off, len) => h.write(s, off, len, 'ascii')
    const octal = (n, len) => n.toString(8).padStart(len - 1, '0') + '\0'
    put(f.name, 0, 100)
    put(octal(0o644, 8), 100, 8)
    put(octal(0, 8), 108, 8)
    put(octal(0, 8), 116, 8)
    put(octal(f.data.length, 12), 124, 12)
    put(octal(Math.floor(MTIME.getTime() / 1000), 12), 136, 12)
    put('        ', 148, 8)
    put('0', 156, 1)
    put('ustar\0', 257, 6)
    put('00', 263, 2)
    let sum = 0
    for (const b of h) sum += b
    put(sum.toString(8).padStart(6, '0') + '\0 ', 148, 8)
    parts.push(h, f.data, Buffer.alloc((512 - (f.data.length % 512)) % 512))
  }
  parts.push(Buffer.alloc(1024))
  return Buffer.concat(parts)
}

const sevenZip = ['C:\\Program Files\\7-Zip\\7z.exe', 'C:\\Program Files (x86)\\7-Zip\\7z.exe', '7z', '7zz']
  .find((candidate) => {
    try {
      execFileSync(candidate, ['i'], { stdio: 'ignore' })
      return true
    } catch {
      return false
    }
  })

function sevenZ(files) {
  if (!sevenZip) return null
  const work = mkdtempSync(join(tmpdir(), 'maki-7z-'))
  try {
    for (const f of files) writeFileSync(join(work, f.name), f.data)
    const archive = join(work, 'out.7z')
    execFileSync(sevenZip, ['a', '-t7z', '-mx=1', archive, ...files.map((f) => join(work, f.name))], { stdio: 'ignore' })
    return readFileSync(archive)
  } finally {
    rmSync(work, { recursive: true, force: true })
  }
}

function pdf(pageSets) {
  const objects = []
  const add = (body) => objects.push(body) && objects.length
  const catalog = add(null)
  const pagesId = add(null)
  const kids = []
  for (const px of pageSets) {
    const image = deflateSync(px)
    const imageId = add(
      Buffer.concat([
        Buffer.from(`<< /Type /XObject /Subtype /Image /Width ${WIDTH} /Height ${HEIGHT} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /Length ${image.length} >>\nstream\n`),
        image,
        Buffer.from('\nendstream'),
      ]),
    )
    const draw = `q ${WIDTH / 2} 0 0 ${HEIGHT / 2} 0 0 cm /Im Do Q`
    const contentId = add(Buffer.from(`<< /Length ${draw.length} >>\nstream\n${draw}\nendstream`))
    kids.push(
      add(
        Buffer.from(
          `<< /Type /Page /Parent ${pagesId} 0 R /MediaBox [0 0 ${WIDTH / 2} ${HEIGHT / 2}] /Resources << /XObject << /Im ${imageId} 0 R >> >> /Contents ${contentId} 0 R >>`,
        ),
      ),
    )
  }
  objects[catalog - 1] = Buffer.from(`<< /Type /Catalog /Pages ${pagesId} 0 R >>`)
  objects[pagesId - 1] = Buffer.from(`<< /Type /Pages /Kids [${kids.map((k) => `${k} 0 R`).join(' ')}] /Count ${kids.length} >>`)
  const parts = [Buffer.from('%PDF-1.4\n%\xe2\xe3\xcf\xd3\n', 'latin1')]
  const offsets = []
  let length = parts[0].length
  objects.forEach((body, i) => {
    const obj = Buffer.concat([Buffer.from(`${i + 1} 0 obj\n`), body, Buffer.from('\nendobj\n')])
    offsets.push(length)
    parts.push(obj)
    length += obj.length
  })
  const xref =
    `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n` +
    offsets.map((o) => `${String(o).padStart(10, '0')} 00000 n \n`).join('') +
    `trailer\n<< /Size ${objects.length + 1} /Root ${catalog} 0 R >>\nstartxref\n${length}\n%%EOF\n`
  parts.push(Buffer.from(xref))
  return Buffer.concat(parts)
}

// ---- fixture ----------------------------------------------------------------------------------

const written = []
const skipped = []
function write(folder, name, data) {
  const dir = join(outDir, folder)
  mkdirSync(dir, { recursive: true })
  writeFileSync(join(dir, name), data)
  written.push(`${folder}/${name}`)
}

rmSync(outDir, { recursive: true, force: true })
mkdirSync(outDir, { recursive: true })

// Plain CBZs, the common case. Chapter 1 carries a ComicInfo.xml, the rest do not.
{
  const folder = 'Dandadan (2021)'
  for (const [file, label, number, volume] of [
    ['Dandadan Vol.1 Ch.1.cbz', 'CH 1', '1', '1'],
    ['Dandadan Vol.1 Ch.2.cbz', 'CH 2', '2', '1'],
    ['Dandadan Ch.2.5.cbz', 'CH 2.5', '2.5', null],
    ['Dandadan Ch.3.cbz', 'CH 3', '3', null],
  ]) {
    const pages = chapterPages(10, label)
    if (number === '1') pages.push(comicInfo({ series: 'Dandadan', number, volume, title: 'That Is How Love Starts' }))
    write(folder, file, zip(pages))
  }
}

// .zip is placed unchanged: a CBZ is a zip.
for (const n of [1, 2, 3]) write('Chainsaw Man', `Chainsaw Man Ch.${String(n).padStart(3, '0')}.zip`, zip(chapterPages(0, `CH ${n}`)))

// .cbr under the romanised title, so matching goes through an alternate title.
for (const n of [1, 2, 3]) write('Sousou no Frieren', `Sousou no Frieren Ch.${String(n).padStart(3, '0')}.cbr`, rar(chapterPages(140, `CH ${n}`)))

// Every repack kind in one series, plus a 7z wearing a .cbz name.
{
  const folder = 'Blue Period'
  const cb7 = sevenZ(chapterPages(210, 'CH 1'))
  const plain7z = sevenZ(chapterPages(210, 'CH 2'))
  const fake = sevenZ(chapterPages(210, 'CH 4'))
  if (cb7) write(folder, 'Blue Period Ch.1.cb7', cb7)
  if (plain7z) write(folder, 'Blue Period Ch.2.7z', plain7z)
  write(folder, 'Blue Period Ch.3.cbt', tar(chapterPages(210, 'CH 3')))
  if (fake) write(folder, 'Blue Period Ch.4.cbz', fake)
  if (!sevenZip) skipped.push('Blue Period .cb7/.7z/fake .cbz (7-Zip not found)')
}

// Loose page images, one folder per volume.
for (const v of [1, 2]) {
  const vol = String(v).padStart(2, '0')
  for (const page of chapterPages(280, `VOL ${v}`)) write(`Vagabond/Vagabond Vol.${vol}`, page.name, page.data)
}

// PDFs are kept as PDFs and read in place.
for (const v of [1, 2]) {
  write(
    'Oyasumi Punpun',
    `Oyasumi Punpun Vol.${v}.pdf`,
    pdf(Array.from({ length: PAGES }, (_, i) => renderPage(40, `VOL ${v}`, i + 1, PAGES))),
  )
}

// The same chapter as .cbr and .cbz (the .cbz should win), plus files that are not comics.
{
  const folder = 'Spy x Family'
  write(folder, 'Spy x Family Ch.1.cbr', rar(chapterPages(320, 'CH 1')))
  write(folder, 'Spy x Family Ch.1.cbz', zip(chapterPages(320, 'CH 1')))
  write(folder, 'Spy x Family Ch.2.cbz', zip(chapterPages(320, 'CH 2')))
  write(folder, 'notes.txt', Buffer.from('Not a comic. The import should ignore this file.\n'))
  write(folder, 'release.nfo', Buffer.from('Not a comic either.\n'))
}

// One good chapter and one cut off halfway, which must be reported or skipped, never crash the run.
{
  const folder = 'Berserk'
  write(folder, 'Berserk Ch.001.cbz', zip(chapterPages(0, 'CH 1')))
  const whole = zip(chapterPages(0, 'CH 2'))
  write(folder, 'Berserk Ch.002.cbz', whole.subarray(0, Math.floor(whole.length / 2)))
}

// Non-ASCII folder and file names, which have broken path handling in Docker before.
for (const n of [1, 2]) write('ワンパンマン', `ワンパンマン Ch.${n}.cbz`, zip(chapterPages(180, `CH ${n}`)))

// Matches nothing in the catalogue.
write('Totally Made Up Series 9999', 'Totally Made Up Series 9999 Ch.1.cbz', zip(chapterPages(90, 'CH 1')))

// Holds no comics at all.
write('Empty Series Folder', 'readme.txt', Buffer.from('No comics in here.\n'))

console.log(`Wrote ${written.length} files to ${outDir}`)
for (const s of skipped) console.warn(`Skipped: ${s}`)
