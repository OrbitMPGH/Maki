/**
 * A cover that fails to load becomes a tinted tile instead of the browser's broken-image icon.
 *
 * One capture-phase listener for the whole document rather than an `onError` on every `<img>`:
 * there are dozens of cover sites, and the usual reason they fail is the server going away, when
 * every one of them fails at once. The tile is the sign-in band's own shape (a brand gradient into
 * the page ground, its strength picked per cover so a grid of them is not one flat block).
 *
 * React leaves `src` alone until the prop itself changes, so the swap holds; a new URL for the
 * same element loads normally and falls back again if it too fails. Images that handle their own
 * failure (reader pages, the sign-in mosaic) opt out with `data-no-cover-fallback`.
 *
 * Imported for its side effect in main.tsx.
 */

const SHADES = [14, 22, 30, 38, 46, 54]

function shadeFor(src: string): number {
  let hash = 0
  for (let i = 0; i < src.length; i++) hash = (hash * 31 + src.charCodeAt(i)) | 0
  return SHADES[Math.abs(hash) % SHADES.length]
}

function tile(src: string): string {
  const root = getComputedStyle(document.documentElement)
  const brand = root.getPropertyValue('--brand').trim() || '#ee7fa4'
  const ground = root.getPropertyValue('--app-bg').trim() || '#0b0d13'
  const shade = shadeFor(src) / 100
  const svg =
    `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 2 3" preserveAspectRatio="none">` +
    `<defs><linearGradient id="g" x1="0.35" y1="0" x2="0.65" y2="1">` +
    `<stop offset="0" style="stop-color:${brand};stop-opacity:${shade}"/>` +
    `<stop offset="1" style="stop-color:${brand};stop-opacity:${shade / 3}"/>` +
    `</linearGradient></defs>` +
    `<rect width="2" height="3" style="fill:${ground}"/><rect width="2" height="3" fill="url(#g)"/></svg>`
  return `data:image/svg+xml,${encodeURIComponent(svg)}`
}

document.addEventListener(
  'error',
  (event) => {
    const img = event.target
    if (!(img instanceof HTMLImageElement)) return
    if (img.dataset.noCoverFallback !== undefined || img.src.startsWith('data:')) return
    img.removeAttribute('srcset')
    img.src = tile(img.src)
  },
  true,
)
