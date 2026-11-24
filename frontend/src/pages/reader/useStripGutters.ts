import { useEffect, type RefObject } from 'react'
import type { StripTracker } from './stripPosition'
import { frameStripSection, mergeStripBands, type StripBand } from './stripFraming'

type Page = StripBand & { index: number; image: HTMLImageElement; height: number }
type Sample = StripBand & { gutters: StripBand[] }
type Tile = StripBand & { image: HTMLImageElement; chunk: number; id: string }

const SAMPLE_WIDTH = 320
const CHUNK_ASPECT = 2
const SAMPLE_OVERLAP = 16
const MIN_BAND_ROWS = 3
const MAX_CHUNKS = 96
const WARM_CHUNKS = 24
const MAX_NEARBY_PAGES = 24
const LOOKAHEAD = 1.35
const TURN_SAMPLE_LIMIT = 3
const TURN_SAMPLE_BUDGET_MS = 12

function distanceTo(band: StripBand, point: number): number {
  return Math.max(0, band.top - point, point - band.bottom)
}

function nearbyPages(
  scroller: HTMLElement,
  images: readonly (HTMLImageElement | null)[],
  page: number,
  low: number,
  high: number,
): Page[] {
  const origin = scroller.getBoundingClientRect().top + scroller.clientTop - scroller.scrollTop
  const pages: Page[] = []
  const add = (index: number) => {
    const image = images[index]
    if (!image) return null
    const box = image.getBoundingClientRect()
    const clip = image.parentElement?.dataset.cut ? image.parentElement.getBoundingClientRect().bottom : box.bottom
    const top = box.top - origin
    const bottom = Math.min(box.bottom, clip) - origin
    if (bottom > top && bottom >= low && top <= high) pages.push({ index, image, top, bottom, height: box.height })
    return { top, bottom }
  }
  const anchor = Math.min(Math.max(0, page), images.length - 1)
  add(anchor)
  for (const direction of [-1, 1]) {
    for (let step = 1; step <= MAX_NEARBY_PAGES; step++) {
      const index = anchor + step * direction
      if (index < 0 || index >= images.length) break
      const box = add(index)
      if (box && (direction < 0 ? box.bottom < low : box.top > high)) break
    }
  }
  return pages
}

function chunks(page: Page, low: number, high: number, focus: number): number[] {
  const { image, top, height } = page
  if (!image.complete || !image.naturalWidth || !image.naturalHeight) return []
  const chunkHeight = height * (image.naturalWidth * CHUNK_ASPECT) / image.naturalHeight
  const first = Math.floor(Math.max(0, low - top) / chunkHeight)
  const last = Math.min(Math.ceil(height / chunkHeight) - 1, Math.floor((Math.min(high, page.bottom) - top) / chunkHeight))
  const middle = Math.min(last, Math.max(first, Math.floor((focus - top) / chunkHeight)))
  const from = Math.max(first, middle - WARM_CHUNKS / 2)
  const to = Math.min(last, middle + WARM_CHUNKS / 2)
  return Array.from({ length: to - from + 1 }, (_, index) => from + index)
}

