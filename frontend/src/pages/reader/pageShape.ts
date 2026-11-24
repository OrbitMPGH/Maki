/** A rectangle of a page, in fractions of its natural size. */
export interface Region {
  x: number
  y: number
  w: number
  h: number
}

export const FULL_REGION: Region = { x: 0, y: 0, w: 1, h: 1 }

export type PagePart = 'whole' | 'left' | 'right'

// Long edge of the copy the margins are measured on. At 160 a sample averaged a dozen source
// pixels, so thin lettering in the gutter faded to white and was cropped away.
const SAMPLE_EDGE = 400
const WHITE_AT = 228
const BLACK_AT = 28
// Samples kept back towards the margin on each side, so a soft edge of the art is never shaved.
const EDGE_PAD = 2
const MAX_CROP_SHARE = 0.45
const MIN_CROP_SHARE = 0.01
const CACHE_LIMIT = 500

const crops = new Map<string, Region | null>()
let canvas: HTMLCanvasElement | null = null

/**
 * The page minus its uniform white or black margins, or null when there is nothing worth trimming.
 * Measured on a small canvas copy of the loaded image, so it costs one downscaled draw per page
 * and is cached by URL. Pages are same-origin (and served from the offline cache under the same
 * URL), so the canvas is never tainted; if it is anyway, the page is shown untrimmed.
 */
export function cropRegion(image: HTMLImageElement): Region | null {
  const key = image.currentSrc || image.src
  const known = crops.get(key)
  if (known !== undefined) return known
  const region = measureMargins(image)
  if (crops.size >= CACHE_LIMIT) crops.delete(crops.keys().next().value as string)
  crops.set(key, region)
  return region
}

function measureMargins(image: HTMLImageElement): Region | null {
  const { naturalWidth: width, naturalHeight: height } = image
  if (!width || !height) return null
  const scale = Math.min(1, SAMPLE_EDGE / Math.max(width, height))
  const cw = Math.max(1, Math.round(width * scale))
  const ch = Math.max(1, Math.round(height * scale))

  canvas ??= document.createElement('canvas')
  canvas.width = cw
  canvas.height = ch
  const context = canvas.getContext('2d', { willReadFrequently: true })
  if (!context) return null
  // The default low quality samples a few source pixels per output pixel and can skip thin strokes.
  context.imageSmoothingEnabled = true
  context.imageSmoothingQuality = 'high'
  let data: Uint8ClampedArray
  try {
    context.drawImage(image, 0, 0, cw, ch)
    data = context.getImageData(0, 0, cw, ch).data
  } catch {
    return null
  }

  const luma = new Uint8Array(cw * ch)
  for (let i = 0; i < luma.length; i++) {
    luma[i] = (data[i * 4] * 299 + data[i * 4 + 1] * 587 + data[i * 4 + 2] * 114) / 1000
  }

  // The border colour is whichever of white and black owns most of the outer edge.
  let white = 0
  let black = 0
  let edge = 0
  const tally = (value: number) => {
    edge++
    if (value >= WHITE_AT) white++
    else if (value <= BLACK_AT) black++
  }
  for (let x = 0; x < cw; x++) {
    tally(luma[x])
    tally(luma[(ch - 1) * cw + x])
  }
  for (let y = 0; y < ch; y++) {
    tally(luma[y * cw])
    tally(luma[y * cw + cw - 1])
  }
  const isWhite = white >= black
  if ((isWhite ? white : black) < edge / 2) return null
  const blank = isWhite ? (value: number) => value >= WHITE_AT : (value: number) => value <= BLACK_AT

  // Content is any non-border sample with a non-border neighbour. Lone specks (dust, JPEG noise)
  // have none, while even one thin stroke of lettering spans a few samples.
  const ink = new Uint8Array(cw * ch)
  for (let i = 0; i < ink.length; i++) if (!blank(luma[i])) ink[i] = 1
  let top = ch
  let bottom = -1
  let left = cw
  let right = -1
  for (let y = 0; y < ch; y++) {
    for (let x = 0; x < cw; x++) {
      if (!ink[y * cw + x] || !hasInkNeighbour(ink, cw, ch, x, y)) continue
      if (y < top) top = y
      if (y > bottom) bottom = y
      if (x < left) left = x
      if (x > right) right = x
    }
  }
  if (bottom < 0) return null

  top = Math.max(0, top - EDGE_PAD)
  left = Math.max(0, left - EDGE_PAD)
  bottom = Math.min(ch - 1, bottom + EDGE_PAD)
  right = Math.min(cw - 1, right + EDGE_PAD)

  const region = { x: left / cw, y: top / ch, w: (right - left + 1) / cw, h: (bottom - top + 1) / ch }
  if (1 - region.w > MAX_CROP_SHARE || 1 - region.h > MAX_CROP_SHARE) return null
  if (1 - region.w < MIN_CROP_SHARE && 1 - region.h < MIN_CROP_SHARE) return null
  return region
}

