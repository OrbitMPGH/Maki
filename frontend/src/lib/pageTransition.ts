import { useLayoutEffect } from 'react'
import { UNSAFE_createBrowserHistory, useLocation } from 'react-router-dom'
import { holdTint, tintSettled } from './coverTint'
import { motionEnabled, motionPace, prefersReducedMotion } from './motionPrefs'
import { pageScroller } from './pageScroll'

/**
 * Cross-fades between pages with the View Transitions API.
 *
 * BrowserRouter has no hook for this (React Router only offers `viewTransition` on the data
 * routers), so the app runs on its own history object and delays each route change until the
 * browser has captured the old page. The router's listener then runs inside the transition's
 * update callback, which waits for React to commit the new route before the new page is captured.
 * The scroll reset in ScrollMemory happens inside that window too, so the old page fades out where
 * the reader left it rather than jumping to the top first.
 *
 * Opening a series from a cover grows that cover into the series page's poster, and going back
 * shrinks the poster into the same cover if it is on screen. The pairing is found in the DOM (the
 * clicked link's image, the visible link to that series) rather than wired into each card, and the
 * flight is a copy of the cover laid out in the page itself (see flyCover). A cover that is not a
 * link to its series takes `data-series-cover={id}` to join in.
 *
 * Only a change of path animates. Filters, tabs and modals ride on search and state, and those
 * stay instant. Both the fade and the flying cover can be switched off per device under Settings >
 * Appearance (lib/motionPrefs.ts).
 */

export const appHistory = UNSAFE_createBrowserHistory({ v5Compat: true })

// A lazy route can hold the commit back while its chunk loads, and the series poster waits on its
// data. Past this the transition gives up waiting and the new page arrives whenever it is ready.
const COMMIT_TIMEOUT_MS = 400

const EASE = 'cubic-bezier(0.16, 1, 0.3, 1)'

const disabled = () => !motionEnabled('pageTransitions') || prefersReducedMotion()

// The reader pages between chapters with its own motion, and a fade on top of it reads as lag.
const isReader = (path: string) => path.startsWith('/read/')

const seriesId = (path: string) => /^\/series\/(\d+)\/?$/.exec(path)?.[1] ?? null

let shownPath = appHistory.location.pathname
let settle: (() => void) | null = null

let lastClick: { target: Element; at: number } | null = null
document.addEventListener(
  'click',
  (e) => {
    lastClick = e.target instanceof Element ? { target: e.target, at: performance.now() } : null
  },
  true,
)

function onScreen(el: Element): boolean {
  const r = el.getBoundingClientRect()
  return r.width > 0 && r.bottom > 0 && r.top < window.innerHeight && r.right > 0 && r.left < window.innerWidth
}

/**
 * The box a cover inside `link` occupies: the nearest clipping ancestor with rounded corners, which
 * is the card's visible shape rather than its image, whose hover zoom overflows it.
 */
function coverIn(link: HTMLElement): HTMLElement | null {
  const img = link instanceof HTMLImageElement ? link : link.querySelector('img')
  if (!img) return null
  let fallback: HTMLElement | null = null
  for (let el: HTMLElement | null = img.parentElement; el && link.contains(el); el = el.parentElement) {
    const style = getComputedStyle(el)
    if (style.overflow === 'visible') continue
    if (parseFloat(style.borderTopLeftRadius) > 0) return el
    fallback ??= el
  }
  return fallback ?? img
}

const linkPath = (el: Element) => (el instanceof HTMLAnchorElement ? new URL(el.href).pathname : '')

/**
 * Links to the series page (search and hash aside), plus covers marked `data-series-cover` for the
 * places whose cover is not one: a thumbnail beside a title link, a cover that opens the reader.
 */
function coversFor(id: string, within: ParentNode): HTMLElement[] {
  const els = within.querySelectorAll<HTMLElement>(`a[href^="/series/${id}"], [data-series-cover="${id}"]`)
  return [...els].filter((el) => el.dataset.seriesCover === id || seriesId(linkPath(el)) === id)
}

function visibleCover(els: HTMLElement[]): HTMLElement | null {
  for (const el of els) {
    const cover = coverIn(el)
    if (cover && onScreen(cover)) return cover
  }
  return null
}

const linkedCover = (id: string) => visibleCover(coversFor(id, document))

function heroPoster(): HTMLImageElement | null {
  const img = document.querySelector<HTMLImageElement>('img.series-hero-poster')
  return img && onScreen(img) ? img : null
}

