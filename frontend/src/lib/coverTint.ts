import { useEffect } from 'react'
import { useThemeChoice } from '../theme-context'

/**
 * The Cover tint background mixes the ground toward the cover on screen instead of the accent.
 * Screens with one subject (the series page, Home's continue-reading hero) publish that cover's
 * colour as `--ground-tint` on the root; everywhere else theme.css falls back to the accent.
 */
const PROPERTY = '--ground-tint'
let owner = 0

function toHsl(r: number, g: number, b: number): [number, number, number] {
  r /= 255
  g /= 255
  b /= 255
  const max = Math.max(r, g, b)
  const min = Math.min(r, g, b)
  const l = (max + min) / 2
  if (max === min) return [0, 0, l]
  const d = max - min
  const s = l > 0.5 ? d / (2 - max - min) : d / (max + min)
  const h = max === r ? (g - b) / d + (g < b ? 6 : 0) : max === g ? (b - r) / d + 2 : (r - g) / d + 4
  return [h / 6, s, l]
}

function toHex(h: number, s: number, l: number): string {
  const f = (n: number) => {
    const k = (n + h * 12) % 12
    const c = l - s * Math.min(l, 1 - l) * Math.max(-1, Math.min(k - 3, 9 - k, 1))
    return Math.round(c * 255).toString(16).padStart(2, '0')
  }
  return `#${f(0)}${f(8)}${f(4)}`
}

/**
 * A vivid mid-tone average, so the paper white and line-art black that fill most covers don't
 * drag the result to grey. The answer is pinned to one lightness so every cover tints the ground
 * by about the same amount; a cover with no real colour returns null and the accent stays.
 */
function sampleTint(img: HTMLImageElement): string | null {
  const w = 24
  const h = 36
  const canvas = document.createElement('canvas')
  canvas.width = w
  canvas.height = h
  const ctx = canvas.getContext('2d', { willReadFrequently: true })
  if (!ctx) return null
  ctx.drawImage(img, 0, 0, w, h)
  let data: Uint8ClampedArray
  try {
    data = ctx.getImageData(0, 0, w, h).data
  } catch {
    return null
  }
  let r = 0
  let g = 0
  let b = 0
  let total = 0
  for (let i = 0; i < data.length; i += 4) {
    const [, s, l] = toHsl(data[i], data[i + 1], data[i + 2])
    const weight = s * s * Math.max(0, 1 - Math.abs(l - 0.5) * 1.8)
    r += data[i] * weight
    g += data[i + 1] * weight
    b += data[i + 2] * weight
    total += weight
  }
  if (total < w * h * 0.03) return null
  const [hue, sat] = toHsl(r / total, g / total, b / total)
  return toHex(hue, Math.min(0.75, Math.max(0.45, sat)), 0.55)
}

export function useCoverTint(coverUrl: string | null | undefined) {
  const { background } = useThemeChoice()
  const active = background === 'cover'

  useEffect(() => {
    if (!active || !coverUrl) return
    const id = ++owner
    const root = document.documentElement
    const img = new Image()
    img.decoding = 'async'
    img.onload = () => {
      if (id !== owner) return
      const tint = sampleTint(img)
      if (tint) root.style.setProperty(PROPERTY, tint)
      else root.style.removeProperty(PROPERTY)
    }
    img.onerror = () => {
      if (id === owner) root.style.removeProperty(PROPERTY)
    }
    img.src = coverUrl
    return () => {
      img.onload = null
      img.onerror = null
      // Deferred so moving from one series to the next goes straight from cover to cover
      // instead of dipping back to the accent in between.
      window.setTimeout(() => {
        if (id === owner) root.style.removeProperty(PROPERTY)
      }, 400)
    }
  }, [active, coverUrl])
}
