import { KnownDevices } from 'puppeteer-core'

const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

// Requests that are POSTs but only read: recommendation queries and page-conversion previews.
const READ_ONLY_POST = /^\/api\/v1\/(recommendations(\/(discover\/(feed|search|count|cohort)|creator))?|pageconversion\/(scope|samples\/row|estimate))$/

// Images and pages go straight through; only JSON is scrubbed and rewritten.
const BINARY = /\/(mediacover|covers?|pages?|thumbnails?|image)(\/|$)|\.(jpe?g|png|webp|avif|jxl|gif)$/i

const HIDE_NOISE = `
  .mantine-Notifications-root, [data-sonner-toaster], .star-nudge { display: none !important; }
  *, *::before, *::after { caret-color: transparent !important; }
  /* Scripted clicks move focus, which would leave focus rings in the shots. */
  :focus, :focus-visible { outline: none !important; }
`

/**
 * Opens a page for one shot: signed in with the API key, theme set before first paint, every
 * API write answered with an empty 204 and the shot's response rewrites applied.
 */
export async function openPage(browser, ctx, shot) {
  const page = await browser.newPage()
  const signedIn = !shot.signedOut
  // Account endpoints refuse any request that carries an API key, even next to a session cookie,
  // so the page signs in with the cookie alone and the key is only a fallback.
  const useCookie = signedIn && ctx.cookies?.length > 0
  if (useCookie) await page.setCookie(...ctx.cookies)
  else if (signedIn) await page.setExtraHTTPHeaders({ 'X-Api-Key': ctx.key })
  await page.setBypassServiceWorker(true)
  await page.emulateMediaFeatures([{ name: 'prefers-reduced-motion', value: 'reduce' }])

  // Shot rewrites first, so they win over the shared ones for the same endpoint.
  const rewrites = [...(shot.rewrites ?? []), ...ctx.rewrites]
  // Writes a shot fakes an answer for have to reach the interception below.
  const fakedWrites = rewrites.filter((r) => (r.method ?? 'GET') !== 'GET').map((r) => [r.method, r.match.source, r.match.flags])

  await page.evaluateOnNewDocument(
    (local, session, faked, readOnlySource) => {
      const readOnly = new RegExp(readOnlySource)
      for (const [k, v] of Object.entries(local)) localStorage.setItem(k, v)
      for (const [k, v] of Object.entries(session)) sessionStorage.setItem(k, v)
      // Kept so a shot can hand the app hub events that a JSON rewrite cannot fake.
      const RealSocket = window.WebSocket
      window.__shotSockets = []
      window.WebSocket = class extends RealSocket {
        constructor(...args) {
          super(...args)
          window.__shotSockets.push(this)
        }
      }
      // The reader's keepalive flush on close outlives request interception, so writes stop here too.
      const realFetch = window.fetch.bind(window)
      window.fetch = (input, init) => {
        const method = (init?.method ?? (input instanceof Request ? input.method : 'GET')).toUpperCase()
        const url = new URL(input instanceof Request ? input.url : String(input), location.href)
        const write = method !== 'GET' && method !== 'HEAD' && url.pathname.startsWith('/api/') && !(method === 'POST' && readOnly.test(url.pathname))
        const fakedHere = faked.some(([m, src, flags]) => m === method && new RegExp(src, flags).test(url.pathname + url.search))
        return write && !fakedHere ? Promise.resolve(new Response(null, { status: 204 })) : realFetch(input, init)
      }
    },
    { ...ctx.localStorage, 'maki-background': shot.background ?? ctx.background, 'maki-accent': shot.accent ?? ctx.accent, ...(shot.localStorage ?? {}) },
    shot.sessionStorage ?? {},
    fakedWrites,
    READ_ONLY_POST.source,
  )

  const cookieHeader = signedIn ? (ctx.cookies ?? []).map((c) => `${c.name}=${c.value}`).join('; ') : ''
  const upstreamHeaders = useCookie ? { Cookie: cookieHeader } : signedIn ? { 'X-Api-Key': ctx.key } : {}
  await page.setRequestInterception(true)
  page.on('request', async (req) => {
    const url = new URL(req.url())
    const method = req.method()
    const isApi = url.pathname.startsWith('/api/')
    const rewrite = rewrites.find((r) => (r.method ?? 'GET') === method && r.match.test(url.pathname + url.search))
    if (rewrite?.respond) {
      // Made up entirely: for writes, and for answers the showcase server cannot give.
      try {
        let sent
        try {
          sent = req.postData() ? JSON.parse(req.postData()) : undefined
        } catch {
          sent = req.postData()
        }
        const body = await rewrite.respond({ url, method, body: sent, ...ctx.data })
        return req.respond({ status: rewrite.status ?? 200, contentType: 'application/json', body: JSON.stringify(body ?? null) })
      } catch (err) {
        console.warn(`  faked ${method} ${url.pathname} failed: ${err.message}`)
        return req.respond({ status: 500, body: '' })
      }
    }
    if (isApi && method !== 'GET' && method !== 'HEAD' && !(method === 'POST' && READ_ONLY_POST.test(url.pathname))) {
      // A 204 rather than an abort: an aborted fetch reads as offline to the PWA build.
      return req.respond({ status: 204, body: '' })
    }
    if (method !== 'GET' || (!isApi && !rewrite) || BINARY.test(url.pathname)) return req.continue()
    try {
      const res = await fetch(req.url(), { headers: upstreamHeaders })
      if (!(res.headers.get('content-type') ?? '').includes('json')) {
        return req.respond({ status: res.status, contentType: res.headers.get('content-type') ?? undefined, body: Buffer.from(await res.arrayBuffer()) })
      }
      let text = await res.text()
      // The showcase lives under the repo, so its paths would show the machine's user folder.
      for (const [from, to] of ctx.scrub) text = text.split(from).join(to)
      if (!rewrite || !res.ok) return req.respond({ status: res.status, contentType: 'application/json', body: text })
      const body = await rewrite.apply(JSON.parse(text), { url, ...ctx.data })
      await req.respond({ status: 200, contentType: 'application/json', body: JSON.stringify(body) })
    } catch (err) {
      console.warn(`  rewrite of ${url.pathname} failed: ${err.message}`)
      req.continue()
    }
  })

  if (shot.device === 'phone') await page.emulate(KnownDevices['iPhone 13'])
  else if (shot.device === 'tablet') await page.emulate(KnownDevices['iPad Pro 11'])
  else await page.setViewport({ width: 1600, height: 1000, ...(shot.viewport ?? {}), deviceScaleFactor: ctx.scale })
  return page
}

