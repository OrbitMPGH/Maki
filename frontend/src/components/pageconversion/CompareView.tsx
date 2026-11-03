import { useEffect, useRef, useState } from 'react'
import type { KeyboardEvent, PointerEvent } from 'react'
import { Loader, Skeleton, VisuallyHidden } from '@mantine/core'
import { useLingui } from '@lingui/react/macro'

const LENS = 176
const ZOOM = 2

/**
 * Before and after at 1:1 pixels. Both pages are laid out identically in one scrollable viewport
 * that opens at the middle of the page; the divider stays where it is on screen while the page
 * scrolls under it, so any part of the page can be wiped. The loupe redraws the same two layers at 2x
 * around the pointer.
 */
export function CompareView({
  originalUrl,
  encodedUrl,
  loading,
  loupe,
}: {
  originalUrl: string | undefined
  encodedUrl: string | undefined
  loading: boolean
  loupe: boolean
}) {
  const { t } = useLingui()
  const wrapper = useRef<HTMLDivElement>(null)
  const viewport = useRef<HTMLDivElement>(null)
  const originalImg = useRef<HTMLImageElement>(null)
  const encodedImg = useRef<HTMLImageElement>(null)
  const lens = useRef<HTMLCanvasElement>(null)
  const needsCentering = useRef(false)
  const dragging = useRef(false)
  const [size, setSize] = useState<{ w: number; h: number } | null>(null)
  const [box, setBox] = useState({ w: 0, h: 0 })
  const [scroll, setScroll] = useState({ x: 0, y: 0 })
  const [split, setSplit] = useState(0.5)

  const shown = originalUrl !== undefined
  useEffect(() => {
    const el = viewport.current
    if (!el) return
    const measure = () => setBox({ w: el.clientWidth, h: el.clientHeight })
    measure()
    const observer = new ResizeObserver(measure)
    observer.observe(el)
    return () => observer.disconnect()
  }, [shown])

  useEffect(() => {
    const el = viewport.current
    if (!size || !el || !needsCentering.current) return
    needsCentering.current = false
    el.scrollLeft = Math.max(0, (size.w - el.clientWidth) / 2)
    el.scrollTop = Math.max(0, (size.h - el.clientHeight) / 2)
    setScroll({ x: el.scrollLeft, y: el.scrollTop })
  }, [size])

  const offsetX = size ? Math.max(0, (box.w - size.w) / 2) : 0
  const dividerX = split * box.w
  const clipLeft = Math.min(Math.max(scroll.x + dividerX - offsetX, 0), size?.w ?? 0)

  const moveDivider = (clientX: number) => {
    const rect = wrapper.current?.getBoundingClientRect()
    if (!rect || box.w === 0) return
    setSplit(Math.min(1, Math.max(0, (clientX - rect.left) / box.w)))
  }

  const onDividerKey = (event: KeyboardEvent) => {
    const step = event.shiftKey ? 0.1 : 0.02
    if (event.key === 'ArrowLeft') setSplit((s) => Math.max(0, s - step))
    else if (event.key === 'ArrowRight') setSplit((s) => Math.min(1, s + step))
    else if (event.key === 'Home') setSplit(0)
    else if (event.key === 'End') setSplit(1)
    else return
    event.preventDefault()
  }

  const hideLens = () => {
    if (lens.current) lens.current.style.display = 'none'
  }

  const drawLens = (event: PointerEvent<HTMLDivElement>) => {
    const canvas = lens.current
    const original = originalImg.current
    const encoded = encodedImg.current
    const frame = viewport.current?.getBoundingClientRect()
    if (!loupe || !canvas || !original || !frame || !size || event.pointerType === 'touch') {
      hideLens()
      return
    }
    const px = event.clientX - frame.left
    const py = event.clientY - frame.top
    const x = px + scroll.x - offsetX
    const y = py + scroll.y
    if (x < 0 || y < 0 || x > size.w || y > size.h) {
      hideLens()
      return
    }
    const ctx = canvas.getContext('2d')
    if (!ctx) return
    const dpr = window.devicePixelRatio || 1
    const span = LENS / ZOOM
    const sx = (x - span / 2) * dpr
    const sy = (y - span / 2) * dpr
    const sw = span * dpr
    ctx.imageSmoothingEnabled = false
    ctx.clearRect(0, 0, LENS, LENS)
    ctx.drawImage(original, sx, sy, sw, sw, 0, 0, LENS, LENS)
    const edge = (clipLeft - (x - span / 2)) * ZOOM
    if (encoded && edge < LENS) {
      ctx.save()
      ctx.beginPath()
      ctx.rect(Math.max(edge, 0), 0, LENS, LENS)
      ctx.clip()
      ctx.drawImage(encoded, sx, sy, sw, sw, 0, 0, LENS, LENS)
      ctx.restore()
    }
    const left = px + 20 + LENS > frame.width ? px - 20 - LENS : px + 20
    const top = py + 20 + LENS > frame.height ? py - 20 - LENS : py + 20
    canvas.style.display = 'block'
    canvas.style.transform = `translate(${Math.max(0, left)}px, ${Math.max(0, top)}px)`
  }

  if (!originalUrl) {
    return <Skeleton height={360} radius="md" aria-label={t`Loading the page`} />
  }

  return (
    <div className="pc-compare" ref={wrapper} aria-busy={loading || undefined} data-loupe={loupe || undefined}>
      <div
        className="pc-compare-viewport"
        ref={viewport}
        onScroll={(event) => setScroll({ x: event.currentTarget.scrollLeft, y: event.currentTarget.scrollTop })}
        onPointerMove={drawLens}
        onPointerLeave={hideLens}
        tabIndex={0}
        role="group"
        aria-label={t`Page preview, scrollable`}
      >
        <div className="pc-compare-content" style={size ? { width: size.w, height: size.h } : undefined}>
          <img
            ref={originalImg}
            className="pc-compare-layer"
            src={originalUrl}
            alt={t`Original page`}
            draggable={false}
            width={size?.w}
            height={size?.h}
            onLoad={(event) => {
              const img = event.currentTarget
              const dpr = window.devicePixelRatio || 1
              needsCentering.current = true
              setSize({ w: img.naturalWidth / dpr, h: img.naturalHeight / dpr })
            }}
          />
          {encodedUrl && size && (
            <div className="pc-compare-layer" style={{ clipPath: `inset(0 0 0 ${clipLeft}px)` }}>
              <img
                ref={encodedImg}
                src={encodedUrl}
                alt={t`Converted page`}
                draggable={false}
                width={size.w}
                height={size.h}
              />
            </div>
          )}
        </div>
      </div>

      <span className="pc-compare-tag" data-side="left">
        {t`Original`}
      </span>
      <span className="pc-compare-tag" data-side="right">
        {t`Converted`}
      </span>

      <div
        className="pc-compare-divider"
        style={{ left: dividerX }}
        role="slider"
        tabIndex={0}
        aria-label={t`Comparison divider`}
        aria-orientation="horizontal"
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={Math.round(split * 100)}
        onKeyDown={onDividerKey}
        onPointerDown={(event) => {
          dragging.current = true
          event.currentTarget.setPointerCapture(event.pointerId)
          moveDivider(event.clientX)
        }}
        onPointerMove={(event) => {
          if (dragging.current) moveDivider(event.clientX)
        }}
        onPointerUp={() => {
          dragging.current = false
        }}
        onPointerCancel={() => {
          dragging.current = false
        }}
      >
        <span className="pc-compare-handle" aria-hidden />
      </div>

      <canvas ref={lens} className="pc-compare-lens" width={LENS} height={LENS} aria-hidden />

      {loading && (
        <span className="pc-compare-busy" role="status">
          <Loader size={14} />
          <VisuallyHidden>{t`Encoding the page`}</VisuallyHidden>
        </span>
      )}
    </div>
  )
}
