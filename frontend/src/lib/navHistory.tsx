import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from 'react'
import { useLocation, useNavigate, useNavigationType, type Location } from 'react-router-dom'
import { pageTitle } from '../nav'

/**
 * One entry of the in-app history stack, mirroring what the browser is holding.
 *
 * `key` is React Router's per-entry id, which is what makes a POP identifiable: the same URL can
 * appear at several depths (series, similar series, back, a different similar series), so the path
 * alone cannot say where in the stack a POP landed.
 */
interface HistoryEntry {
  key: string
  pathname: string
  search: string
  /** What a link pointing back here should read. Pages can override it, see {@link usePageLabel}. */
  label: string
}

interface NavHistory {
  entries: HistoryEntry[]
  setLabel: (key: string, label: string) => void
}

const NavHistoryContext = createContext<NavHistory>({ entries: [], setLabel: () => {} })

function entryFor(location: Location): HistoryEntry {
  const label = pageTitle(location.pathname)
  // `pageTitle` answers "Maki" for anything it does not recognise, which is a window title, not a
  // back link. Those pages either register a real label or get the generic word.
  return {
    key: location.key,
    pathname: location.pathname,
    search: location.search,
    label: label === 'Maki' ? 'Back' : label,
  }
}

/**
 * Mirrors the browser history stack so a page can offer a back link that names where you came from
 * and returns you there by *popping*, not by pushing a new entry.
 *
 * Popping is the point. A page reached by pushing its origin's URL again is a fresh mount at the
 * top of a growing stack: forward is gone, the browser restores no scroll, and pressing back once
 * more lands you on the page you just left. Walking the stack backwards by the real distance is
 * what makes the button behave like the one in the browser chrome, which is what people expect it
 * to be.
 */
export function NavHistoryProvider({ children }: { children: ReactNode }) {
  const location = useLocation()
  const navigationType = useNavigationType()
  const [entries, setEntries] = useState<HistoryEntry[]>(() => [entryFor(location)])

  useEffect(() => {
    setEntries((prev) => {
      // Already the top of the stack: the first render, or StrictMode replaying this effect.
      if (prev[prev.length - 1]?.key === location.key) return prev

      const seen = prev.findIndex((e) => e.key === location.key)
      if (seen !== -1) return prev.slice(0, seen + 1) // back, by however many entries

      // REPLACE overwrites the entry it landed on rather than adding one, or this stack would
      // outgrow the browser's and every distance computed from it would be too large. A forward
      // POP has no entry to find and is treated as a push, which is what it is as far as depth
      // goes.
      if (navigationType === 'REPLACE') return [...prev.slice(0, -1), entryFor(location)]
      return [...prev, entryFor(location)]
    })
  }, [location, navigationType])

  const setLabel = useCallback((key: string, label: string) => {
    setEntries((prev) => {
      const i = prev.findIndex((e) => e.key === key)
      if (i === -1 || prev[i].label === label) return prev
      const next = [...prev]
      next[i] = { ...next[i], label }
      return next
    })
  }, [])

  const value = useMemo(() => ({ entries, setLabel }), [entries, setLabel])
  return <NavHistoryContext.Provider value={value}>{children}</NavHistoryContext.Provider>
}

/**
 * Names the current history entry, for any page whose title is not derivable from its path. A
 * series page registers its series' title, so the back link out of a series you reached from
 * another series reads with that series' name instead of the word "Series".
 */
export function usePageLabel(label: string | null | undefined) {
  const { setLabel } = useContext(NavHistoryContext)
  const { key } = useLocation()
  useEffect(() => {
    if (label) setLabel(key, label)
  }, [key, label, setLabel])
}

/** A click that carries enough of a mouse event to tell "open in a new tab" apart from a plain click. */
interface BackClick {
  preventDefault: () => void
  metaKey?: boolean
  ctrlKey?: boolean
  shiftKey?: boolean
  button?: number
}

export interface BackTarget {
  /** Link text: "Discover", "Add series", the title of the series you came from. */
  label: string
  /** Real href, so middle-click and ctrl-click still open the origin in a tab. */
  to: string
  /** Walks the history back to the origin. Falls through to the plain link when there is none. */
  onClick: (e: BackClick) => void
}

/**
 * Where a detail page's back link should point: the last entry in this session's history that is
 * not the page you are on.
 *
 * "Not the page you are on" rather than "not a series page" because tabs and sub-views push
 * entries of their own (the series page's `?tab=` is a push, deliberately, so the browser's back
 * button steps through them). Those all share a pathname, so skipping them takes one comparison
 * and no per-page knowledge, and the distance covers them in a single jump.
 */