/** Finds flat bands in a bounded tile. Keep their ends in fractions of the original page. */
function sampleGutters(canvas: HTMLCanvasElement, image: HTMLImageElement, chunk: number): Sample | null {
  const { naturalWidth: width, naturalHeight: height } = image
  const chunkHeight = width * CHUNK_ASPECT
  const overlap = Math.ceil((SAMPLE_OVERLAP * width) / SAMPLE_WIDTH)
  const start = Math.max(0, chunk * chunkHeight - overlap)
  const end = Math.min(height, (chunk + 1) * chunkHeight + overlap)
  canvas.width = Math.min(width, SAMPLE_WIDTH)
  canvas.height = Math.max(1, Math.round(((end - start) * canvas.width) / width))
  const context = canvas.getContext('2d', { alpha: false, willReadFrequently: true })
  if (!context) return null
  context.fillStyle = '#fff'
  context.fillRect(0, 0, canvas.width, canvas.height)
  context.imageSmoothingQuality = 'high'
  let data: Uint8ClampedArray
  try {
    context.drawImage(image, 0, start, width, end - start, 0, 0, canvas.width, canvas.height)
    data = context.getImageData(0, 0, canvas.width, canvas.height).data
  } catch {
    return null
  }
  const gutters: StripBand[] = []
  const share = (row: number) => (start + (row / canvas.height) * (end - start)) / height
  let from = -1
  const sums = [0, 0, 0]
  const squares = [0, 0, 0]
  const lastMean = [0, 0, 0]
  for (let y = 0; y < canvas.height; y++) {
    sums.fill(0)
    squares.fill(0)
    for (let x = 0; x < canvas.width; x++) {
      const offset = (y * canvas.width + x) * 4
      for (let channel = 0; channel < 3; channel++) {
        const value = data[offset + channel]
        sums[channel] += value
        squares[channel] += value * value
      }
    }
    let quiet = true
    for (let channel = 0; channel < 3; channel++) {
      const mean = sums[channel] / canvas.width
      if (squares[channel] / canvas.width - mean * mean > 16 || (y > 0 && Math.abs(mean - lastMean[channel]) > 5)) {
        quiet = false
      }
      lastMean[channel] = mean
    }
    if (quiet && from < 0) from = y
    if (!quiet && from >= 0) {
      if (y - from >= MIN_BAND_ROWS) gutters.push({ top: share(from), bottom: share(y) })
      from = -1
    }
  }
  if (from >= 0 && canvas.height - from >= MIN_BAND_ROWS) {
    gutters.push({ top: share(from), bottom: share(canvas.height) })
  }
  return { top: start / height, bottom: end / height, gutters }
}

