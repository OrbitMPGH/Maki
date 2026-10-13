import { useRef, useState } from 'react'
import { flushSync } from 'react-dom'
import type { KeyboardEvent, PointerEvent } from 'react'

export type ColumnSpec = { width: number; min: number; max?: number }

const KEY_STEP = 16
const DEFAULT_MAX = 640

function clampWidth(spec: ColumnSpec, width: number) {
  return Math.round(Math.min(spec.max ?? DEFAULT_MAX, Math.max(spec.min, width)))
}

function readWidths<K extends string>(storageKey: string, specs: Record<K, ColumnSpec>): Record<K, number> {
  const widths = {} as Record<K, number>
  let stored: Partial<Record<K, unknown>> = {}
  try {
    const parsed: unknown = JSON.parse(localStorage.getItem(storageKey) ?? '{}')
    if (parsed && typeof parsed === 'object') stored = parsed as Partial<Record<K, unknown>>
  } catch {
    /* unreadable or blocked storage falls back to the defaults */
  }
  for (const key of Object.keys(specs) as K[]) {
    const value = stored[key]
    widths[key] = typeof value === 'number' && Number.isFinite(value) ? clampWidth(specs[key], value) : specs[key].width
  }
  return widths
}

export type LiveResize<K extends string> = (widths: Record<K, number>, key: K, shift: number) => void

/**
 * Per-device column widths for a table with drag handles in its header. Dragging, arrow keys on a
 * focused handle and a double-click to reset all go through `handleProps`. Only widths that differ
 * from the default are written back, so changing a default later still reaches everyone who never
 * touched that column.
 *
 * A drag never renders: it writes the header cell's width straight to the DOM, once a frame, and
 * commits to state on release. Re-rendering a page-sized table per pointer move is what made this
 * lag. `onLive` gets the in-flight widths and how far the dragged column has grown since the drag
 * began, for anything else that has to follow along without a render; it is called once more with
 * a shift of 0 after the commit.
 */
export function useColumnWidths<K extends string>(
  storageKey: string,
  specs: Record<K, ColumnSpec>,
  onLive?: LiveResize<K>,
) {
  const [widths, setWidths] = useState(() => readWidths(storageKey, specs))
  const latest = useRef(widths)
  latest.current = widths
  const live = useRef(onLive)
  live.current = onLive

  const persist = (next: Record<K, number>) => {
    const changed: Partial<Record<K, number>> = {}
    for (const key of Object.keys(specs) as K[]) {
      if (next[key] !== specs[key].width) changed[key] = next[key]
    }
    try {
      if (Object.keys(changed).length === 0) localStorage.removeItem(storageKey)
      else localStorage.setItem(storageKey, JSON.stringify(changed))
    } catch {
      /* a lost preference is not an error */
    }
  }

  const setWidth = (key: K, width: number) => {
    const next = { ...latest.current, [key]: clampWidth(specs[key], width) }
    latest.current = next
    setWidths(next)
    persist(next)
  }

  // The column's laid-out width, which can be wider than what was asked for when its content
  // doesn't fit. Drags start from it so the divider never has to cross a dead zone first.
  const renderedWidth = (key: K, handle: HTMLElement) =>
    handle.parentElement?.getBoundingClientRect().width ?? latest.current[key]

  /**
   * `edge` is the side of the header cell the handle sits on. A column right of the table's
   * flexible column is resized from its start edge: growing it from the end would only take the
   * space back from the flexible column, and the divider would stay where it was.
   */
  const handleProps = (key: K, edge: 'start' | 'end' = 'end') => {
    const sign = edge === 'start' ? -1 : 1
    return {
      role: 'separator' as const,
      'aria-orientation': 'vertical' as const,
      'aria-valuenow': widths[key],
      'aria-valuemin': specs[key].min,
      'aria-valuemax': specs[key].max ?? DEFAULT_MAX,
      tabIndex: 0,
      className: 'column-resize-handle',
      'data-edge': edge,
      onClick: (e: { stopPropagation: () => void }) => e.stopPropagation(),
      onDoubleClick: () => setWidth(key, specs[key].width),
      onKeyDown: (e: KeyboardEvent<HTMLElement>) => {
        if (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight') return
        e.preventDefault()
        const step = (e.key === 'ArrowRight' ? KEY_STEP : -KEY_STEP) * sign
        setWidth(key, renderedWidth(key, e.currentTarget) + step)
      },
      onPointerDown: (e: PointerEvent<HTMLElement>) => {
        if (e.button !== 0) return
        e.preventDefault()
        const handle = e.currentTarget
        const cell = handle.parentElement
        if (!cell) return
        const startX = e.clientX
        const startWidth = renderedWidth(key, handle)
        let current = { ...latest.current }
        let pendingX: number | null = null
        let frame = 0

        const apply = () => {
          frame = 0
          if (pendingX === null) return
          const width = clampWidth(specs[key], startWidth + (pendingX - startX) * sign)
          pendingX = null
          current = { ...current, [key]: width }
          cell.style.width = `${width}px`
          live.current?.(current, key, cell.getBoundingClientRect().width - startWidth)
        }
        const onMove = (ev: globalThis.PointerEvent) => {
          pendingX = ev.clientX
          if (!frame) frame = requestAnimationFrame(apply)
        }
        const onEnd = () => {
          handle.removeEventListener('pointermove', onMove)
          handle.removeEventListener('pointerup', onEnd)
          handle.removeEventListener('pointercancel', onEnd)
          if (frame) cancelAnimationFrame(frame)
          apply()
          document.body.classList.remove('column-resizing')
          delete handle.dataset.resizing
          latest.current = current
          // Synchronous, so whatever re-measures off the committed widths has done so before the
          // live adjustments are taken back off.
          flushSync(() => setWidths(current))
          persist(current)
          live.current?.(current, key, 0)
        }

        handle.setPointerCapture(e.pointerId)
        document.body.classList.add('column-resizing')
        handle.dataset.resizing = 'true'
        handle.addEventListener('pointermove', onMove)
        handle.addEventListener('pointerup', onEnd)
        handle.addEventListener('pointercancel', onEnd)
      },
    }
  }

  return { widths, handleProps }
}
