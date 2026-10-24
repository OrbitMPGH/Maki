import { useLayoutEffect } from 'react'
import { useThemeChoice } from '../theme-context'
import { motionPace, prefersReducedMotion } from './motionPrefs'

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

const EASE_MS = 250
const EASING = 'cubic-bezier(0.65, 0, 0.35, 1)'
const ACCENT = 'var(--brand)'
/** What the property holds now: the last value written, or the accent when it is unset. */
let current = ACCENT
let clock: Animation | null = null
let frame = 0

/**
 * Held by lib/pageTransition.ts while a page transition prepares the new page, which is captured on
 * the old ground. The tint then eases in alongside the cross-fade. Writing it before the capture left
 * the cross-fade's 0.2s, front-loaded curve as the only blend, which read as a snap.
 */
let holds = 0
/** Where the ground goes once the last hold is released; undefined when nothing asked to move. */
let pending: string | null | undefined
export function holdTint(): () => void {
  holds++
  let released = false
  return () => {
    if (released) return
    released = true
    if (--holds > 0 || pending === undefined) return
    const tint = pending
    pending = undefined
    easeTint(tint)
  }
}

let settling: Promise<void> = Promise.resolve()
/** Resolves once the cover the current page asked for has been sampled. */
export const tintSettled = () => settling

/**
 * Eases the ground to `tint`, or back to the accent for null. Scripted rather than a CSS transition
 * on the property: while Chrome animates a custom property it can paint what depends on it from the
 * fallback, so on some GPUs default buttons drew in the primary pink and pill borders in the text
 * colour for the length of the ease, with every computed value correct. Written frame by frame, the
 * tint stays an ordinary property.
 *
 * It still eases like the transition did: the blend is the same oklab mix, and the progress comes
 * from an animation with no target, which moves nothing but runs the easing on the document
 * timeline. Nothing is read back from the page, so each frame costs one restyle and no more.
 */
function easeTint(tint: string | null) {
  if (holds > 0) {
    pending = tint
    return
  }
  const root = document.documentElement
  clock?.cancel()
  cancelAnimationFrame(frame)
  const from = current
  const to = tint ?? ACCENT
  const write = (value: string) => {
    current = value
    if (value === ACCENT) root.style.removeProperty(PROPERTY)
    else root.style.setProperty(PROPERTY, value)
  }
  if (from === to || prefersReducedMotion()) return write(to)

  const timer = new Animation(new KeyframeEffect(null, null, { duration: EASE_MS * motionPace(), easing: EASING }))
  clock = timer
  timer.finished.then(
    () => {
      if (clock !== timer) return
      cancelAnimationFrame(frame)
      write(to)
    },
    () => {},
  )
  const step = () => {
    if (clock !== timer || timer.playState === 'finished') return
    const p = timer.effect?.getComputedTiming().progress
    if (p != null) write(`color-mix(in oklab, ${from}, ${to} ${(p * 100).toFixed(2)}%)`)
    frame = requestAnimationFrame(step)
  }
  // Started from a frame rather than mid-task, where the page that set the tint is usually still
  // mounting.
  frame = requestAnimationFrame(() => {
    if (clock !== timer) return
    timer.play()
    step()
  })
}

export function useCoverTint(coverUrl: string | null | undefined) {
  const { background } = useThemeChoice()
  const active = background === 'cover'

  // A layout effect so a page transition preparing this page sees the cover on its way during the
  // commit itself, rather than whenever React gets round to passive effects.
  useLayoutEffect(() => {
    if (!active || !coverUrl) return
    const id = ++owner
    let done!: () => void
    settling = new Promise<void>((resolve) => (done = resolve))
    const img = new Image()
    img.decoding = 'async'
    img.onload = () => {
      if (id === owner) easeTint(sampleTint(img))
      done()
    }
    img.onerror = () => {
      if (id === owner) easeTint(null)
      done()
    }
    img.src = coverUrl
    return () => {
      img.onload = null
      img.onerror = null
      done()
      // Inside a page transition the accent only becomes pending, and the next cover replaces it.
      // Otherwise deferred, so moving from one series to the next goes straight from cover to cover
      // instead of dipping back to the accent in between.
      if (holds > 0) {
        if (id === owner) easeTint(null)
        return
      }
      window.setTimeout(() => {
        if (id === owner) easeTint(null)
      }, 400)
    }
  }, [active, coverUrl])
}
