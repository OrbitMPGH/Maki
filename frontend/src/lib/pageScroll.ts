/**
 * Which box the page scrolls in.
 *
 * Normally the window. In the installed iOS app it is the shell's <main>: iPadOS shows whatever has
 * scrolled past the top of the document behind its status bar, and nothing the page draws can cover
 * it, because fixed and pinned layers are clipped at the window's edge. A document that never
 * scrolls has nothing up there. Imported first thing in main.tsx so the attribute the stylesheet
 * keys off is set before the first render.
 *
 * `localStorage['inner-scroll'] = '1'` turns it on anywhere, for testing in a desktop browser.
 */
function detectInnerScroll(): boolean {
  if ((navigator as Navigator & { standalone?: boolean }).standalone === true) return true
  try {
    return localStorage.getItem('inner-scroll') === '1'
  } catch {
    return false
  }
}

const inner = detectInnerScroll()
if (inner) document.documentElement.dataset.scroll = 'inner'

/** The scrolling <main>, or null when the window scrolls (or the shell isn't mounted). */
function scroller(): HTMLElement | null {
  return inner ? document.getElementById('main') : null
}

export function pageScrollTop(): number {
  return scroller()?.scrollTop ?? window.scrollY
}

export function pageViewportHeight(): number {
  return scroller()?.clientHeight ?? window.innerHeight
}

export function scrollPageTo(top: number): void {
  const el = scroller()
  if (el) el.scrollTo(0, top)
  else window.scrollTo(0, top)
}

/**
 * Calls `listener` when the page scrolls. Listens in the capture phase on the window, because a
 * scroll on an element doesn't bubble and <main> mounts and unmounts with the shell.
 */
export function onPageScroll(listener: () => void): () => void {
  const handler = (e: Event) => {
    const el = scroller()
    if (el ? e.target === el : e.target === document) listener()
  }
  window.addEventListener('scroll', handler, { capture: true, passive: true })
  return () => window.removeEventListener('scroll', handler, { capture: true })
}