export function useBackTarget(fallback: { to: string; label: string }): BackTarget {
  const { entries } = useContext(NavHistoryContext)
  const location = useLocation()
  const navigate = useNavigate()

  const origin = useMemo(() => {
    for (let i = entries.length - 1; i >= 0; i--) {
      if (entries[i].pathname !== location.pathname) {
        return { entry: entries[i], distance: entries.length - 1 - i }
      }
    }
    return null
  }, [entries, location.pathname])

  const onClick = useCallback(
    (e: BackClick) => {
      // Leave the modified clicks to the browser: they are asking for a new tab or window, where
      // there is no history to pop.
      if (e.metaKey || e.ctrlKey || e.shiftKey || (e.button != null && e.button !== 0)) return
      if (!origin || origin.distance < 1) return // nothing of ours to pop; the href does the work
      e.preventDefault()
      navigate(-origin.distance)
    },
    [navigate, origin],
  )

  if (!origin) return { ...fallback, onClick }
  return {
    label: origin.entry.label,
    to: `${origin.entry.pathname}${origin.entry.search}`,
    onClick,
  }
}

const SCROLL_PREFIX = 'maki-scroll:'
/**
 * How long to keep re-applying a restored offset. Async pages render short and grow, and the
 * document is not tall enough to hold the offset until its queries land: on a cold cache the rails
 * on Discover take a couple of seconds, so anything much shorter than this gives up on the page
 * that needs it most.
 */
const RESTORE_WINDOW_MS = 4000
/** Events that mean the reader has taken over and the restore should stop fighting them. */
const USER_SCROLL_EVENTS = ['wheel', 'touchstart', 'keydown', 'pointerdown'] as const

/**
 * Restores the scroll offset when you go back.
 *
 * React Router only ships `ScrollRestoration` for the data routers, and the browser's own
 * restoration is unreliable in an SPA: on POP the page it measures is the one that has not fetched
 * its data yet, so the offset is clamped to a document that is still a spinner tall. This records
 * the offset per history entry and re-applies it across frames until the content is there.
 *
 * Only POP is touched, so a fresh navigation keeps whatever the browser does today.
 */
export function ScrollMemory() {
  const location = useLocation()
  const navigationType = useNavigationType()
  // Path as well as key: a reload starts a fresh stack whose first entry is always keyed
  // "default", so a key alone would hand one page the offset another page left behind.
  const slot = `${SCROLL_PREFIX}${location.key}:${location.pathname}`
  const slotRef = useRef(slot)
  slotRef.current = slot

  useEffect(() => {
    if ('scrollRestoration' in window.history) window.history.scrollRestoration = 'manual'
  }, [])

  // Recorded continuously rather than on unmount: a POP unmounts the old page *after* the router
  // has already moved, so anything read in a cleanup is the new page's offset, not the old one's.
  useEffect(() => {
    let frame = 0
    const onScroll = () => {
      if (frame) return
      frame = requestAnimationFrame(() => {
        frame = 0
        try {
          sessionStorage.setItem(slotRef.current, String(window.scrollY))
        } catch { /* private mode or a full quota; scroll memory is not worth failing over */ }
      })
    }
    window.addEventListener('scroll', onScroll, { passive: true })
    return () => {
      window.removeEventListener('scroll', onScroll)
      if (frame) cancelAnimationFrame(frame)
    }
  }, [])

  useEffect(() => {
    if (navigationType !== 'POP') return
    let stored: string | null = null
    try {
      stored = sessionStorage.getItem(slot)
    } catch { return }
    const target = Number(stored)
    if (!stored || !Number.isFinite(target) || target <= 0) return

    let frame = 0
    const stop = () => {
      if (frame) cancelAnimationFrame(frame)
      frame = 0
      for (const ev of USER_SCROLL_EVENTS) window.removeEventListener(ev, stop)
    }
    for (const ev of USER_SCROLL_EVENTS) window.addEventListener(ev, stop, { passive: true })

    const deadline = performance.now() + RESTORE_WINDOW_MS
    frame = requestAnimationFrame(function step() {
      window.scrollTo(0, target)
      // Landed, or out of patience: the document may simply never get that tall again, if rows
      // were deleted or filtered away while we were gone.
      if (Math.abs(window.scrollY - target) < 2 || performance.now() > deadline) {
        stop()
        return
      }
      frame = requestAnimationFrame(step)
    })
    return stop
  }, [slot, navigationType])

  return null
}
