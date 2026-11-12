import type { StripPlace } from './stripPosition'

const BOTTOM_EPSILON = 2
/** Pages stepped over looking for the one under the middle of the screen; it is never far from the current one. */
const MAX_STEPS = 24

function clamp01(value: number): number {
  return Number.isFinite(value) ? Math.min(1, Math.max(0, value)) : 0
}

/** Where `scroller` is in the strip, relative to `page`, the page the band tracker calls current. */
export function measureStrip(
  scroller: HTMLElement,
  pages: readonly (HTMLImageElement | null)[],
  page: number,
  count: number,
): StripPlace | null {
  const element = pages[page]
  if (!element || count === 0) return null
  const view = scroller.getBoundingClientRect()
  const box = element.getBoundingClientRect()
  const offset = box.height > 0 ? clamp01((view.top - box.top) / box.height) : 0

  const middle = view.top + view.height / 2
  let index = page
  let rect = box
  for (let step = 0; step < MAX_STEPS && index < count - 1 && rect.bottom < middle; step++) {
    const next = pages[index + 1]
    if (!next) break
    index++
    rect = next.getBoundingClientRect()
  }
  for (let step = 0; step < MAX_STEPS && index > 0 && rect.top > middle; step++) {
    const previous = pages[index - 1]
    if (!previous) break
    index--
    rect = previous.getBoundingClientRect()
  }
  const within = rect.height > 0 ? clamp01((middle - rect.top) / rect.height) : 0
  const atBottom = scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight < BOTTOM_EPSILON
  const progress = atBottom ? 1 : clamp01((index + within) / count)

  return { page, offset, tall: box.height > view.height, progress }
}

/** Scrolls so the point `progress` of the way through the chapter, one equal step per page, is mid-screen. */
export function seekStrip(
  scroller: HTMLElement,
  pages: readonly (HTMLImageElement | null)[],
  count: number,
  progress: number,
): void {
  if (count === 0) return
  const share = clamp01(progress)
  if (share >= 1) {
    scroller.scrollTo({ top: scroller.scrollHeight })
    return
  }
  const index = Math.min(count - 1, Math.floor(share * count))
  const element = pages[index]
  if (!element) return
  const box = element.getBoundingClientRect()
  const view = scroller.getBoundingClientRect()
  const point = box.top - view.top + scroller.scrollTop + box.height * (share * count - index)
  scroller.scrollTo({ top: Math.max(0, point - scroller.clientHeight / 2) })
}
