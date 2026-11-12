import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'

/** How the page is divided into tap zones in the paged layouts. */
export type TapLayout = 'sides' | 'l-shape' | 'kindle' | 'edges'
export type TapAction = 'next' | 'previous' | 'menu'

export const TAP_LAYOUTS: { value: TapLayout; label: MessageDescriptor; hint: MessageDescriptor }[] = [
  { value: 'sides', label: msg`Sides`, hint: msg`One side goes back, the other goes forward.` },
  { value: 'l-shape', label: msg`L-shape`, hint: msg`The top and one side go back, the bottom and the other side go forward.` },
  { value: 'kindle', label: msg`Kindle`, hint: msg`Most of the page goes forward. A strip on one side goes back, the top opens the bars.` },
  { value: 'edges', label: msg`Edges`, hint: msg`Both edges go forward and the bottom middle goes back, for reading with one hand.` },
]

export const TAP_WIDTH_MIN = 15
export const TAP_WIDTH_MAX = 45

/**
 * What a tap does at `x`, `y` (fractions of the page area, 0 to 1). The layouts are drawn for
 * left-to-right reading; right-to-left mirrors them, so forward is always on the side reading moves
 * towards. `width` is the side zones' share of the page in percent; `invert` swaps back and forward.
 */
export function tapActionAt(
  layout: TapLayout,
  width: number,
  invert: boolean,
  rtl: boolean,
  x: number,
  y: number,
): TapAction {
  const w = clampWidth(width) / 100
  const left = rtl ? x > 1 - w : x < w
  const right = rtl ? x < w : x > 1 - w
  const top = y < w
  const bottom = y > 1 - w
  let action: TapAction
  switch (layout) {
    case 'l-shape':
      action = top || (!bottom && left) ? 'previous' : bottom || right ? 'next' : 'menu'
      break
    case 'kindle':
      action = top ? 'menu' : left ? 'previous' : 'next'
      break
    case 'edges':
      action = left || right ? 'next' : bottom ? 'previous' : 'menu'
      break
    default:
      action = left ? 'previous' : right ? 'next' : 'menu'
  }
  return swapped(action, invert)
}

/** Continuous has no layouts: a band at the top goes back, one at the bottom goes forward. `width` is each band's height. */
export function stripTapActionAt(width: number, invert: boolean, y: number): TapAction {
  const w = clampWidth(width) / 100
  return swapped(y < w ? 'previous' : y > 1 - w ? 'next' : 'menu', invert)
}

export interface TapZoneCell {
  key: string
  x: number
  y: number
  width: number
  height: number
  action: TapAction
}

/**
 * The zones as cells over the page area, in fractions. Every paged boundary sits at the side width
 * or its mirror, so the 3 x 3 grid those lines make is exact; Continuous is three full-width rows.
 */
export function tapZoneCells(
  prefs: { tapLayout: TapLayout; tapWidth: number; tapInvert: boolean; direction: string },
  vertical: boolean,
): TapZoneCell[] {
  const w = clampWidth(prefs.tapWidth) / 100
  const stops = [0, w, 1 - w, 1]
  const cells: TapZoneCell[] = []
  for (let row = 0; row < 3; row++) {
    const y = stops[row]
    const height = stops[row + 1] - y
    if (vertical) {
      cells.push({ key: `${row}`, x: 0, y, width: 1, height, action: stripTapActionAt(prefs.tapWidth, prefs.tapInvert, y + height / 2) })
      continue
    }
    for (let col = 0; col < 3; col++) {
      const x = stops[col]
      const width = stops[col + 1] - x
      const action = tapActionAt(
        prefs.tapLayout,
        prefs.tapWidth,
        prefs.tapInvert,
        prefs.direction === 'rtl',
        x + width / 2,
        y + height / 2,
      )
      cells.push({ key: `${row}-${col}`, x, y, width, height, action })
    }
  }
  return cells
}

function clampWidth(width: number): number {
  return Math.min(TAP_WIDTH_MAX, Math.max(TAP_WIDTH_MIN, width))
}

function swapped(action: TapAction, invert: boolean): TapAction {
  if (!invert || action === 'menu') return action
  return action === 'next' ? 'previous' : 'next'
}
