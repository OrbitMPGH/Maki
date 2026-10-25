import { useEffect, useSyncExternalStore } from 'react'
import type { EnhanceEngine, EnhancePreset } from '../../lib/enhance'
import { isServerMethod } from './enhanceMethods'
import type { Region } from './pageShape'
import type { EnhanceMethod } from './prefs'

export type EngineState =
  | { status: 'off' }
  | { status: 'loading' }
  | { status: 'ready'; engine: EnhanceEngine }
  | { status: 'unsupported' }
  | { status: 'broken' }

const OFF: EngineState = { status: 'off' }
let state: EngineState = OFF
const listeners = new Set<() => void>()

function set(next: EngineState) {
  state = next
  for (const listener of listeners) listener()
}

function subscribe(listener: () => void) {
  listeners.add(listener)
  return () => listeners.delete(listener)
}

// The engine module, shaders included, is fetched only here and only once Enhance is on.
function start() {
  if (state.status !== 'off') return
  set({ status: 'loading' })
  import('../../lib/enhance')
    .then((module) => module.loadEnhanceEngine())
    .then(
      (engine) =>
        set(!engine.supported ? { status: 'unsupported' } : engine.broken ? { status: 'broken' } : { status: 'ready', engine }),
      () => set({ status: 'unsupported' }),
    )
}

/** The engine failed mid-session: every page falls back to the plain image until a reload. */
export function markEnhanceBroken() {
  if (state.status === 'ready') set({ status: 'broken' })
}

/** The session's enhance engine, loaded the first time `active` is true. */
export function useEnhanceEngine(active: boolean): EngineState {
  const current = useSyncExternalStore(subscribe, () => state)
  useEffect(() => {
    if (active) start()
  }, [active])
  return active ? current : OFF
}

/** The browser preset a method runs as. A server method shows Balanced until its upscale is ready, and whenever the server tier cannot be used. */
export function presetFor(method: EnhanceMethod): EnhancePreset {
  return isServerMethod(method) ? 'a4k-m' : (method as EnhancePreset)
}

export function enhanceKey(src: string, region: Region): string {
  return `${src}#${region.x},${region.y},${region.w},${region.h}`
}

export interface EnhanceTarget {
  width: number
  height: number
  /** Shown at 1.2x its source or more on either axis, the `auto` threshold. */
  stretched: boolean
}

const STRETCH = 1.2

/**
 * Device pixels a region is displayed at, from the layout box that shows it. A quarter turn swaps
 * the box's axes back. The target never exceeds 2x the region's own pixels.
 */
export function measureTarget(
  box: HTMLElement,
  rotate: number,
  natural: { width: number; height: number },
  region: Region,
): EnhanceTarget | null {
  const rect = box.getBoundingClientRect()
  const [w, h] = rotate === 0 ? [rect.width, rect.height] : [rect.height, rect.width]
  if (w < 1 || h < 1) return null
  const dpr = window.devicePixelRatio || 1
  const nw = natural.width * region.w
  const nh = natural.height * region.h
  return {
    width: Math.max(1, Math.round(Math.min(w * dpr, nw * 2))),
    height: Math.max(1, Math.round(Math.min(h * dpr, nh * 2))),
    stretched: (w * dpr) / nw >= STRETCH || (h * dpr) / nh >= STRETCH,
  }
}

/** The region of a decoded page as a bitmap the engine can take ownership of. */
export async function regionBitmap(image: HTMLImageElement, region: Region): Promise<ImageBitmap> {
  const sx = Math.round(image.naturalWidth * region.x)
  const sy = Math.round(image.naturalHeight * region.y)
  const sw = Math.max(1, Math.round(image.naturalWidth * region.w))
  const sh = Math.max(1, Math.round(image.naturalHeight * region.h))
  try {
    return await createImageBitmap(image, sx, sy, sw, sh)
  } catch {
    // Some browsers refuse an SVG or an undecoded element directly; a canvas copy always works.
    const canvas = document.createElement('canvas')
    canvas.width = sw
    canvas.height = sh
    canvas.getContext('2d')?.drawImage(image, sx, sy, sw, sh, 0, 0, sw, sh)
    const bitmap = await createImageBitmap(canvas)
    canvas.width = 0
    return bitmap
  }
}

export function isEnhanceError(error: unknown, code: string): boolean {
  return error instanceof Error && error.name === 'EnhanceError' && (error as Error & { code?: string }).code === code
}

/**
 * The cache keys one view has asked the engine for, grouped by page URL, so pages that leave the
 * view's window are evicted. It also remembers what each region size was last measured at, which
 * is how a page not yet on screen can be enhanced ahead at the size it will be shown, and which
 * pages are shown stretched, which is all `auto` asks the server for.
 */
export class EnhanceScope {
  private readonly keys = new Map<string, Set<string>>()
  private readonly sizes = new Map<string, EnhanceTarget>()
  private readonly stretched = new Map<number, boolean>()
  private engine: EnhanceEngine | null = null
  private readonly onSize: () => void
  private readonly onStretched: (pages: ReadonlySet<number>) => void

  constructor(onSize: () => void = () => {}, onStretched: (pages: ReadonlySet<number>) => void = () => {}) {
    this.onSize = onSize
    this.onStretched = onStretched
  }

  attach(engine: EnhanceEngine | null) {
    this.engine = engine
  }

  note(src: string, key: string) {
    let set = this.keys.get(src)
    if (!set) this.keys.set(src, (set = new Set()))
    set.add(key)
  }

  noteSize(regionWidth: number, regionHeight: number, target: EnhanceTarget) {
    const id = `${Math.round(regionWidth)}x${Math.round(regionHeight)}`
    const known = this.sizes.get(id)
    if (known && known.width === target.width && known.height === target.height && known.stretched === target.stretched) return
    this.sizes.set(id, target)
    this.onSize()
  }

  sizeFor(regionWidth: number, regionHeight: number): EnhanceTarget | undefined {
    return this.sizes.get(`${Math.round(regionWidth)}x${Math.round(regionHeight)}`)
  }

  notePage(page: number, stretched: boolean) {
    if (this.stretched.get(page) === stretched) return
    this.stretched.set(page, stretched)
    this.reportPages()
  }

  /** Drops every page's stretched state, for a new chapter or a view going away. */
  forgetPages() {
    if (this.stretched.size === 0) return
    this.stretched.clear()
    this.reportPages()
  }

  private reportPages() {
    const pages = new Set<number>()
    for (const [page, stretched] of this.stretched) if (stretched) pages.add(page)
    this.onStretched(pages)
  }

  /** Evicts every page whose URL is not in `srcs`. */
  retain(srcs: Set<string>) {
    for (const [src, keys] of this.keys) {
      if (srcs.has(src)) continue
      for (const key of keys) this.engine?.evict(key)
      this.keys.delete(src)
    }
  }

  release() {
    this.retain(new Set())
    this.sizes.clear()
  }
}

/** What a view hands each page while Enhance is on and the engine is ready. */
export interface PageEnhance {
  mode: 'auto' | 'always'
  preset: EnhancePreset
  /** Null while a server method runs without a usable engine: pages stay plain until the server's upscale is ready. */
  engine: EnhanceEngine | null
  scope: EnhanceScope
  /** Changes with fit, zoom and scale; a change re-measures the page after a short settle. */
  layout: string
}
