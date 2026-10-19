import { existsSync, readFileSync } from 'node:fs'
import { mkdir } from 'node:fs/promises'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import puppeteer, { KnownDevices } from 'puppeteer-core'
import { findChrome } from './chrome.mjs'

const here = dirname(fileURLToPath(import.meta.url))
const rawDir = join(here, 'raw')

const base = (process.env.MAKI_URL ?? 'http://localhost:8990').replace(/\/$/, '')
const credentialsPath = join(here, '..', '..', '.screenshots', 'credentials.json')
const key = process.env.MAKI_API_KEY ?? (existsSync(credentialsPath) ? JSON.parse(readFileSync(credentialsPath, 'utf8')).apiKey : undefined)
const background = process.env.MAKI_BACKGROUND ?? 'tinted'
const accent = process.env.MAKI_ACCENT ?? 'indigo'
const only = process.argv.slice(2)

if (!key) {
  console.error('Set MAKI_API_KEY to a Full API key, or build the showcase config so .screenshots/credentials.json exists.')
  process.exit(1)
}

async function api(path) {
  const res = await fetch(`${base}/api/v1${path}`, { headers: { 'X-Api-Key': key } })
  if (!res.ok) throw new Error(`GET ${path} returned ${res.status}`)
  return res.json()
}

async function pickShowcase() {
  const wanted = (process.env.MAKI_SERIES ?? 'Dandadan').toLowerCase()
  const series = await api('/series')
  const match = series.find((s) => s.displayTitle.toLowerCase().includes(wanted)) ?? series[0]
  if (!match) throw new Error('The library has no series.')
  return { seriesId: match.id, chapterId: Number(process.env.MAKI_READER_CHAPTER) || null, title: match.displayTitle }
}

const showcase = await pickShowcase()
console.log(`Series page: ${showcase.title}`)

const TILE = { width: 1400, height: 756 }

const SHOTS = [
  { name: 'desktop-library', device: 'desktop', path: '/library', accent: 'rose' },
  { name: 'desktop-discover', device: 'desktop', path: '/discover', accent: 'rose', viewport: { width: 1600, height: 864 } },
  { name: 'desktop-scrobble', device: 'desktop', path: '/scrobble', accent: 'indigo', viewport: TILE },
  { name: 'desktop-series', device: 'desktop', path: `/series/${showcase.seriesId}`, accent: 'emerald', viewport: TILE, click: 'Chapters', scrollY: 300 },
  { name: 'desktop-stats', device: 'desktop', path: '/stats', accent: 'amber', viewport: TILE },
  { name: 'phone-home', device: 'phone', path: '/home', accent: 'rose' },
  showcase.chapterId && { name: 'phone-reader', device: 'phone', path: `/read/${showcase.chapterId}`, accent: 'rose', settleMs: 1500, keys: ['ArrowLeft', 'ArrowLeft', 'ArrowLeft'] },
  { name: 'phone-discover', device: 'phone', path: '/discover/recommended', accent: 'rose', scrollY: 330 },
  { name: 'phone-stats', device: 'phone', path: '/stats', accent: 'amber' },
].filter(Boolean)

if (!showcase.chapterId) console.warn('MAKI_READER_CHAPTER is not set, so the reader shot is skipped.')

const hideNoise = `
  .mantine-Notifications-root, [data-version], .star-nudge { display: none !important; }
`

await mkdir(rawDir, { recursive: true })
const browser = await puppeteer.launch({ executablePath: findChrome(), headless: true })

try {
  for (const shot of SHOTS.filter((s) => only.length === 0 || only.includes(s.name))) {
    const page = await browser.newPage()
    await page.setExtraHTTPHeaders({ 'X-Api-Key': key })
    await page.evaluateOnNewDocument(
      (bg, ac) => {
        localStorage.setItem('maki-background', bg)
        localStorage.setItem('maki-accent', ac)
        localStorage.setItem('star-nudge-dismissed', '1')
      },
      background,
      shot.accent ?? accent,
    )

    // Capturing must never write: the reader would otherwise record progress and XP.
    await page.setRequestInterception(true)
    page.on('request', (req) => {
      const url = new URL(req.url())
      const readOnlyPost = req.method() === 'POST' && /^\/api\/v1\/recommendations$/.test(url.pathname)
      const writes =
        req.method() !== 'GET' && req.method() !== 'HEAD' && url.pathname.startsWith('/api/') && !readOnlyPost
      if (writes) req.abort()
      else req.continue()
    })

    if (shot.device === 'phone') await page.emulate(KnownDevices['iPhone 13'])
    else await page.setViewport({ ...(shot.viewport ?? { width: 1600, height: 1000 }), deviceScaleFactor: 2 })

    await page.goto(base + shot.path, { waitUntil: 'networkidle2', timeout: 60000 })
    if (/\/(login|setup)/.test(new URL(page.url()).pathname)) {
      throw new Error(`${shot.name} landed on ${page.url()}. Is the API key valid and Full scope?`)
    }
    await page.addStyleTag({ content: hideNoise })

    await page.evaluate(async () => {
      for (let y = 0; y < document.body.scrollHeight; y += 600) {
        window.scrollTo(0, y)
        await new Promise((r) => setTimeout(r, 120))
      }
      window.scrollTo(0, 0)
    })
    for (const key of shot.keys ?? []) {
      await page.keyboard.press(key)
      await new Promise((r) => setTimeout(r, 900))
    }
    if (shot.click) {
      await page.evaluate((label) => {
        const tab = [...document.querySelectorAll('[role="tab"], button, a')].find((e) => e.textContent?.trim().startsWith(label))
        tab?.click()
      }, shot.click)
      await new Promise((r) => setTimeout(r, 800))
    }
    if (shot.scrollY) {
      await page.evaluate((y) => {
        const scrollers = [document.scrollingElement, ...document.querySelectorAll('main, [class*="main"]')]
        for (const el of scrollers) if (el && el.scrollHeight > el.clientHeight + 50) el.scrollTo(0, y)
        window.scrollTo(0, y)
      }, shot.scrollY)
    }
    await page.waitForNetworkIdle({ idleTime: 800, timeout: 30000 }).catch(() => {})
    await new Promise((r) => setTimeout(r, shot.settleMs ?? 1200))

    await page.screenshot({ path: join(rawDir, `${shot.name}.png`) })
    console.log(`Captured ${shot.name}`)
    await page.close()
  }
} finally {
  await browser.close()
}
