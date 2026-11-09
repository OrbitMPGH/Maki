/**
 * How soft a page looks, after Crété-Roffet's blur estimate: blur the page again and see how much
 * of its pixel-to-pixel contrast survives. A sharp page loses most of it, an already soft page
 * barely changes. The score runs from 0 (sharp) to 1 (smeared) and does not depend on contrast.
 *
 * Measured on a grid of small tiles at the size the page is shown, never above its own pixels, so a
 * 15000 px webtoon slice costs the same as a single page. A large soft file shown scaled down looks
 * sharp, and upscaling it would cost a lot for nothing (a 2822 x 4000 release scored 0.30 to 0.49
 * at its own pixels and 0.13 to 0.25 at 1300 to 2400 px tall). Flat areas add nothing to either
 * sum, so blank tiles do not dilute it.
 */

const TILE = 160
const KERNEL = 9
const COLUMNS = 3

/**
 * Calibrated on about 100 pages from a mixed library: screentone and clean line art score 0.08 to
 * 0.2, sub-800 px scans and soft colour webtoons 0.25 to 0.45.
 */
export const SOFT_BLUR = 0.28

/** Shown scale in twentieths: a resize that barely changes the page reuses its score. */
const shownScale = (scale: number) => Math.min(1, Math.max(0.05, Math.round(scale * 20) / 20))

/** Below this mean step per pixel the tiles are close to blank and say nothing either way. */
const MIN_DETAIL = 1

interface Sums {
  sharp: [number, number]
  kept: [number, number]
}

function addTile(luma: Float32Array, size: number, sums: Sums) {
  const half = KERNEL >> 1
  for (let axis = 0; axis < 2; axis++) {
    // axis 0 runs down columns, axis 1 along rows.
    for (let line = 0; line < size; line++) {
      const at = (i: number) => luma[axis === 0 ? i * size + line : line * size + i]
      let window = 0
      for (let k = -half; k <= half; k++) window += at(Math.min(size - 1, Math.max(0, k)))
      let previous = at(0)
      let previousBlur = window / KERNEL
      for (let i = 1; i < size; i++) {
        window += at(Math.min(size - 1, i + half)) - at(Math.max(0, i - half - 1))
        const value = at(i)
        const blur = window / KERNEL
        const step = Math.abs(value - previous)
        const blurStep = Math.abs(blur - previousBlur)
        sums.sharp[axis] += step
        sums.kept[axis] += Math.max(0, step - blurStep)
        previous = value
        previousBlur = blur
      }
    }
  }
}

/**
 * The blur score of a decoded page shown at `scale` device pixels per page pixel, or null when it is
 * close to blank or cannot be read. Above 1 the page is stretched, and Auto takes it anyway.
 */
export function measureBlur(image: HTMLImageElement, scale = 1): number | null {
  const shown = shownScale(scale)
  const width = Math.floor(image.naturalWidth * shown)
  const height = Math.floor(image.naturalHeight * shown)
  if (width < COLUMNS * 16 || height < 3 * 16) return null
  const rows = Math.max(3, Math.min(8, Math.round((height / width) * COLUMNS)))
  const size = Math.min(TILE, Math.floor(width / COLUMNS), Math.floor(height / rows))
  const canvas = document.createElement('canvas')
  canvas.width = size
  canvas.height = size
  const context = canvas.getContext('2d', { willReadFrequently: true })
  if (!context) return null
  context.imageSmoothingQuality = 'high'
  const sums: Sums = { sharp: [0, 0], kept: [0, 0] }
  const luma = new Float32Array(size * size)
  try {
    for (let row = 0; row < rows; row++) {
      for (let column = 0; column < COLUMNS; column++) {
        const y = Math.floor(((row + 0.5) * height) / rows - size / 2)
        const x = Math.floor(((column + 0.5) * width) / COLUMNS - size / 2)
        context.drawImage(image, x / shown, y / shown, size / shown, size / shown, 0, 0, size, size)
        const { data } = context.getImageData(0, 0, size, size)
        for (let i = 0; i < luma.length; i++) {
          luma[i] = (2 * data[i * 4] + 5 * data[i * 4 + 1] + data[i * 4 + 2]) / 8
        }
        addTile(luma, size, sums)
      }
    }
  } catch {
    return null
  } finally {
    canvas.width = 0
  }
  const pixels = rows * COLUMNS * size * size
  if (sums.sharp[0] / pixels < MIN_DETAIL && sums.sharp[1] / pixels < MIN_DETAIL) return null
  return Math.max(1 - sums.kept[0] / sums.sharp[0], 1 - sums.kept[1] / sums.sharp[1])
}

const CACHE_SIZE = 400
const scores = new Map<string, number | null>()

const scoreKey = (src: string, scale: number) => `${src}@${shownScale(scale)}`

/** Whether the page at `src`, shown at `scale`, looks soft enough for Auto to enhance it, measured once per URL and scale. */
export function isSoftPage(image: HTMLImageElement, src: string, scale: number): boolean {
  const key = scoreKey(src, scale)
  let score = scores.get(key)
  if (score === undefined) {
    if (!image.complete) return false
    // A broken image never becomes readable, so it is answered as plain rather than left unknown.
    score = image.naturalWidth === 0 ? null : measureBlur(image, scale)
    if (scores.size >= CACHE_SIZE) scores.delete(scores.keys().next().value!)
    scores.set(key, score)
  }
  return score !== null && score >= SOFT_BLUR
}

/** The answer for a page already measured at this scale, or undefined. Measuring takes 10 to 35 ms, so a page on screen asks this first. */
export function knownSoft(src: string, scale: number): boolean | undefined {
  const score = scores.get(scoreKey(src, scale))
  return score === undefined ? undefined : score !== null && score >= SOFT_BLUR
}
