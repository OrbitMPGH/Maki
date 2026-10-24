import { http, HttpResponse } from 'msw'
import { api } from './api'

// Image routes only, with no app imports: the Storybook dev server also loads this in Node
// (mockImages in main.ts) to answer <img> requests when the page mocks without a Service Worker.

// Deterministic per seed, so a series keeps its colour everywhere it appears.
// Golden-angle steps spread consecutive ids (1, 2, 3...) across the wheel instead of clustering.
function hue(seed: string): number {
  let h = 0
  for (const c of seed) h = (h * 31 + c.charCodeAt(0)) >>> 0
  return Math.round((h * 137.508) % 360)
}

function escapeXml(s: string): string {
  return s.replace(/[<>&"']/g, (c) => `&#${c.charCodeAt(0)};`)
}

function svg(body: string, w: number, h: number) {
  return new HttpResponse(
    `<svg xmlns="http://www.w3.org/2000/svg" width="${w}" height="${h}" viewBox="0 0 ${w} ${h}">${body}</svg>`,
    { headers: { 'Content-Type': 'image/svg+xml' } },
  )
}

// Abstract art only: every surface that shows a cover already prints the title beside or over it.
function coverSvg(seed: string) {
  const h = hue(seed)
  const n = h % 3
  const figure =
    n === 0
      ? `<circle cx="210" cy="140" r="78" fill="hsl(${(h + 180) % 360},70%,72%)" opacity=".45"/>`
      : n === 1
        ? `<path d="M0 330 L120 210 L200 280 L300 170 L300 450 L0 450 Z" fill="hsl(${(h + 30) % 360},45%,16%)" opacity=".7"/><circle cx="80" cy="110" r="36" fill="hsl(${(h + 60) % 360},80%,80%)" opacity=".6"/>`
        : `<rect x="40" y="60" width="220" height="220" rx="110" fill="none" stroke="hsl(${(h + 160) % 360},70%,75%)" stroke-width="14" opacity=".5"/>`
  return svg(
    `<defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="hsl(${h},55%,50%)"/><stop offset="1" stop-color="hsl(${(h + 50) % 360},60%,20%)"/></linearGradient></defs>` +
      `<rect width="300" height="450" fill="url(#g)"/>${figure}<rect y="330" width="300" height="120" fill="#000" opacity=".25"/>`,
    300,
    450,
  )
}

/**
 * The server's sign-in mosaic: 24 covers on an 8-column grid with transparent gaps, blurred as one
 * image (SignInMosaicService), so the page treats it the same as the real WebP.
 */
function mosaicSvg() {
  const [w, h, gap, cols] = [200, 300, 20, 8]
  const tiles = Array.from({ length: 24 }, (_, i) => {
    const hu = hue(String(i + 1))
    const [x, y] = [(i % cols) * (w + gap), Math.floor(i / cols) * (h + gap)]
    return `<rect x="${x}" y="${y}" width="${w}" height="${h}" fill="hsl(${hu},55%,42%)"/><circle cx="${x + 116}" cy="${y + 94}" r="50" fill="hsl(${(hu + 180) % 360},70%,72%)" opacity=".5"/>`
  }).join('')
  return svg(
    `<defs><filter id="b" x="-5%" y="-5%" width="110%" height="110%"><feGaussianBlur stdDeviation="5"/></filter></defs><g filter="url(#b)">${tiles}</g>`,
    cols * w + (cols - 1) * gap,
    3 * h + 2 * gap,
  )
}

function pageSvg(seed: string, n: string, titleFor: (seed: string) => string) {
  const h = hue(seed)
  const panels = [
    [30, 30, 740, 360],
    [30, 410, 360, 420],
    [410, 410, 360, 420],
    [30, 850, 740, 320],
  ]
    .map(([x, y, w, ht], i) => `<rect x="${x}" y="${y}" width="${w}" height="${ht}" fill="hsl(${(h + i * 25) % 360},25%,${88 - i * 6}%)" stroke="#111" stroke-width="4"/>`)
    .join('')
  return svg(
    `<rect width="800" height="1200" fill="#fafafa"/>${panels}<text x="400" y="1185" font-family="sans-serif" font-size="22" text-anchor="middle" fill="#555">${escapeXml(titleFor(seed))} - ${escapeXml(n)}</text>`,
    800,
    1200,
  )
}

function spreadSvg(seed: string, n: string, titleFor: (seed: string) => string) {
  const h = hue(seed)
  const half = (x: number, label: string, i: number) =>
    `<rect x="${x + 30}" y="30" width="740" height="1140" fill="hsl(${(h + i * 40) % 360},30%,82%)" stroke="#111" stroke-width="4"/>` +
    `<text x="${x + 400}" y="620" font-family="sans-serif" font-size="64" text-anchor="middle" fill="#222">${label}</text>`
  return svg(
    `<rect width="1600" height="1200" fill="#fafafa"/>${half(800, 'Read first', 0)}${half(0, 'Read second', 1)}<text x="800" y="1185" font-family="sans-serif" font-size="22" text-anchor="middle" fill="#555">${escapeXml(titleFor(seed))} - ${escapeXml(n)}</text>`,
    1600,
    1200,
  )
}

function stripSvg(seed: string, n: string, titleFor: (seed: string) => string) {
  const h = hue(seed)
  return svg(
    `<rect width="800" height="2400" fill="hsl(${h},20%,92%)"/><rect x="60" y="200" width="680" height="900" fill="hsl(${h},35%,70%)"/><rect x="60" y="1300" width="680" height="900" fill="hsl(${(h + 30) % 360},35%,60%)"/><text x="400" y="2360" font-family="sans-serif" font-size="28" text-anchor="middle" fill="#333">${escapeXml(titleFor(seed))} - ${escapeXml(n)}</text>`,
    800,
    2400,
  )
}

function bannerSvg(seed: string) {
  const h = hue(seed)
  return svg(
    `<defs><linearGradient id="b" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="hsl(${h},50%,30%)"/><stop offset="1" stop-color="hsl(${(h + 70) % 360},55%,18%)"/></linearGradient></defs><rect width="1600" height="600" fill="url(#b)"/>`,
    1600,
    600,
  )
}

/** `titleFor` turns a seed into the caption printed on generated pages (a series id into its title). */
export const imageHandlers = (titleFor: (seed: string) => string = (seed) => seed) => [
  // The real cover route, for any fixture that carries a server-shaped coverUrl.
  http.get(api('/mediacover/:id/:file'), ({ params }) => coverSvg(String(params.id))),
  http.get('/mock/cover/:seed', ({ params }) => coverSvg(String(params.seed).replace(/\.svg$/, ''))),
  http.get(api('/signin/mosaic.webp'), () => mosaicSvg()),
  http.get('/mock/page/:seed/:n', ({ params }) => pageSvg(String(params.seed), String(params.n).replace(/\.svg$/, ''), titleFor)),
  http.get('/mock/spread/:seed/:n', ({ params }) => spreadSvg(String(params.seed), String(params.n).replace(/\.svg$/, ''), titleFor)),
  http.get('/mock/strip/:seed/:n', ({ params }) => stripSvg(String(params.seed), String(params.n).replace(/\.svg$/, ''), titleFor)),
  http.get('/mock/banner/:seed', ({ params }) => bannerSvg(String(params.seed).replace(/\.svg$/, ''))),
  http.get(api('/reader/chapter/:id/page/:n'), ({ params }) => pageSvg(String(params.id), String(params.n), titleFor)),
  http.get(api('/reader/chapter/:id/thumb/:n'), ({ params }) => pageSvg(String(params.id), String(params.n), titleFor)),
]