/**
 * The cover to grow from when leaving for `/series/id`: the one just clicked, or the cover for that
 * series nearest the click, which covers a title link beside its thumbnail, an "Open series" button
 * or menu item, and rows that navigate from script.
 */
function clickedCover(id: string): HTMLElement | null {
  if (!lastClick || performance.now() - lastClick.at > 1000) return null
  const link = lastClick.target.closest('a[href]')
  if (link && seriesId(linkPath(link)) !== id) return null
  for (let el = lastClick.target.parentElement; el; el = el.parentElement) {
    const cover = visibleCover(coversFor(id, el))
    if (cover) return cover
  }
  return null
}

// No animation frame runs while the update callback is pending, so waits inside it use timers.
const tick = (ms = 0) => new Promise<void>((resolve) => setTimeout(resolve, ms))

/** Waits for the series poster to render and decode, so the cover lands on a real image. */
async function posterReady(deadline: number): Promise<HTMLImageElement | null> {
  while (performance.now() < deadline) {
    const img = heroPoster()
    if (img) {
      await Promise.race([img.decode().catch(() => {}), tick(deadline - performance.now())])
      return img.complete ? img : null
    }
    await tick(16)
  }
  return null
}

/**
 * Rails and list rows skip layout while off screen (`content-visibility: auto`) and stand in at a
 * guessed height until the browser first decides whether they are on screen. It decides at the next
 * rendering, which a transition holds back until the new page is captured, so everything measured
 * meanwhile (the restored scroll offset, where the cover lands) and the capture itself used the
 * guesses, and Home then collapsed by the difference. The attribute lays them out for real until a
 * frame after the capture; after that each remembers its real height when it is skipped again.
 */
let settling = 0
function settleLayout(): number {
  document.documentElement.dataset.pageSettling = ''
  return ++settling
}
/** Only the latest transition's release counts, so an earlier one can't pull it out from under it. */
function unsettleLayout(token: number) {
  if (token === settling) delete document.documentElement.dataset.pageSettling
}

/** The new page drifts up into place while it fades in. Without the API it fades in alone. */
function rise(fade: boolean) {
  const main = document.getElementById('main')
  if (!main) return
  main.animate(
    fade
      ? [{ opacity: 0, transform: 'translateY(6px)' }, { opacity: 1, transform: 'none' }]
      : [{ transform: 'translateY(6px)' }, { transform: 'none' }],
    { duration: 250 * motionPace(), easing: EASE },
  )
}

/**
 * Flies a copy of the cover from `from` (where it was on screen) into `target`, which stays hidden
 * until it lands.
 *
 * The copy lives in the page, not in the view transition's layer. That layer sits above everything
 * and is pinned to the viewport, so a flying snapshot covered the top bar and the sticky toolbars
 * and had to be chased from script as the page scrolled under it. Positioned in page coordinates at
 * z-index 1, the copy scrolls with the content for free, passes under the bars, and moves on a
 * transform alone, which the compositor keeps running while the new page is busy rendering.
 *
 * Built inside the update callback so the new page is captured with the copy at its start, and
 * started from the returned function once the transition is ready.
 */
function flyCover(
  from: DOMRect,
  target: HTMLElement,
): { decoded: Promise<unknown>; start: () => void; cancel: () => void } | null {
  const img = target instanceof HTMLImageElement ? target : target.querySelector('img')
  // A lazy card that has just remounted hasn't asked for its image yet, so currentSrc is still empty.
  const src = img?.currentSrc || img?.src
  if (!src) return null
  // The installed iOS app scrolls <main> rather than the window (lib/pageScroll.ts), so the copy has
  // to sit in <main> to move with it, and <main> has to contain it for that.
  const scroller = pageScroller()
  const host = scroller ?? document.body
  const restorePosition = scroller && getComputedStyle(scroller).position === 'static'
  if (restorePosition) scroller.style.position = 'relative'
  const origin = scroller
    ? (() => {
        const r = scroller.getBoundingClientRect()
        return { left: r.left - scroller.scrollLeft, top: r.top - scroller.scrollTop }
      })()
    : { left: -window.scrollX, top: -window.scrollY }

  const to = target.getBoundingClientRect()
  const ghost = document.createElement('img')
  ghost.className = 'cover-ghost'
  ghost.src = src
  ghost.alt = ''
  Object.assign(ghost.style, {
    left: `${to.left - origin.left}px`,
    top: `${to.top - origin.top}px`,
    width: `${to.width}px`,
    height: `${to.height}px`,
    borderRadius: getComputedStyle(target).borderTopLeftRadius,
  })
  const launch = `translate(${from.left - to.left}px, ${from.top - to.top}px) scale(${from.width / to.width}, ${from.height / to.height})`
  ghost.style.transform = launch
  host.append(ghost)
  target.style.visibility = 'hidden'

  let ended = false
  const end = () => {
    if (ended) return
    ended = true
    target.style.removeProperty('visibility')
    if (restorePosition) scroller.style.removeProperty('position')
    // The card underneath carries its title and badges, which the copy doesn't; a short fade hands over.
    ghost.animate([{ opacity: 1 }, { opacity: 0 }], { duration: 140 * motionPace(), easing: 'ease-out' }).finished.finally(() => ghost.remove())
  }
  return {
    decoded: ghost.decode().catch(() => {}),
    start: () => {
      ghost.style.transform = 'none'
      ghost.animate([{ transform: launch }, { transform: 'none' }], { duration: 440 * motionPace(), easing: EASE }).finished.finally(end)
    },
    cancel: () => {
      ghost.remove()
      ended = true
      target.style.removeProperty('visibility')
      if (restorePosition) scroller.style.removeProperty('position')
    },
  }
}