async function settle(page, ms = 800) {
  await page.waitForNetworkIdle({ idleTime: 600, timeout: 20000 }).catch(() => {})
  await sleep(ms)
}

// Runs in the page: finds the element (or union of elements) a locator names.
function locateInPage(spec) {
  const visible = (e) => {
    const r = e.getBoundingClientRect()
    return r.width > 0 && r.height > 0
  }
  const text = (e) => (e.textContent ?? '').replace(/\s+/g, ' ').trim()
  const headings = (scope = document) => [...scope.querySelectorAll('h1,h2,h3,h4,h5,h6,[role=heading]')].filter(visible)
  const byHeading = (label, scope) => {
    const all = headings(scope)
    return all.find((h) => text(h) === label) ?? all.find((h) => text(h).startsWith(label))
  }
  const within = spec.within ? document.querySelector(spec.within) ?? document : document

  let els = []
  if (spec.css) {
    els = [...within.querySelectorAll(spec.css)].filter(visible)
    if (spec.last) els = els.slice(-1)
    else if (spec.nth !== undefined) els = els.slice(spec.nth, spec.nth + 1)
    else if (!spec.all) els = els.slice(0, 1)
  } else if (spec.panel) {
    const h = byHeading(spec.panel, within)
    const card = h?.closest(spec.closest ?? '.panel, [data-with-border], .mantine-Paper-root, .mantine-Card-root')
    if (card) els = [card]
  } else if (spec.rail) {
    // A section header and whatever follows it up to the next header.
    const h = byHeading(spec.rail, within)
    let head = h?.closest('.section-header')
    if (head) {
      els = [head]
      let next = head.nextElementSibling
      const max = spec.blocks ?? 8
      for (let i = 0; next && i < max && !next.classList.contains('section-header') && !next.querySelector(':scope > .section-header'); i++) {
        els.push(next)
        next = next.nextElementSibling
      }
    }
  } else if (spec.heading) {
    // A heading inside a card and its following siblings up to the next heading of the same kind.
    const h = byHeading(spec.heading, within)
    if (h) {
      els = [h]
      let next = h.nextElementSibling
      while (next && next.tagName !== h.tagName && !(next.querySelector && next.querySelector(h.tagName))) {
        els.push(next)
        next = next.nextElementSibling
      }
    }
  } else if (spec.rows) {
    // A slice of a table: its header row plus the rows from one label to another.
    const table = within.querySelector(spec.rows.table ?? 'table')
    // The chapter cell runs straight into its badges, so a label ends where its number does.
    const label = (n) => new RegExp(`${n.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}(?![\\d.])`)
    const rows = table ? [...table.querySelectorAll('tbody tr')].filter(visible) : []
    const from = rows.findIndex((r) => label(spec.rows.from).test(text(r)))
    const to = rows.findIndex((r) => label(spec.rows.to).test(text(r)))
    if (from >= 0 && to >= from) {
      // Hidden rather than cropped around, so the header sits right above the slice.
      rows.forEach((r, i) => {
        if (i < from || i > to) r.style.display = 'none'
      })
      const head = table.querySelector('thead')
      els = [...(head ? [head] : []), ...rows.slice(from, to + 1)]
    }
  } else if (spec.text) {
    const all = [...within.querySelectorAll(spec.tag ?? '*')].filter((e) => visible(e) && text(e).startsWith(spec.text))
    const leaf = all.filter((e) => !all.some((o) => o !== e && e.contains(o)))
    const hit = leaf[0]
    const box = hit && spec.closest ? hit.closest(spec.closest) : hit
    if (box) els = [box]
  }
  els = els.filter(Boolean)
  if (els.length === 0) return null
  const rects = els.map((e) => e.getBoundingClientRect())
  const x = Math.min(...rects.map((r) => r.left))
  const y = Math.min(...rects.map((r) => r.top))
  const right = Math.max(...rects.map((r) => r.right))
  const bottom = Math.max(...rects.map((r) => r.bottom))
  return { x: x + scrollX, y: y + scrollY, width: right - x, height: bottom - y }
}

