import { useCallback, useLayoutEffect, useState } from 'react'

/**
 * Row windowing for the library's grid and list views.
 *
 * `.cover-card` / `.series-row` already carry `content-visibility: auto`, which is what keeps
 * *painting* a big library cheap — the browser skips layout and paint for anything off screen.
 * What that can't skip is React: every card is still a component instance and ~18 DOM nodes, so a
 * 2,000-series library commits ~36,000 nodes on the first render of the page and holds them for
 * as long as it is mounted. This hook is the other half — it keeps the off-screen rows out of the
 * tree entirely, replacing them with padding on a wrapper element.
 *
 * The trade is real and is why this is thresholded rather than always on: with rows unmounted,
 * the browser's own Ctrl+F can only find what is currently rendered. Below
 * {@link WINDOW_MIN_ITEMS} nothing is windowed, so the common library keeps find-in-page working
 * exactly as before; above it the page's own filter box is the practical way to find a series
 * anyway, and mount cost is the thing that actually hurts.
 *
 * Measured, not configured: the row height comes from the first rendered child and the column
 * count from the container's resolved `grid-template-columns`, so Mantine's responsive `cols`
 * breakpoints and the three density presets need no duplicate table here. Everything the hook
 * measures is a function of layout only, never of the range it produces, which is what keeps the
 * ResizeObserver from feeding back into itself.
 */

/** Item count at which windowing switches on. Below this the full list renders. */
export const WINDOW_MIN_ITEMS = 600

/** Rows rendered beyond the viewport on each side, so a fast scroll doesn't reach empty space. */
const OVERSCAN_ROWS = 3

/**
 * Items rendered before anything has been measured. Enough to fill a tall viewport at the densest
 * grid (10 columns), and enough to guarantee there is a child for `measure` to read.
 */
const INITIAL_ITEMS = 80

interface Metrics {
  /** Columns in the container, 1 for the list view. */
  perRow: number
  /** Height of one row in pixels, excluding the gap below it. */
  rowHeight: number
  /** Row gap between two rendered rows. */
  gap: number
}

const UNMEASURED: Metrics = { perRow: 1, rowHeight: 0, gap: 0 }

export interface WindowedRows {
  /**
   * Attach to the padded wrapper. Its top edge is the origin row 0 is measured from, which is why
   * it has to be the element that does *not* carry the items.
   */
  outerRef: (el: HTMLDivElement | null) => void
  /**
   * Attach to the element that lays the items out (the grid or the stack). Passed explicitly
   * rather than derived from the wrapper's first child: Mantine's SimpleGrid emits a `<style>`
   * element for its responsive column variables as a *sibling* of its root, so the wrapper's
   * first child is that style tag, not the grid.
   */
  innerRef: (el: HTMLDivElement | null) => void
  /** Render `items.slice(start, end)`. */
  start: number
  end: number
  /** Stand-in height for the rows above and below the rendered slice. */
  padTop: number
  padBottom: number
}

/**
 * @param count Number of items the full list would render.
 * @param enabled Pass false to render everything (small library, or a view that opts out).
 */
