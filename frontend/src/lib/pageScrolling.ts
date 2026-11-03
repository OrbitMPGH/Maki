import { onPageScroll } from './pageScroll'

/**
 * Knows whether the page is scrolling right now, so work that can wait (a library refetch during a
 * bulk download) does, and under a mouse turns off pointer hit-testing on the page content.
 *
 * With the wheel turning and the cursor still, every card that slides under it gets :hover (the
 * poster zoom animates layout properties), a `pointerover` the tooltip layer answers with a
 * forced layout, and a series prefetch once it lingers 80 ms. None of that is wanted mid-scroll.
 * Touch has no hover, and there a tap that stops a fling must still land, so phones only get the
 * flag, never the attribute.
 *
 * The attribute goes on `#main` rather than <html>, so toggling it only restyles the page content.
 * Imported for its side effect in main.tsx.
 */

const IDLE_MS = 120

let timer = 0
let main: HTMLElement | null = null

export function isPageScrolling(): boolean {
  return timer !== 0
}

if (typeof window !== 'undefined') {
  const hover = window.matchMedia('(hover: hover) and (pointer: fine)').matches
  onPageScroll(() => {
    if (timer) window.clearTimeout(timer)
    else if (hover) {
      main = document.getElementById('main')
      main?.setAttribute('data-scrolling', '')
    }
    timer = window.setTimeout(() => {
      timer = 0
      main?.removeAttribute('data-scrolling')
      main = null
    }, IDLE_MS)
  })
}