export async function locate(page, spec) {
  return page.evaluate(locateInPage, spec)
}

async function clickTarget(page, target) {
  const spec = typeof target === 'string' ? { text: target, tag: 'button, a, [role=tab], [role=button], [role=menuitem], label' } : target
  const ok = await page.evaluate((spec) => {
    const visible = (e) => {
      const r = e.getBoundingClientRect()
      return r.width > 0 && r.height > 0
    }
    let el
    const label = (e) => (e.textContent ?? '').replace(/\s+/g, ' ').trim()
    if (spec.css) el = [...document.querySelectorAll(spec.css)].filter((e) => visible(e) && (!spec.text || label(e).startsWith(spec.text)))[spec.nth ?? 0]
    else if (spec.label) el = [...document.querySelectorAll(`[aria-label="${spec.label}"], [title="${spec.label}"]`)].filter(visible)[spec.nth ?? 0]
    else {
      const all = [...document.querySelectorAll(spec.tag ?? '*')].filter((e) => visible(e) && (e.textContent ?? '').replace(/\s+/g, ' ').trim().startsWith(spec.text))
      el = all[spec.nth ?? 0]
    }
    if (!el) return false
    el.scrollIntoView({ block: 'center' })
    if (spec.real) {
      const r = el.getBoundingClientRect()
      return { x: r.left + r.width / 2, y: r.top + r.height / 2 }
    }
    el.click()
    return true
  }, spec)
  if (!ok) throw new Error(`nothing to click for ${JSON.stringify(spec)}`)
  // Some targets only answer a real pointer (a tooltip-wrapped popover trigger).
  if (typeof ok === 'object') await page.mouse.click(ok.x, ok.y)
}

async function runStep(page, step) {
  if (step.click) await clickTarget(page, step.click)
  else if (step.hover) {
    const el = await page.waitForSelector(step.hover, { visible: true, timeout: 10000 })
    await el.hover()
  } else if (step.key) await page.keyboard.press(step.key)
  else if (step.upload) {
    const input = await page.waitForSelector(step.into, { timeout: 10000 })
    await input.uploadFile(step.upload)
  } else if (step.type) {
    const el = await page.waitForSelector(step.into, { visible: true, timeout: 10000 })
    await el.click()
    await page.keyboard.type(step.type, { delay: 20 })
  }
  else if (step.tap) {
    const { width, height } = page.viewport()
    await page.mouse.click(width * step.tap[0], height * step.tap[1])
  } else if (step.hub) {
    // SignalR JSON protocol: one invocation record per event, each ended by 0x1E.
    const frames = step.hub.map((e) => JSON.stringify({ type: 1, target: e.target, arguments: e.arguments }) + '\x1e').join('')
    const sent = await page.evaluate((data) => {
      const open = (window.__shotSockets ?? []).filter((s) => s.url.includes('/signalr/') && s.readyState === 1)
      for (const s of open) s.dispatchEvent(new MessageEvent('message', { data }))
      return open.length
    }, frames)
    if (!sent) throw new Error('the SignalR hub is not connected, so hub events cannot be injected')
  }
  else if (step.waitFor) await page.waitForSelector(step.waitFor, { visible: true, timeout: step.timeout ?? 15000 })
  else if (step.wait) await sleep(step.wait)
  else if (step.scroll) await page.evaluate((y) => window.scrollTo(0, y), step.scroll)
  else if (step.eval) await page.evaluate(step.eval)
  await settle(page, step.settle ?? 500)
}