const listen = appHistory.listen
appHistory.listen = (listener) =>
  listen((update) => {
    const from = shownPath
    const to = update.location.pathname
    shownPath = to
    if (from === to || (isReader(from) && isReader(to)) || disabled()) {
      listener(update)
      return
    }
    if (!document.startViewTransition) {
      listener(update)
      settle = () => {
        settle = null
        rise(true)
      }
      return
    }

    const opening = seriesId(to)
    const leaving = seriesId(from)
    // The cross-fade's length lives in theme.css, keyed off this.
    const root = document.documentElement
    if (motionEnabled('fastMotion')) root.dataset.motion = 'fast'
    else delete root.dataset.motion
    const morph = motionEnabled('coverMorph')
    const outgoing = !morph ? null : opening ? clickedCover(opening) : leaving ? heroPoster() : null
    const fromRect = outgoing?.getBoundingClientRect()
    let flight: ReturnType<typeof flyCover> = null
    let settled = 0
    // Set once the browser skips the transition: a hidden tab, or another navigation cutting in. The
    // skip can land before the callback has built the flight, which would then never start or clear.
    let skipped = false

    let releaseTint = () => {}
    const transition = document.startViewTransition(() => {
      releaseTint = holdTint()
      listener(update)
      settle?.()
      const deadline = performance.now() + COMMIT_TIMEOUT_MS
      const committed = new Promise<void>((resolve) => {
        const done = () => {
          window.clearTimeout(timer)
          if (settle === done) settle = null
          resolve()
        }
        // Out of patience: the transition plays with the old page standing, so the new one fades in
        // when it does commit rather than popping in.
        const timer = window.setTimeout(() => {
          done()
          settle = () => {
            settle = null
            rise(true)
          }
        }, COMMIT_TIMEOUT_MS)
        settle = done
      })
      return committed.then(async () => {
        settled = settleLayout()
        // Lets the commit's passive effects run first, ScrollMemory's restore among them, so the
        // new page is captured where it will sit.
        await tick()
        let incoming: HTMLElement | null = null
        if (outgoing && opening) incoming = await posterReady(deadline)
        else if (outgoing && leaving) incoming = linkedCover(leaving)
        if (fromRect && incoming && !skipped) flight = flyCover(fromRect, incoming)
        if (!flight) rise(false)
        // The flight's image, and the new cover's tint so it can ease in with the cross-fade.
        await Promise.race([
          Promise.all([flight?.decoded, tintSettled()]),
          tick(Math.max(0, deadline - performance.now())),
        ])
        if (skipped) flight?.cancel()
      })
    })
    transition.ready.then(
      () => {
        flight?.start()
        releaseTint()
      },
      () => {
        skipped = true
        flight?.cancel()
        releaseTint()
      },
    )
    transition.finished.finally(() => {
      releaseTint()
      // A couple of frames past the capture, so every rail has been laid out once and remembers it.
      requestAnimationFrame(() => requestAnimationFrame(() => unsettleLayout(settled)))
    })
  })

/** Rendered inside the router: tells a pending transition the new route has committed. */
export function PageTransitionCommit() {
  const { key } = useLocation()
  useLayoutEffect(() => {
    settle?.()
  }, [key])
  return null
}
