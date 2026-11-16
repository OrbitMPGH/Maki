import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { BACKGROUNDS } from './prefs'

type Rgb = [number, number, number]

// Same small copy the crop measures on: it averages away screentone and JPEG noise, and keeps the
// scan to a few thousand pixels.
const SAMPLE_EDGE = 96
// How deep the sampled frame is, in pixels of that copy.
const RING = 2
const CACHE_LIMIT = 500
const LIGHT_AT = 0.5
const SEEN_LIMIT = 6

// By page URL: the image in the DOM may be the reader's in-memory copy, whose URL is not the page's.
const edges = new Map<string, Rgb | null>()
let canvas: HTMLCanvasElement | null = null
// The last colour shown, so a reader opening on a light page does not start on the dark default and
// fade over. Per tab; a reload starts from what the previous visit ended on.
const LAST_KEY = 'reader.edgeBackground'

function remembered(): Rgb | null {
  try {
    const raw = sessionStorage.getItem(LAST_KEY)
    const parts = raw?.split(',').map(Number)
    return parts?.length === 3 && parts.every((n) => n >= 0 && n <= 255) ? (parts as Rgb) : null
  } catch {
    return null
  }
}

function remember(rgb: Rgb) {
  try {
    sessionStorage.setItem(LAST_KEY, rgb.join(','))
  } catch {
    // Remembering is a nicety.
  }
}

const median = (values: number[]) => values.sort((a, b) => a - b)[values.length >> 1]

/**
 * The colour a page's outer edge is, or null when it cannot be read. The median of the frame, per
 * channel, so a speck of art touching the border does not tint it.
 */
function measureEdge(image: HTMLImageElement): Rgb | null {
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
  let data: Uint8ClampedArray
  try {
    context.drawImage(image, 0, 0, cw, ch)
    data = context.getImageData(0, 0, cw, ch).data
  } catch {
    return null
  }
  const reds: number[] = []
  const greens: number[] = []
  const blues: number[] = []
  for (let y = 0; y < ch; y++) {
    const onFrame = y < RING || y >= ch - RING
    for (let x = 0; x < cw; x++) {
      if (!onFrame && x >= RING && x < cw - RING) {
        x = cw - RING - 1
        continue
      }
      const i = (y * cw + x) * 4
      reds.push(data[i])
      greens.push(data[i + 1])
      blues.push(data[i + 2])
    }
  }
  if (reds.length === 0) return null
  return [median(reds), median(greens), median(blues)]
}

/** Perceived brightness from 0 to 1. */
function lightness([r, g, b]: Rgb) {
  return (r * 299 + g * 587 + b * 114) / 255000
}

const css = (rgb: Rgb) => `rgb(${rgb.join(', ')})`
const hexLightness = (hex: string) => {
  const n = parseInt(hex.slice(1), 16)
  return lightness([(n >> 16) & 255, (n >> 8) & 255, n & 255])
}

export interface ReaderGround {
  /** The CSS background for the reader's root. */
  background: string
  /** The ground is light enough that dark-on-light fixes apply to what sits straight on it. */
  light: boolean
  /** Reports a page that has loaded, so the ground can follow it. */
  note: (index: number, image: HTMLImageElement) => void
}

/**
 * What the reader's root is painted with. A fixed background passes through; Auto follows the
 * edge colour of the pages on screen (two pages of a spread are averaged), and holds the previous
 * colour while a page is still loading so a turn never blinks to the default.
 */
export function useReaderGround(
  background: string,
  urls: string[],
  page: number,
  shownTo: number,
  continuous: boolean,
): ReaderGround {
  const auto = background === BACKGROUNDS.auto
  const [version, setVersion] = useState(0)
  const [held, setHeld] = useState<Rgb | null>(remembered)
  const seen = useRef(new Map<number, HTMLImageElement>())

  const note = useCallback(
    (index: number, image: HTMLImageElement) => {
      const src = urls[index]
      // Kept even when Auto is off, so switching to it measures the page already on screen.
      seen.current.delete(index)
      seen.current.set(index, image)
      if (seen.current.size > SEEN_LIMIT) seen.current.delete(seen.current.keys().next().value as number)
      if (!auto || !src || edges.has(src)) return
      if (edges.size >= CACHE_LIMIT) edges.delete(edges.keys().next().value as string)
      edges.set(src, measureEdge(image))
      setVersion((v) => v + 1)
    },
    [auto, urls],
  )

  useEffect(() => {
    if (!auto) return
    for (let i = page; i <= shownTo; i++) {
      const image = seen.current.get(i)
      if (image) note(i, image)
    }
  }, [auto, page, shownTo, note])

  // The strip draws its own images, tagged with their page; the paged view reports through `note`.
  useEffect(() => {
    if (!auto || !continuous) return
    const sample = () => {
      for (let i = page; i <= shownTo; i++) {
        const image = document.querySelector<HTMLImageElement>(`.reader-continuous img[data-page="${i}"]`)
        if (image?.complete && image.naturalWidth > 0) note(i, image)
      }
    }
    sample()
    const onLoad = (event: Event) => {
      if (event.target instanceof HTMLImageElement && event.target.dataset.page !== undefined) sample()
    }
    document.addEventListener('load', onLoad, true)
    return () => document.removeEventListener('load', onLoad, true)
  }, [auto, continuous, page, shownTo, note])

  const colour = useMemo(() => {
    if (!auto) return null
    const found: Rgb[] = []
    for (let i = page; i <= shownTo; i++) {
      const c = urls[i] ? edges.get(urls[i]) : null
      if (c) found.push(c)
    }
    if (found.length === 0) return null
    return [0, 1, 2].map((k) => Math.round(found.reduce((sum, c) => sum + c[k], 0) / found.length)) as Rgb
    // `version` stands in for the module cache, which is not reactive.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [auto, urls, page, shownTo, version])

  const key = colour?.join(',')
  useEffect(() => {
    if (!colour) return
    setHeld(colour)
    remember(colour)
    // Keyed on the value, so an equal colour measured again changes nothing.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key])
  const shown = colour ?? held

  if (!auto) {
    return { background, light: /^#[0-9a-f]{6}$/i.test(background) && hexLightness(background) > LIGHT_AT, note }
  }
  const fallback = BACKGROUNDS.dark
  return {
    background: shown ? css(shown) : fallback,
    light: shown ? lightness(shown) > LIGHT_AT : false,
    note,
  }
}
