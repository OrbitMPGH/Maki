import { execSync } from 'node:child_process'
import { existsSync, readFileSync, writeFileSync } from 'node:fs'
import { mkdir, rm } from 'node:fs/promises'
import { dirname, join, relative, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import puppeteer from 'puppeteer-core'
import { findChrome } from '../readme-images/chrome.mjs'
import { MakiClient } from '../showcase/client.mjs'
import { capture, openPage } from './lib/browser.mjs'
import { markRunEnded, prepare } from './lib/prepare.mjs'
import { build, isUp, start } from './lib/server.mjs'
import { CHAPTERS, DEFAULTS, REWRITES, SERIES, SHOTS, STATES } from './manifest.mjs'

const here = dirname(fileURLToPath(import.meta.url))
const repo = resolve(here, '..', '..')

const argv = process.argv.slice(2)
const flag = (name) => argv.includes(name)
const option = (name) => {
  const i = argv.indexOf(name)
  return i >= 0 ? argv[i + 1] : undefined
}
const filters = argv.filter((a, i) => !a.startsWith('--') && !(i > 0 && ['--out', '--config'].includes(argv[i - 1])))

if (flag('--help')) {
  console.log(`node shots.mjs [filter...] [--list] [--no-build] [--no-prepare] [--running] [--out dir] [--config dir]

  filter      shot names or prefixes to take, e.g. series/ reader/settings-panel
  --list      print the shot names and exit
  --no-build  skip dotnet build
  --no-prepare  leave the database alone (no date shift, no staged states)
  --running   use a backend that is already up on MAKI_URL instead of starting one
  --out       output folder (default scripts/doc-shots/out)
  --config    showcase config folder (default .screenshots)`)
  process.exit(0)
}

const wanted = SHOTS.filter((s) => filters.length === 0 || filters.some((f) => s.name === f || s.name.startsWith(f)))
if (flag('--list')) {
  for (const s of SHOTS) console.log(s.name)
  process.exit(0)
}
if (wanted.length === 0) {
  console.error(`No shot matches ${filters.join(', ')}. Run with --list to see them.`)
  process.exit(1)
}

const configDir = resolve(repo, option('--config') ?? '.screenshots')
// Never point this at a real library: the prepare step rewrites dates and chapter states.
if (!existsSync(join(configDir, '.showcase'))) {
  console.error(`${configDir} is not a showcase config (no .showcase marker). See scripts/showcase/README.md.`)
  process.exit(1)
}
const credentials = JSON.parse(readFileSync(join(configDir, 'credentials.json'), 'utf8'))
const port = JSON.parse(readFileSync(join(configDir, 'config.json'), 'utf8')).Port ?? 8994
const base = (process.env.MAKI_URL ?? `http://localhost:${port}`).replace(/\/$/, '')
const outDir = resolve(option('--out') ?? join(here, 'out'))
const sharedDumpDir =
  process.env.MAKI_SHARED_DUMP_DIR ?? (existsSync(join(configDir, 'mangabaka.db')) ? undefined : join(repo, '.simulated'))

function releaseVersion() {
  if (process.env.MAKI_VERSION) return process.env.MAKI_VERSION
  try {
    return execSync('git describe --tags --abbrev=0 origin/main', { cwd: repo, stdio: ['ignore', 'pipe', 'ignore'] }).toString().trim()
  } catch {
    return undefined
  }
}

// Local folders as they would read in Docker, longest first so the library wins over its parent.
function scrubPairs() {
  const pairs = [
    [join(configDir, 'library'), '/manga'],
    [configDir, '/config'],
    [repo, '/app'],
  ]
  const out = []
  for (const [from, to] of pairs) {
    out.push([JSON.stringify(from).slice(1, -1), to], [from.replaceAll('\\', '/'), to])
  }
  return out
}

async function api(path) {
  const res = await fetch(`${base}/api/v1${path}`, { headers: { 'X-Api-Key': credentials.apiKey } })
  if (!res.ok) throw new Error(`GET ${path} returned ${res.status}`)
  return res.json()
}

let server
const useRunning = flag('--running')
if (!useRunning && (await isUp(base))) {
  console.error(`Something already answers on ${base}. Stop it, or pass --running to use it as is.`)
  process.exit(1)
}

if (!useRunning) {
  if (!flag('--no-build')) build(repo)
  if (!flag('--no-prepare')) {
    const r = prepare(configDir, STATES)
    console.log(`Moved ${r.shifted} timestamps forward by ${r.days.toFixed(1)} days; ${r.log.join('; ')}.`)
  }
  await mkdir(outDir, { recursive: true })
  console.log(`Starting the backend on ${base} against ${relative(repo, configDir)}...`)
  server = await start({ repo, configDir, sharedDumpDir, base, logPath: join(here, 'backend.log') })
}

const failed = []
const taken = []
try {
  const library = await api('/series')
  const series = {}
  for (const [key, title] of Object.entries(SERIES)) {
    const hit = library.find((s) => s.title.toLowerCase().includes(title.toLowerCase()) || s.displayTitle.toLowerCase().includes(title.toLowerCase()))
    if (!hit) throw new Error(`The showcase library has no series like "${title}" (SERIES.${key} in manifest.mjs).`)
    series[key] = hit.id
  }
  const chapters = {}
  for (const [key, { series: s, number }] of Object.entries(CHAPTERS)) {
    const list = await api(`/chapter?seriesId=${series[s]}`)
    const hit = list.find((c) => Number(c.number) === number)
    if (!hit) throw new Error(`${SERIES[s]} has no chapter ${number} (CHAPTERS.${key} in manifest.mjs).`)
    chapters[key] = hit.id
  }
  const data = { series, chapters, version: releaseVersion(), api }

  // A cookie session as well as the key: the SignalR hub and the account endpoints refuse API keys.
  const client = new MakiClient(base)
  await client.post('/auth/login', { username: credentials.username, password: credentials.password })
  const cookies = [...client.cookies].map(([name, value]) => ({ name, value, domain: new URL(base).hostname, path: '/' }))

  const ctx = {
    base,
    key: credentials.apiKey,
    scale: DEFAULTS.scale,
    background: DEFAULTS.background,
    accent: DEFAULTS.accent,
    localStorage: DEFAULTS.localStorage,
    cookies,
    scrub: scrubPairs(),
    rewrites: REWRITES,
    data,
  }

  if (filters.length === 0) await rm(outDir, { recursive: true, force: true }).catch(() => {})
  const browser = await puppeteer.launch({ executablePath: findChrome(), headless: true, args: ['--hide-scrollbars'] })
  try {
    for (const shot of wanted) {
      const file = join(outDir, `${shot.name}.png`)
      await mkdir(dirname(file), { recursive: true })
      const resolved = {
        ...shot,
        path: typeof shot.path === 'function' ? shot.path(data) : shot.path,
        steps: typeof shot.steps === 'function' ? shot.steps(data) : shot.steps,
      }
      const page = await openPage(browser, ctx, resolved)
      try {
        await capture(page, ctx, resolved, file)
        taken.push(shot)
        console.log(`ok    ${shot.name}`)
      } catch (err) {
        failed.push({ name: shot.name, error: err.message })
        console.log(`FAIL  ${shot.name}: ${err.message}`)
        // What the page looked like when the shot gave up, for tuning its steps.
        const debug = join(outDir, '_failed', `${shot.name}.png`)
        await mkdir(dirname(debug), { recursive: true })
        await page.screenshot({ path: debug }).catch(() => {})
      } finally {
        await page.close()
      }
    }
  } finally {
    await browser.close()
  }
} finally {
  if (server) {
    await server.stop()
    markRunEnded(configDir)
  }
}

writeIndex()
console.log(`\n${taken.length} shots in ${outDir}${failed.length ? `, ${failed.length} failed` : ''}. Open index.html to look through them.`)
if (failed.length) process.exitCode = 1

// A contact sheet of every shot, grouped by folder, for picking images for the docs.
function writeIndex() {
  const groups = new Map()
  for (const s of SHOTS) {
    if (!existsSync(join(outDir, `${s.name}.png`))) continue
    const g = s.name.includes('/') ? s.name.split('/')[0] : 'misc'
    if (!groups.has(g)) groups.set(g, [])
    groups.get(g).push(s)
  }
  const esc = (t) => String(t ?? '').replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c])
  const sections = [...groups]
    .map(
      ([g, shots]) => `<h2>${esc(g)}</h2><div class="grid">${shots
        .map((s) => `<figure><a href="${esc(s.name)}.png"><img loading="lazy" src="${esc(s.name)}.png" alt=""></a><figcaption><code>${esc(s.name)}</code>${s.note ? `<br>${esc(s.note)}` : ''}</figcaption></figure>`)
        .join('')}</div>`,
    )
    .join('')
  const failures = failed.length ? `<h2>Failed</h2><ul>${failed.map((f) => `<li><code>${esc(f.name)}</code>: ${esc(f.error)}</li>`).join('')}</ul>` : ''
  writeFileSync(
    join(outDir, 'index.html'),
    `<!doctype html><meta charset="utf-8"><title>Maki doc shots</title>
<style>body{font:14px system-ui;background:#16151a;color:#ddd;margin:24px}h2{margin:32px 0 12px;text-transform:capitalize}
.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(360px,1fr));gap:16px}figure{margin:0;background:#211f26;border-radius:8px;padding:8px}
img{width:100%;height:240px;object-fit:contain;object-position:top;background:#0d0c10;border-radius:4px}figcaption{padding:6px 2px 0;color:#aaa}code{color:#f3a6c4}</style>
<h1>Maki doc shots</h1><p>${new Date().toISOString().slice(0, 16).replace('T', ' ')} UTC</p>${failures}${sections}`,
  )
}
