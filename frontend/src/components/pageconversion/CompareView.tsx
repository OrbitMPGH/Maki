import { useEffect, useRef, useState } from 'react'
import type { KeyboardEvent } from 'react'
import { Loader, SegmentedControl, Skeleton, VisuallyHidden } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'

export type CompareZoom = 'fit' | 'actual'

/**
 * Before and after in one scrollable viewport, at 1:1 pixels or fitted to its width. The divider
 * stays where it is on screen while the page scrolls under it. Holding Space, or the Original
 * button, shows the original across the whole view, which shows artefacts better than a wipe.
 */
export function CompareView({
  originalUrl,
  encodedUrl,
  encodedLabel,
  loading,
}: {
  originalUrl: string | undefined
  encodedUrl: string | undefined
  /** What the right side shows, such as "AVIF, High". */
  encodedLabel: string
  loading: boolean
}) {
  const { t } = useLingui()
  const wrapper = useRef<HTMLDivElement>(null)
  const viewport = useRef<HTMLDivElement>(null)
  const needsCentering = useRef(false)
  const dragging = useRef(false)
  const [natural, setNatural] = useState<{ w: number; h: number } | null>(null)
  const [box, setBox] = useState({ w: 0, h: 0 })
  const [scroll, setScroll] = useState({ x: 0, y: 0 })
  const [split, setSplit] = useState(0.5)
  const [zoom, setZoom] = useState<CompareZoom>('actual')
  const [flipped, setFlipped] = useState(false)

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
    // Space still presses a focused button or option; anywhere else it shows the original.
    const usesSpace = (target: EventTarget | null) =>
      target instanceof HTMLElement &&
      (target.isContentEditable ||
        /^(INPUT|TEXTAREA|SELECT|BUTTON|A)$/.test(target.tagName) ||
        /^(button|radio|checkbox|option|switch|menuitem|tab)$/.test(target.getAttribute('role') ?? ''))
    const down = (event: globalThis.KeyboardEvent) => {
      if (event.code !== 'Space' || usesSpace(event.target)) return
      event.preventDefault()
      setFlipped(true)
    }
    const up = (event: globalThis.KeyboardEvent) => {
      if (event.code === 'Space') setFlipped(false)
    }
    const blur = () => setFlipped(false)
    window.addEventListener('keydown', down)
    window.addEventListener('keyup', up)
    window.addEventListener('blur', blur)
    return () => {
      window.removeEventListener('keydown', down)
      window.removeEventListener('keyup', up)
      window.removeEventListener('blur', blur)
    }
  }, [])

  const scale = natural && zoom === 'fit' && box.w > 0 ? Math.min(1, box.w / natural.w) : 1
  const size = natural ? { w: natural.w * scale, h: natural.h * scale } : null

  useEffect(() => {
    const el = viewport.current
    if (!natural || !el || !needsCentering.current) return
    needsCentering.current = false
    el.scrollLeft = Math.max(0, (natural.w * scale - el.clientWidth) / 2)
    el.scrollTop = Math.max(0, (natural.h * scale - el.clientHeight) / 2)
    setScroll({ x: el.scrollLeft, y: el.scrollTop })
  }, [natural, scale])

  const offsetX = size ? Math.max(0, (box.w - size.w) / 2) : 0
  const dividerX = split * box.w
  const clipLeft = flipped ? (size?.w ?? 0) : Math.min(Math.max(scroll.x + dividerX - offsetX, 0), size?.w ?? 0)

  const changeZoom = (next: CompareZoom) => {
    const el = viewport.current
    if (el && natural && box.w > 0) {
      // Keep the middle of the view on the same spot of the page.
      const from = zoom === 'fit' ? Math.min(1, box.w / natural.w) : 1
      const to = next === 'fit' ? Math.min(1, box.w / natural.w) : 1
      const cy = (el.scrollTop + el.clientHeight / 2) / from
      const cx = (el.scrollLeft + el.clientWidth / 2) / from
      requestAnimationFrame(() => {
        el.scrollTop = Math.max(0, cy * to - el.clientHeight / 2)
        el.scrollLeft = Math.max(0, cx * to - el.clientWidth / 2)
        setScroll({ x: el.scrollLeft, y: el.scrollTop })
      })
    }
    setZoom(next)
  }

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

  if (!originalUrl) {
    return <Skeleton height={360} radius="md" aria-label={t`Loading the page`} />
  }

  return (
    <div className="pc-compare" ref={wrapper} aria-busy={loading || undefined}>
      <div
        className="pc-compare-viewport"
        ref={viewport}
        onScroll={(event) => setScroll({ x: event.currentTarget.scrollLeft, y: event.currentTarget.scrollTop })}
        tabIndex={0}
        role="group"
        aria-label={t`Page preview, scrollable`}
      >
        <div className="pc-compare-content" style={size ? { width: size.w, height: size.h } : undefined}>
          <img
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
              setNatural({ w: img.naturalWidth / dpr, h: img.naturalHeight / dpr })
            }}
          />
          {encodedUrl && size && (
            <div className="pc-compare-layer" style={{ clipPath: `inset(0 0 0 ${clipLeft}px)` }}>
              <img src={encodedUrl} alt={t`Converted page`} draggable={false} width={size.w} height={size.h} />
            </div>
          )}
        </div>
      </div>

      <span className="pc-compare-tag" data-side="left">
        {t`Original`}
      </span>
      {!flipped && (
        <span className="pc-compare-tag" data-side="right">
          {encodedLabel}
        </span>
      )}

      {!flipped && (
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
      )}

      <div className="pc-compare-tools">
        <SegmentedControl
          size="xs"
          aria-label={t`Zoom`}
          value={zoom}
          onChange={(next) => changeZoom(next as CompareZoom)}
          data={[
            { value: 'fit', label: t`Fit` },
            { value: 'actual', label: t`100%` },
          ]}
        />
        <button
          type="button"
          className="pc-compare-flip"
          aria-pressed={flipped}
          onPointerDown={(event) => {
            event.currentTarget.setPointerCapture(event.pointerId)
            setFlipped(true)
          }}
          onPointerUp={() => setFlipped(false)}
          onPointerCancel={() => setFlipped(false)}
          onKeyDown={(event) => {
            if (event.key !== 'Enter' && event.key !== ' ') return
            event.preventDefault()
            setFlipped(true)
          }}
          onKeyUp={(event) => {
            if (event.key === 'Enter' || event.key === ' ') setFlipped(false)
          }}
        >
          <Trans>Hold for original</Trans>
          <kbd className="pc-compare-kbd">
            <Trans>Space</Trans>
          </kbd>
        </button>
      </div>

      {loading && (
        <span className="pc-compare-busy" role="status">
          <Loader size={14} />
          <VisuallyHidden>{t`Encoding the page`}</VisuallyHidden>
        </span>
      )}
    </div>
  )
}