// Scrolls once through the page so lazy covers and rails load.
async function warm(page) {
  await page.evaluate(async () => {
    for (let y = 0; y < document.documentElement.scrollHeight; y += 700) {
      window.scrollTo(0, y)
      await new Promise((r) => setTimeout(r, 90))
    }
    window.scrollTo(0, 0)
  })
}

const MAX_CSS_HEIGHT = 7800

export async function capture(page, ctx, shot, file) {
  await page.goto(ctx.base + shot.path, { waitUntil: 'networkidle2', timeout: 60000 })
  if (!shot.signedOut && /^\/(login|setup)/.test(new URL(page.url()).pathname)) {
    throw new Error(`landed on ${page.url()}. Is the API key valid and Full scope?`)
  }
  await page.addStyleTag({ content: HIDE_NOISE + (shot.css ?? '') })
  if (shot.warm !== false && !shot.device) await warm(page)
  await settle(page, 600)
  for (const step of shot.steps ?? []) await runStep(page, step)
  await settle(page, shot.settleMs ?? 900)

  const what = shot.capture ?? 'viewport'
  if (what === 'viewport') return page.screenshot({ path: file })
  const viewport = page.viewport()
  if (what === 'full') {
    // A viewport as tall as the page, so lazy rails and covers below the fold mount too.
    for (let i = 0; i < 3; i++) {
      const height = Math.min(await page.evaluate(() => document.documentElement.scrollHeight), MAX_CSS_HEIGHT)
      if (height === page.viewport().height) break
      await page.setViewport({ ...viewport, height })
      await settle(page, 900)
    }
    await page.evaluate(() => window.scrollTo(0, 0))
    return page.screenshot({ path: file })
  }

  const pad = shot.pad ?? 16
  let rect = await locate(page, what)
  if (!rect) throw new Error(`could not find ${JSON.stringify(what)}`)
  // The top-bar layout keeps a fixed header over the page; crops sit below it.
  const headerBottom = await page.evaluate(() => {
    const h = document.querySelector('.mantine-AppShell-header')
    if (!h) return 0
    const s = getComputedStyle(h)
    return (s.position === 'fixed' || s.position === 'sticky') && s.display !== 'none' ? h.getBoundingClientRect().bottom : 0
  })
  // Some pages keep a closed popover mounted, so only a visible overlay counts.
  const inOverlay = await page.evaluate(() =>
    [...document.querySelectorAll('.mantine-Modal-content, .mantine-Popover-dropdown, .mantine-Menu-dropdown, .reader-settings')].some((e) => {
      const r = e.getBoundingClientRect()
      const st = getComputedStyle(e)
      return r.width > 0 && r.height > 0 && st.display !== 'none' && st.visibility !== 'hidden' && Number(st.opacity) > 0
    }),
  )
  const top = inOverlay ? 0 : headerBottom
  const needed = Math.min(Math.ceil(rect.height + 2 * pad + top), MAX_CSS_HEIGHT)
  if (needed > viewport.height) {
    await page.setViewport({ ...viewport, height: needed })
    await settle(page, 600)
  }
  rect = await locate(page, what)
  // Popovers hide once their trigger leaves the screen, so leave anything already in view alone.
  const inView = await page.evaluate((r, pad, top) => r.y - pad >= scrollY + top && r.y + r.height + pad <= scrollY + innerHeight, rect, pad, top)
  if (!inView) {
    await page.evaluate((y) => window.scrollTo(0, Math.max(0, y)), rect.y - pad - top)
    await settle(page, 400)
    rect = await locate(page, what)
  }
  if (!rect) throw new Error(`lost ${JSON.stringify(what)} after scrolling`)
  const vw = page.viewport().width
  const x = Math.max(0, rect.x - pad)
  const clip = {
    x,
    y: Math.max(0, rect.y - pad),
    width: Math.min(vw, rect.x + rect.width + pad) - x,
    height: Math.min(rect.height + 2 * pad, page.viewport().height),
  }
  return page.screenshot({ path: file, clip, captureBeyondViewport: false })
}
