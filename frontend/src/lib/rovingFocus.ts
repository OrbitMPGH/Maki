import type { KeyboardEvent } from 'react'

const STEP: Record<string, number> = { ArrowRight: 1, ArrowDown: 1, ArrowLeft: -1, ArrowUp: -1 }

/**
 * Keyboard for a hand-built tablist or radiogroup, as the ARIA patterns expect: the group is one Tab
 * stop (the selected item carries `tabIndex={0}`, the rest `-1`), arrows and Home/End move between
 * its direct `tab`/`radio` children, and the item focus lands on is selected by clicking it.
 */
export function onRovingKeyDown(e: KeyboardEvent<HTMLElement>) {
  const items = [...e.currentTarget.querySelectorAll<HTMLElement>(':scope > [role="tab"], :scope > [role="radio"]')]
  const from = items.indexOf(document.activeElement as HTMLElement)
  if (from < 0) return
  let to: number
  if (e.key === 'Home') to = 0
  else if (e.key === 'End') to = items.length - 1
  else if (e.key in STEP) {
    const flip = (e.key === 'ArrowLeft' || e.key === 'ArrowRight') && getComputedStyle(e.currentTarget).direction === 'rtl'
    to = (from + STEP[e.key] * (flip ? -1 : 1) + items.length) % items.length
  } else return
  e.preventDefault()
  items[to].focus()
  items[to].click()
}
