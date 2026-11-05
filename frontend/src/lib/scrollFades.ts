// Sideways strips (tab rows, the stats range, saved filters) clip on a phone with no hint that more
// sits off screen. This marks each strip's overflowing edges so theme.css can fade them, and on
// first sight scrolls the active item into view so a deep tab is never hidden past the edge.
const STRIPS =
  '.series-tabs, .stats-range, .stats-rail-links, .library-saved-filters, .discover-seeds-row, .discover-taste-chips'
const ACTIVE = '[aria-selected="true"], [data-active="true"], [data-active=""], [aria-current="page"]'

const seen = new WeakSet<HTMLElement>()

// A strip sized to its content can still report a few pixels of overflow from subpixel rounding at
// some zoom levels, which faded the last item of a row that fits (the stats range's "All").
const SLACK = 4

function mark(el: HTMLElement) {
  const max = el.scrollWidth - el.clientWidth
  const start = max > SLACK && el.scrollLeft > 1
  const end = max > SLACK && el.scrollLeft < max - 1
  if (start) el.dataset.fadeStart = ''
  else delete el.dataset.fadeStart
  if (end) el.dataset.fadeEnd = ''
  else delete el.dataset.fadeEnd
}

function revealActive(el: HTMLElement) {
  if (el.scrollWidth <= el.clientWidth) return
  const active = el.querySelector<HTMLElement>(ACTIVE)
  if (!active) return
  const box = el.getBoundingClientRect()
  const item = active.getBoundingClientRect()
  const pad = 24
  if (item.right > box.right - pad) el.scrollLeft += item.right - box.right + pad
  else if (item.left < box.left + pad) el.scrollLeft -= box.left + pad - item.left
}

const resize = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver((entries) => {
  for (const entry of entries) mark(entry.target as HTMLElement)
})

function adopt() {
  for (const el of document.querySelectorAll<HTMLElement>(STRIPS)) {
    if (seen.has(el)) continue
    seen.add(el)
    revealActive(el)
    mark(el)
    resize?.observe(el)
    if (el.firstElementChild) resize?.observe(el.firstElementChild)
  }
}

if (typeof document !== 'undefined') {
  document.addEventListener(
    'scroll',
    (event) => {
      const el = event.target
      if (el instanceof HTMLElement && seen.has(el)) mark(el)
    },
    { capture: true, passive: true },
  )
  let queued = false
  new MutationObserver(() => {
    if (queued) return
    queued = true
    requestAnimationFrame(() => {
      queued = false
      adopt()
    })
  }).observe(document.documentElement, { childList: true, subtree: true })
}
