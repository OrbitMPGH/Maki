import { existsSync } from 'node:fs'
import { mkdir, readFile, writeFile } from 'node:fs/promises'
import { dirname, join } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
import puppeteer from 'puppeteer-core'
import sharp from 'sharp'
import { findChrome } from './chrome.mjs'

const here = dirname(fileURLToPath(import.meta.url))
const outDir = join(here, '..', '..', 'docs', 'readme')
const captions = JSON.parse(await readFile(join(here, 'captions.json'), 'utf8'))
const only = process.argv.slice(2)

const hasRaw = (shot) => existsSync(join(here, 'raw', `${shot}.png`))
const skipped = captions.phones.filter((p) => !hasRaw(p.shot)).map((p) => p.id)
if (skipped.length > 0) console.warn(`No capture for ${skipped.join(', ')}, leaving those out.`)
captions.phones = captions.phones.filter((p) => hasRaw(p.shot))

const shots = [
  captions.banner.desktop.shot,
  captions.banner.phone.shot,
  ...captions.tiles.filter((t) => t.shot).map((t) => t.shot),
  ...captions.phones.map((p) => p.shot),
]
const missing = [...new Set(shots)].filter((s) => !existsSync(join(here, 'raw', `${s}.png`)))
if (missing.length > 0) {
  console.error(`Missing raw captures: ${missing.join(', ')}. Run "npm run capture" first.`)
  process.exit(1)
}

await mkdir(outDir, { recursive: true })
const browser = await puppeteer.launch({ executablePath: findChrome(), headless: true })

try {
  const page = await browser.newPage()
  await page.setViewport({ width: 1800, height: 1200, deviceScaleFactor: 1 })
  await page.evaluateOnNewDocument((c) => {
    window.CAPTIONS = c
  }, captions)
  await page.goto(pathToFileURL(join(here, 'compose.html')).href, { waitUntil: 'networkidle0' })
  await page.evaluate(() => document.fonts.ready)

  for (const el of await page.$$('[data-image]')) {
    const name = await el.evaluate((n) => n.dataset.image)
    if (only.length > 0 && !only.includes(name)) continue

    const png = await el.screenshot({ type: 'png' })
    const small = await sharp(png).png({ palette: true, quality: 92, effort: 10 }).toBuffer()
    await writeFile(join(outDir, `${name}.png`), small)
    console.log(`${name}.png  ${(small.length / 1024).toFixed(0)} KB`)
  }
} finally {
  await browser.close()
}
