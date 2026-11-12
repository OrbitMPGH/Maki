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
  const w = Math.min(TAP_WIDTH_MAX, Math.max(TAP_WIDTH_MIN, width)) / 100
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
  if (!invert || action === 'menu') return action
  return action === 'next' ? 'previous' : 'next'
}