function hasInkNeighbour(ink: Uint8Array, cw: number, ch: number, x: number, y: number): boolean {
  for (let dy = -1; dy <= 1; dy++) {
    const ny = y + dy
    if (ny < 0 || ny >= ch) continue
    for (let dx = -1; dx <= 1; dx++) {
      const nx = x + dx
      if ((dx || dy) && nx >= 0 && nx < cw && ink[ny * cw + nx]) return true
    }
  }
  return false
}

/** The crop, narrowed to one half of a wide page. The halves meet at the page's own centre, the spine. */
export function partOf(crop: Region | null, part: PagePart): Region {
  const base = crop ?? FULL_REGION
  if (part === 'whole') return base
  const end = base.x + base.w
  return part === 'left'
    ? { ...base, w: Math.max(0.01, 0.5 - base.x) }
    : { ...base, x: 0.5, w: Math.max(0.01, end - 0.5) }
}

export function isFull(region: Region): boolean {
  return region.x === 0 && region.y === 0 && region.w === 1 && region.h === 1
}

// Height over width at which a page counts as a webtoon slice. Print pages sit near 1.4 to 1.6.
const TALL_RATIO = 2
const PROBE_PAGES = 4
// Dimensions arrive with the image header, well before the pixels, so this waits for that and no
// longer. Past the deadline the guess is made from whatever reported in.
const PROBE_POLL_MS = 40
const PROBE_DEADLINE_MS = 2500

const guesses = new Map<string, boolean>()

/**
 * Whether a chapter looks like a webtoon: most of a few sampled pages are very tall. Reads only the
 * image headers of pages the reader is about to load anyway, starting at `from`.
 */
export function probeWebtoon(urls: string[], from: number, signal: AbortSignal): Promise<boolean> {
  const start = Math.max(0, Math.min(from, urls.length - PROBE_PAGES))
  const sample = urls.slice(start, start + PROBE_PAGES)
  const key = sample.join('|')
  const known = guesses.get(key)
  if (known !== undefined) return Promise.resolve(known)
  if (sample.length === 0) return Promise.resolve(false)

  return new Promise((resolve) => {
    const images = sample.map((src) => {
      const image = new Image()
      image.src = src
      return image
    })
    const startedAt = Date.now()
    const finish = (verdict: boolean, remember: boolean) => {
      clearInterval(timer)
      signal.removeEventListener('abort', abort)
      if (remember) guesses.set(key, verdict)
      resolve(verdict)
    }
    const check = () => {
      const sized = images.filter((image) => image.naturalWidth > 0)
      const settled = images.every((image) => image.naturalWidth > 0 || image.complete)
      const late = Date.now() - startedAt >= PROBE_DEADLINE_MS
      if (!settled && !late) return
      const tall = sized.filter((image) => image.naturalHeight / image.naturalWidth >= TALL_RATIO).length
      finish(sized.length > 0 && tall * 2 > sized.length, settled)
    }
    const abort = () => finish(false, false)
    signal.addEventListener('abort', abort)
    const timer = setInterval(check, PROBE_POLL_MS)
    check()
  })
}