export function useStripGutters(
  enabled: boolean,
  tracker: StripTracker | undefined,
  container: RefObject<HTMLDivElement | null>,
  images: RefObject<(HTMLImageElement | null)[]>,
  page: RefObject<number>,
  urls: string[],
) {
  useEffect(() => {
    const strip = container.current
    const scroller = strip?.parentElement
    if (!enabled || !tracker || !strip || !scroller || urls.length === 0) return

    const canvas = document.createElement('canvas')
    const cache = new Map<string, Sample | null>()
    const key = (image: HTMLImageElement, chunk: number) => `${image.currentSrc || image.src}:${chunk}`
    const points = (low: number, high: number) => nearbyPages(scroller, images.current, page.current, low, high)
    const canSample = () => scroller.scrollWidth <= scroller.clientWidth + 2
    const tilesFor = (item: Page, low: number, high: number, focus: number): Tile[] => {
      const { image } = item
      return chunks(item, low, high, focus).map((chunk) => {
        const height = item.height * image.naturalWidth * CHUNK_ASPECT / image.naturalHeight
        const top = item.top + chunk * height
        return { image, chunk, id: key(image, chunk), top, bottom: Math.min(item.bottom, top + height) }
      })
    }
    const remember = ({ id, image, chunk }: Tile) => {
      if (cache.size >= MAX_CHUNKS) cache.delete(cache.keys().next().value!)
      cache.set(id, sampleGutters(canvas, image, chunk))
    }

    let cancel: (() => void) | undefined
    const warm = () => {
      cancel = undefined
      if (!canSample()) return
      const height = scroller.clientHeight
      const top = scroller.scrollTop
      const bottom = top + height
      const low = top - height * LOOKAHEAD
      const high = bottom + height * LOOKAHEAD
      const nearby = points(low, high).flatMap((item) => tilesFor(item, low, high, top + height / 2))
      const distance = (tile: Tile) => Math.min(distanceTo(tile, bottom), distanceTo(tile, top) + height / 4)
      nearby.sort((a, b) => distance(a) - distance(b))
      const next = nearby.slice(0, WARM_CHUNKS).find(({ id }) => !cache.has(id))
      if (!next) return
      remember(next)
      schedule()
    }
    function schedule() {
      if (cancel) return
      if (typeof requestIdleCallback === 'function') {
        const id = requestIdleCallback(warm, { timeout: 150 })
        cancel = () => cancelIdleCallback(id)
      } else {
        const id = window.setTimeout(warm, 32)
        cancel = () => window.clearTimeout(id)
      }
    }

    const plan = (pages: { item: Page; tiles: Tile[] }[], from: number, height: number, direction: 1 | -1, max: number) => {
      const coverage: StripBand[] = []
      const gutters: StripBand[] = []
      let previousGutterAtEnd = false
      for (let i = 0; i < pages.length; i++) {
        const { item, tiles } = pages[i]
        const previous = pages[i - 1]?.item
        let gutterAtStart = false
        let gutterAtEnd = false
        const project = (band: StripBand) => ({
          top: item.top + band.top * item.height,
          bottom: Math.min(item.bottom, item.top + band.bottom * item.height),
        })
        for (const { id } of tiles) {
          const sample = cache.get(id)
          if (!sample) continue
          const range = project(sample)
          if (range.bottom <= range.top) continue
          coverage.push(range)
          for (const gutter of sample.gutters) {
            const band = project(gutter)
            if (band.bottom <= band.top) continue
            gutters.push(band)
            gutterAtStart ||= band.top <= item.top + 1
            gutterAtEnd ||= band.bottom >= item.bottom - 1
          }
        }
        if (previous?.index === item.index - 1 && previous.bottom < item.top) {
          const displayGap = { top: previous.bottom, bottom: item.top }
          coverage.push(displayGap)
          // A display gap extends a gutter when both images are blank at the join.
          if (previousGutterAtEnd && gutterAtStart) gutters.push(displayGap)
        }
        previousGutterAtEnd = gutterAtEnd
      }
      const first = pages[0]?.item
      const last = pages.at(-1)?.item
      return frameStripSection(
        from, height, max, direction, mergeStripBands(coverage), mergeStripBands(gutters),
        first?.index === 0 ? first.top : undefined,
        last?.index === urls.length - 1 ? last.bottom : undefined,
      )
    }

    tracker.framedTarget = (from, direction, max) => {
      if (!canSample()) return
      const height = scroller.clientHeight
      const low = from - height * LOOKAHEAD
      const high = from + height * (1 + LOOKAHEAD)
      const pages = points(low, high).sort((a, b) => a.index - b.index)
        .map((item) => ({ item, tiles: tilesFor(item, low, high, from + height / 2) }))
      const target = plan(pages, from, height, direction, max)
      if (target !== undefined) return target

      // Rapid turns can reach loaded images before the idle callback has sampled them.
      const edge = direction > 0 ? from + height : from
      const priority = (tile: Tile) => distanceTo(tile, edge) +
        ((direction > 0 ? tile.bottom < edge : tile.top > edge) ? height * 0.85 : 0)
      const missing = pages.flatMap(({ tiles }) => tiles).filter(({ id }) => !cache.has(id))
        .sort((a, b) => priority(a) - priority(b))
      const deadline = performance.now() + TURN_SAMPLE_BUDGET_MS
      for (const tile of missing.slice(0, TURN_SAMPLE_LIMIT)) {
        remember(tile)
        const framed = plan(pages, from, height, direction, max)
        if (framed !== undefined) return framed
        if (performance.now() >= deadline) break
      }
    }

    const observer = new ResizeObserver(schedule)
    observer.observe(scroller)
    observer.observe(strip)
    scroller.addEventListener('scroll', schedule, { passive: true })
    scroller.addEventListener('load', schedule, true)
    schedule()
    return () => {
      cancel?.()
      observer.disconnect()
      scroller.removeEventListener('scroll', schedule)
      scroller.removeEventListener('load', schedule, true)
      tracker.framedTarget = null
    }
  }, [enabled, tracker, container, images, page, urls])
}
