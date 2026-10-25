import { useEffect, useRef, useState } from 'react'
import { useCompareHeld } from './compareHold'
import {
  enhanceKey,
  isEnhanceError,
  markEnhanceBroken,
  measureTarget,
  regionBitmap,
  type EnhanceTarget,
  type PageEnhance,
} from './enhanceRuntime'
import type { Region } from './pageShape'

const SETTLE_MS = 300

export interface EnhancedPageProps {
  /** The decoded original page. It is the engine's source and stays mounted for crop and compare. */
  image: HTMLImageElement
  /** The page URL without a retry suffix; with the region it keys the engine's cache. */
  src: string
  region: Region
  rotate: 0 | 90 | -90
  alt: string
  tone?: string
  enhance: PageEnhance
  /** 0 for a page on screen, then its distance in reading order. */
  priority: number
  /** The layout box the region is displayed in, measured for the auto gate and the target size. */
  box: HTMLElement | null
  /** Draw positioned over `box` (the continuous strip) instead of filling the parent (RegionPage's frame). */
  overlay?: boolean
  /** True while the canvas is showing, so the host can hide its image. */
  onShowing: (showing: boolean) => void
}

const sameTarget = (a: EnhanceTarget | null, b: EnhanceTarget) =>
  a !== null && a.width === b.width && a.height === b.height && a.stretched === b.stretched

/**
 * A page region run through the enhance engine and drawn into a canvas. Until the result is in,
 * while compare is held, or when `auto` finds the page shown at about its own size, the canvas is
 * hidden and the host's image shows instead.
 */
export default function EnhancedPage({
  image,
  src,
  region,
  rotate,
  alt,
  tone,
  enhance,
  priority,
  box,
  overlay = false,
  onShowing,
}: EnhancedPageProps) {
  const canvas = useRef<HTMLCanvasElement>(null)
  const held = useCompareHeld()
  const [target, setTarget] = useState<EnhanceTarget | null>(null)
  const [drawn, setDrawn] = useState<string | null>(null)
  const { mode, preset, engine, scope, layout } = enhance
  const key = enhanceKey(src, region)
  const identity = `${key}|${preset}`
  const regionId = `${region.x},${region.y},${region.w},${region.h}`

  const measure = useRef(() => {})
  measure.current = () => {
    if (!box) return
    const next = measureTarget(box, rotate, { width: image.naturalWidth, height: image.naturalHeight }, region)
    if (!next) return
    scope.noteSize(image.naturalWidth * region.w, image.naturalHeight * region.h, next)
    setTarget((previous) => (sameTarget(previous, next) ? previous : next))
  }

  // Measured at first layout, then again only once a resize has settled, so turning a phone does
  // not flip pages between enhanced and plain on the way round.
  useEffect(() => {
    if (!box) return
    let first = true
    let timer: ReturnType<typeof setTimeout> | undefined
    const observer = new ResizeObserver(() => {
      if (first) {
        first = false
        measure.current()
        return
      }
      clearTimeout(timer)
      timer = setTimeout(() => measure.current(), SETTLE_MS)
    })
    observer.observe(box)
    return () => {
      observer.disconnect()
      clearTimeout(timer)
    }
  }, [box, rotate, regionId, image])

  const lastLayout = useRef(layout)
  useEffect(() => {
    if (lastLayout.current === layout) return
    lastLayout.current = layout
    const timer = setTimeout(() => measure.current(), SETTLE_MS)
    return () => clearTimeout(timer)
  }, [layout])

  // In the strip the canvas sits over the page image, which moves whenever a page above it loads.
  useEffect(() => {
    const element = canvas.current
    const parent = box?.parentElement
    if (!overlay || !box || !element || !parent) return
    const place = () => {
      const at = box.getBoundingClientRect()
      const origin = parent.getBoundingClientRect()
      element.style.left = `${at.left - origin.left - parent.clientLeft}px`
      element.style.top = `${at.top - origin.top - parent.clientTop}px`
      element.style.width = `${at.width}px`
      element.style.height = `${at.height}px`
    }
    place()
    const observer = new ResizeObserver(place)
    observer.observe(box)
    observer.observe(parent)
    return () => observer.disconnect()
  }, [overlay, box])

  const active = target !== null && (mode === 'always' || target.stretched)
  const priorityRef = useRef(priority)
  priorityRef.current = priority
  // `key` carries the region's values; the object itself may be rebuilt on every render.
  const regionRef = useRef(region)
  regionRef.current = region

  useEffect(() => {
    if (!active || !target) return
    const draw = (bitmap: ImageBitmap) => {
      const element = canvas.current
      if (!element) return
      element.width = bitmap.width
      element.height = bitmap.height
      element.getContext('2d')?.drawImage(bitmap, 0, 0)
      setDrawn(identity)
    }
    scope.note(src, key)
    const hit = engine.peek(key, preset, target.width, target.height)
    if (hit) {
      draw(hit)
      return
    }
    const controller = new AbortController()
    const { signal } = controller
    void regionBitmap(image, regionRef.current)
      .then((bitmap) => {
        if (signal.aborted) {
          bitmap.close()
          return null
        }
        return engine.enhance(bitmap, {
          preset,
          targetWidth: target.width,
          targetHeight: target.height,
          key,
          priority: priorityRef.current,
          signal,
        })
      })
      .then((result) => {
        if (result && !signal.aborted) draw(result)
      })
      .catch((error: unknown) => {
        if (isEnhanceError(error, 'broken') || isEnhanceError(error, 'unsupported')) markEnhanceBroken()
      })
    return () => controller.abort()
  }, [active, target, engine, scope, image, identity, key, preset, src])

  useEffect(() => {
    engine.setPriority(key, priority)
  }, [engine, key, priority])

  // Drop the canvas's pixels with it, rather than waiting for the element to be collected.
  useEffect(() => {
    const element = canvas.current
    return () => {
      if (!element) return
      element.width = 0
      element.height = 0
    }
  }, [])

  const showing = active && drawn === identity && !held
  const reportShowing = useRef(onShowing)
  reportShowing.current = onShowing
  useEffect(() => {
    reportShowing.current(showing)
  }, [showing])
  useEffect(() => () => reportShowing.current(false), [])

  return (
    <canvas
      ref={canvas}
      className={overlay ? 'reader-enhanced reader-enhanced-overlay' : 'reader-enhanced'}
      role="img"
      aria-label={alt}
      data-enhanced={showing}
      style={{ filter: tone, visibility: showing ? undefined : 'hidden' }}
    />
  )
}