export function useWindowedRows(count: number, enabled: boolean): WindowedRows {
  // Both elements live in state rather than refs so that swapping views (grid ↔ list mounts a
  // different pair) re-runs the subscription against the new ones. A ref would leave the
  // ResizeObserver watching the unmounted element.
  const [wrapper, setWrapper] = useState<HTMLDivElement | null>(null)
  const [container, setContainer] = useState<HTMLDivElement | null>(null)
  const [metrics, setMetrics] = useState<Metrics>(UNMEASURED)
  const [rows, setRows] = useState<[number, number]>([0, 0])

  /**
   * Re-reads the layout facts. Deliberately does not look at the wrapper's own height — that
   * moves with the padding this hook writes, and reading it here would make the ResizeObserver
   * chase its own tail.
   */
  const measure = useCallback(() => {
    const grid = container
    const first = grid?.firstElementChild
    if (!grid || !(first instanceof HTMLElement)) return

    const style = getComputedStyle(grid)
    const next: Metrics = {
      perRow:
        style.display === 'grid'
          ? Math.max(1, style.gridTemplateColumns.split(' ').filter(Boolean).length)
          : 1,
      rowHeight: first.offsetHeight,
      gap: parseFloat(style.rowGap) || 0,
    }
    setMetrics((prev) =>
      prev.perRow === next.perRow && prev.rowHeight === next.rowHeight && prev.gap === next.gap
        ? prev
        : next,
    )
  }, [container])

  /** Picks the row range covering the viewport plus overscan. */
  const recompute = useCallback(() => {
    const stride = metrics.rowHeight + metrics.gap
    if (!wrapper || stride <= 0) return

    const lastIndex = Math.max(0, Math.ceil(count / metrics.perRow) - 1)
    // The wrapper's top edge is where row 0 starts and does not move when the padding inside it
    // changes, unlike the inner grid's — which is why the ref goes on the wrapper.
    const top = wrapper.getBoundingClientRect().top + window.scrollY
    const first = Math.min(
      Math.max(0, Math.floor((window.scrollY - top) / stride) - OVERSCAN_ROWS),
      lastIndex,
    )
    const last = Math.min(
      Math.max(
        first,
        Math.ceil((window.scrollY + window.innerHeight - top) / stride) + OVERSCAN_ROWS,
      ),
      lastIndex,
    )
    setRows((prev) => (prev[0] === first && prev[1] === last ? prev : [first, last]))
  }, [count, metrics, wrapper])

  useLayoutEffect(() => {
    if (!enabled || !wrapper || !container) return

    measure()
    recompute()

    // Coalesce to one recompute per frame: a scroll fires far more often than it can paint.
    let frame = 0
    const onScroll = () => {
      if (frame) return
      frame = requestAnimationFrame(() => {
        frame = 0
        recompute()
      })
    }
    const onResize = () => {
      measure()
      recompute()
    }

    // The grid's *width* is what changes the column count and the card height; its height also
    // changes as the rendered slice moves, which is harmless because `measure` reads neither.
    const observer = new ResizeObserver(onResize)
    observer.observe(container)

    window.addEventListener('scroll', onScroll, { passive: true })
    window.addEventListener('resize', onResize)
    return () => {
      if (frame) cancelAnimationFrame(frame)
      observer.disconnect()
      window.removeEventListener('scroll', onScroll)
      window.removeEventListener('resize', onResize)
    }
  }, [enabled, wrapper, container, measure, recompute])

  const refs = { outerRef: setWrapper, innerRef: setContainer }

  if (!enabled) {
    return { ...refs, start: 0, end: count, padTop: 0, padBottom: 0 }
  }

  const stride = metrics.rowHeight + metrics.gap
  if (stride <= 0) {
    // First pass: render enough to fill the viewport and to give `measure` something to read.
    return { ...refs, start: 0, end: Math.min(count, INITIAL_ITEMS), padTop: 0, padBottom: 0 }
  }

  const lastIndex = Math.max(0, Math.ceil(count / metrics.perRow) - 1)
  // Clamped here as well as in `recompute`: a filter that shrinks the list re-renders with the
  // previous range still in state, and an unclamped `start` past the end would render nothing.
  const lastRow = Math.min(rows[1], lastIndex)
  const firstRow = Math.min(rows[0], lastRow)
  return {
    ...refs,
    start: firstRow * metrics.perRow,
    end: Math.min(count, (lastRow + 1) * metrics.perRow),
    padTop: firstRow * stride,
    // One stride per hidden row below: the grid renders no gap after the last visible row, so
    // each skipped row owes its own gap as well as its height.
    padBottom: Math.max(0, (lastIndex - lastRow) * stride),
  }
}
