import { useEffect, type RefObject } from 'react'
import type { StripTracker } from './stripPosition'

type Band = { top: number; bottom: number }
type Page = Band & { index: number; image: HTMLImageElement; height: number }

const SAMPLE_WIDTH = 320
const CHUNK_ASPECT = 2
const SAMPLE_OVERLAP = 16
const MIN_BAND_ROWS = 3
const MAX_CHUNKS = 96
const WARM_CHUNKS = 24
const MAX_NEARBY_PAGES = 24
const GUTTER_PAD = 7
const MIN_STEP = 0.65
const MAX_STEP = 0.95

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
function sampleGutters(canvas: HTMLCanvasElement, image: HTMLImageElement, chunk: number): Band[] {
  const { naturalWidth: width, naturalHeight: height } = image
  const chunkHeight = width * CHUNK_ASPECT
  const overlap = Math.ceil((SAMPLE_OVERLAP * width) / SAMPLE_WIDTH)
  const start = Math.max(0, chunk * chunkHeight - overlap)
  const end = Math.min(height, (chunk + 1) * chunkHeight + overlap)
  canvas.width = Math.min(width, SAMPLE_WIDTH)
  canvas.height = Math.max(1, Math.round(((end - start) * canvas.width) / width))
  const context = canvas.getContext('2d', { alpha: false, willReadFrequently: true })
  if (!context) return []
  context.fillStyle = '#fff'
  context.fillRect(0, 0, canvas.width, canvas.height)
  context.imageSmoothingQuality = 'high'
  let data: Uint8ClampedArray
  try {
    context.drawImage(image, 0, start, width, end - start, 0, 0, canvas.width, canvas.height)
    data = context.getImageData(0, 0, canvas.width, canvas.height).data
  } catch {
    return []
  }
  const bands: Band[] = []
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
      if (y - from >= MIN_BAND_ROWS) bands.push({ top: share(from), bottom: share(y) })
      from = -1
    }
  }
  if (from >= 0 && canvas.height - from >= MIN_BAND_ROWS) {
    bands.push({ top: share(from), bottom: share(canvas.height) })
  }
  return bands
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
    const cache = new Map<string, Band[]>()
    const key = (image: HTMLImageElement, chunk: number) => `${image.currentSrc || image.src}:${chunk}`
    const points = (low: number, high: number) => nearbyPages(scroller, images.current, page.current, low, high)
    const canSample = () => scroller.scrollWidth <= scroller.clientWidth + 2

    let cancel: (() => void) | undefined
    const warm = () => {
      cancel = undefined
      if (!canSample()) return
      const height = scroller.clientHeight
      const middle = scroller.scrollTop + height / 2
      const low = middle - height * 1.5
      const high = middle + height * 1.5
      const nearby = points(low, high).flatMap((item) => chunks(item, low, high, middle).map((chunk) => ({
        image: item.image,
        chunk,
        id: key(item.image, chunk),
        distance: Math.abs(item.top + (chunk + 0.5) * item.height *
          ((item.image.naturalWidth * CHUNK_ASPECT) / item.image.naturalHeight) - middle),
      })))
      nearby.sort((a, b) => a.distance - b.distance)
      const next = nearby.slice(0, WARM_CHUNKS).find(({ id }) => !cache.has(id))
      if (!next) return
      if (cache.size >= MAX_CHUNKS) cache.delete(cache.keys().next().value!)
      cache.set(next.id, sampleGutters(canvas, next.image, next.chunk))
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

    tracker.gutterTarget = (from, direction, fallback, max) => {
      if (fallback <= 0 || fallback >= max || !canSample()) return fallback
      const height = scroller.clientHeight
      const edgeOffset = direction < 0 ? height : 0
      const first = from + direction * height * MIN_STEP
      const last = from + direction * height * MAX_STEP
      const low = Math.max(0, Math.min(first, last)) + edgeOffset
      const high = Math.min(max, Math.max(first, last)) + edgeOffset
      const preferred = fallback + edgeOffset
      let best = preferred
      let distance = Infinity
      const consider = (top: number, bottom: number) => {
        const start = Math.max(low, top + GUTTER_PAD)
        const end = Math.min(high, bottom - GUTTER_PAD)
        if (start > end) return
        const position = Math.min(end, Math.max(start, preferred))
        const difference = Math.abs(position - preferred)
        if (difference < distance || (difference === distance && (position - best) * direction < 0)) {
          best = position
          distance = difference
        }
      }
      const pages = points(low - GUTTER_PAD, high + GUTTER_PAD).sort((a, b) => a.index - b.index)
      for (let i = 0; i < pages.length; i++) {
        const item = pages[i]
        const previous = pages[i - 1]
        if (previous?.index === item.index - 1 && previous.image.complete && previous.image.naturalWidth &&
          item.image.complete && item.image.naturalWidth) {
          consider(previous.bottom, item.top)
        }
        for (const chunk of chunks(item, low - GUTTER_PAD, high + GUTTER_PAD, preferred)) {
          for (const band of cache.get(key(item.image, chunk)) ?? []) {
            consider(item.top + band.top * item.height, Math.min(item.bottom, item.top + band.bottom * item.height))
          }
        }
      }
      return best - edgeOffset
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
      tracker.gutterTarget = null
    }
  }, [enabled, tracker, container, images, page, urls])
}
