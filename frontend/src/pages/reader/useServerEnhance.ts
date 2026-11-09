import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { ApiError, trackedFetch } from '../../api/client'
import {
  cancelServerEnhance,
  requestServerEnhance,
  serverEnhancedUrl,
  type EnhanceServer,
  type ServerPageState,
} from '../../api/reader'
import { PAGE_ENHANCED, useHubConnected, useHubEvent, type PageEnhancedEvent } from '../../api/signalr'
import { serverModelId } from './enhanceMethods'
import type { EnhanceMethod, ReaderEnhance } from './prefs'

/** What the views need to swap in server upscales: the request's identity and each page's state. */
export interface ServerEnhance {
  chapterId: number
  model: string
  pageVersion: string
  states: ReadonlyMap<number, ServerPageState>
  /** Pages whose server upscale is decoded and drawn in place of the original. */
  shown: ReadonlySet<number>
  /** Called by a view as a page's server upscale starts and stops being drawn. */
  reportShown: (page: number, shown: boolean) => void
  /** Called when a page answered `ready` is gone from the server's cache, so it is asked for again. */
  reportMissing: (page: number) => void
}

const MAX_PAGES = 32
const MAX_CHAPTER_PAGES = 1000
const DEBOUNCE_MS = 250
const HUB_GRACE_MS = 10_000
const POLL_MS = 5_000
const SETTLED: ReadonlySet<ServerPageState | undefined> = new Set<ServerPageState>(['ready', 'failed', 'skipped'])
const NONE: ReadonlyMap<number, ServerPageState> = new Map()
const NO_PAGES: ReadonlySet<number> = new Set()

/**
 * The server model a reader's method runs on, or null when the server tier cannot be used and the
 * browser fallback preset stands in: the tier is off, the model is not enabled or not downloaded,
 * or this device is offline or opened the chapter from its offline copy.
 */
export function serverModelFor(
  enhance: ReaderEnhance,
  method: EnhanceMethod,
  tier: EnhanceServer | undefined,
  offline: boolean,
): string | null {
  const model = enhance === 'off' ? null : serverModelId(method)
  if (!model || offline || !tier?.enabled) return null
  return tier.models.some((entry) => entry.id === model && entry.ready) ? model : null
}

interface Tracked {
  scope: string | null
  states: ReadonlyMap<number, ServerPageState>
  refused: boolean
}

/** Pages Enhance on `auto` has measured: the ones it wants upscaled and the ones it shows plain. */
export interface AutoPages {
  wanted: ReadonlySet<number>
  plain: ReadonlySet<number>
}

/**
 * The chapter's pages outside the window and not shown plain: from `from` to the end, then back
 * from `from` to the start.
 */
function chapterRest(from: number, pageCount: number, window: number[], plain: number[], limit: number): number[] {
  const skip = new Set([...window, ...plain])
  const rest: number[] = []
  const start = Math.min(Math.max(0, from), pageCount)
  for (let page = start; page < pageCount && rest.length < limit; page++) if (!skip.has(page)) rest.push(page)
  for (let page = start - 1; page >= 0 && rest.length < limit; page--) if (!skip.has(page)) rest.push(page)
  return rest
}

/**
 * Asks the server to upscale the reading window (current page first, at most 32) a moment after
 * each page change. With `auto` set (Enhance on `auto`), only the window's wanted pages are asked
 * for, and a page joining them asks again. Pages `auto` shows plain are sent as `notWanted`, so the
 * server drops them where an earlier guess queued them (the previous chapter's look-ahead, the whole
 * chapter's rest), and their states are left out: a page shown plain never waits on an upscale. Each page is followed from `queued` or `working` to
 * `ready`, `failed` or `skipped` through the response and the `pageEnhanced` push. If the live
 * connection has been down for 10 s, the window's unfinished pages are polled every 5 s instead. A
 * 409 (tier switched off or model gone) stops asking for the rest of the visit. Null whenever
 * `model` is. Once the window runs past the last page, `next` names the next chapter's first pages,
 * queued behind the window so that chapter opens already upscaled; on `auto` they go along while
 * any page of the window is stretched. `whole` (the "Whole chapter" setting) queues the rest of the
 * chapter behind the window the same way, pages ahead first; undefined means not known yet (the
 * manifest is loading). The server's leftovers are cancelled when it turns off, when the reader
 * unmounts and when the tab is closed.
 */
export function useServerEnhance(
  chapterId: number,
  model: string | null,
  pages: number[],
  pageVersion: string | undefined,
  auto: AutoPages | null = null,
  next: { chapterId: number; pages: number[] } | null = null,
  whole: { from: number; pageCount: number } | null | undefined = null,
): ServerEnhance | null {
  const scope = model && pageVersion !== undefined ? `${chapterId}|${model}|${pageVersion}` : null
  const scopeRef = useRef(scope)
  scopeRef.current = scope
  const [tracked, setTracked] = useState<Tracked>({ scope: null, states: NONE, refused: false })
  const current: Tracked = tracked.scope === scope ? tracked : { scope, states: NONE, refused: false }
  const statesRef = useRef(current.states)
  statesRef.current = current.states

  const merge = useCallback((forScope: string, updates: { page: number; state: ServerPageState }[]) => {
    if (forScope !== scopeRef.current) return
    setTracked((previous) => {
      const base = previous.scope === forScope ? previous : { scope: forScope, states: NONE, refused: false }
      let next: Map<number, ServerPageState> | null = null
      for (const { page, state } of updates) {
        const known = base.states.get(page)
        // A late response must not walk a page back from a push that already started or settled it.
        if (known === state || (SETTLED.has(known) && !SETTLED.has(state)) || (known === 'working' && state === 'queued')) continue
        next ??= new Map(base.states)
        next.set(page, state)
      }
      return next ? { ...base, states: next } : base
    })
  }, [])

  const refuse = useCallback((forScope: string) => {
    if (forScope !== scopeRef.current) return
    setTracked((previous) => ({ ...(previous.scope === forScope ? previous : { scope: forScope, states: NONE }), refused: true }))
  }, [])

  const only = auto?.wanted ?? null
  const plainKey = auto ? [...auto.plain].sort((a, b) => a - b).slice(0, MAX_CHAPTER_PAGES).join(',') : ''
  const windowKey = pages
    .filter((page) => Number.isInteger(page) && page >= 0)
    .filter((page, index, all) => all.indexOf(page) === index && (only === null || only.has(page)))
    .slice(0, MAX_PAGES)
    .join(',')
  const windowCount = windowKey ? windowKey.split(',').length : 0
  const wholeFrom = whole ? whole.from : -1
  const wholePages = whole ? whole.pageCount : 0
  const restKey = useMemo(
    () =>
      windowKey && wholePages > 0
        ? chapterRest(
            wholeFrom,
            wholePages,
            windowKey.split(',').map(Number),
            plainKey ? plainKey.split(',').map(Number) : [],
            MAX_CHAPTER_PAGES - windowCount,
          ).join(',')
        : '',
    [windowKey, windowCount, wholeFrom, wholePages, plainKey],
  )
  // Not tied to the window: on Auto a window with nothing wanted would drop the server's cap to 32
  // and cut the chapter it already queued.
  const wholeOn = wholePages > 0
  const room = (wholeOn ? MAX_CHAPTER_PAGES : MAX_PAGES) - windowCount - (restKey ? restKey.split(',').length : 0)
  const nextChapterId = next && next.chapterId !== chapterId && room > 0 ? next.chapterId : null
  const nextKey = nextChapterId === null ? '' : next!.pages.slice(0, room).join(',')
  const connected = useHubConnected()
  const active = scope !== null && model !== null && !current.refused
  const [revived, setRevived] = useState(0)
  // What the server last heard from this chapter with nothing to queue. The first request in a
  // chapter always goes out, even an empty one: it ends the previous chapter's leftovers.
  const told = useRef<string | null>(null)

  // Re-runs on reconnect too: pushes sent while the socket was down are gone, so the window's
  // unfinished pages are asked about again.
  useEffect(() => {
    if (!active || !scope || !model) return
    const timer = setTimeout(() => {
      const asked = [windowKey, restKey].filter(Boolean).join(',')
      const wanted = asked ? asked.split(',').map(Number).filter((page) => !SETTLED.has(statesRef.current.get(page))) : []
      const ahead = nextChapterId !== null && nextKey ? { chapterId: nextChapterId, pages: nextKey.split(',').map(Number) } : null
      const said = `${scope}|${plainKey}`
      if (wanted.length === 0 && !ahead && told.current === said) return
      told.current = said
      const plain = plainKey ? plainKey.split(',').map(Number) : []
      const inWindow = windowKey ? windowKey.split(',').filter((page) => !SETTLED.has(statesRef.current.get(Number(page)))).length : 0
      requestServerEnhance(chapterId, model, wanted, ahead, wholeOn, plain, inWindow).then(
        (response) => merge(scope, response.pages),
        (error: unknown) => {
          if (error instanceof ApiError && error.status === 409) refuse(scope)
        },
      )
    }, DEBOUNCE_MS)
    return () => clearTimeout(timer)
  }, [active, scope, chapterId, model, windowKey, restKey, plainKey, wholeOn, nextChapterId, nextKey, connected, revived, merge, refuse])

  // Unknown (a chapter's manifest loading) keeps the last answer, so turning to the next chapter
  // does not cancel the pages already queued for it.
  const wholeKnown = whole === undefined ? undefined : whole !== null && active
  const wholeWas = useRef(false)
  useEffect(() => {
    if (wholeKnown === undefined) return
    if (wholeWas.current && !wholeKnown) void cancelServerEnhance().catch(() => {})
    wholeWas.current = wholeKnown
  }, [wholeKnown])
  useEffect(() => {
    const onHide = () => {
      if (wholeWas.current) void cancelServerEnhance().catch(() => {})
    }
    // Back from the back-forward cache with the queue cancelled: ask again.
    const onShow = (event: PageTransitionEvent) => {
      if (event.persisted) setRevived((count) => count + 1)
    }
    window.addEventListener('pagehide', onHide)
    window.addEventListener('pageshow', onShow)
    return () => {
      window.removeEventListener('pagehide', onHide)
      window.removeEventListener('pageshow', onShow)
      onHide()
    }
  }, [])

  useHubEvent<PageEnhancedEvent>(PAGE_ENHANCED, (event) => {
    const live = scopeRef.current
    if (!live || event.chapterId !== chapterId || event.model !== model) return
    merge(live, [{ page: event.page, state: event.state }])
  })

  const downSince = useRef<number | null>(null)
  useEffect(() => {
    downSince.current = connected ? null : (downSince.current ?? Date.now())
  }, [connected])

  useEffect(() => {
    if (connected || !active || !scope || !model || pageVersion === undefined || !windowKey) return
    const controller = new AbortController()
    let interval: ReturnType<typeof setInterval> | undefined
    const poll = () => {
      for (const page of windowKey.split(',').map(Number)) {
        if (SETTLED.has(statesRef.current.get(page))) continue
        void serverEnhancedUrl(chapterId, page, model, pageVersion)
          .then((url) => trackedFetch(url, { cache: 'no-store', credentials: 'same-origin', signal: controller.signal }))
          .then((response) => {
            void response.body?.cancel()
            if (response.ok) merge(scope, [{ page, state: 'ready' }])
            else if (response.status === 409) refuse(scope)
          })
          .catch(() => {})
      }
    }
    const since = downSince.current ?? Date.now()
    const timer = setTimeout(() => {
      poll()
      interval = setInterval(poll, POLL_MS)
    }, Math.max(0, since + HUB_GRACE_MS - Date.now()))
    return () => {
      clearTimeout(timer)
      clearInterval(interval)
      controller.abort()
    }
  }, [connected, active, scope, chapterId, model, pageVersion, windowKey, merge, refuse])

  const [shownFor, setShownFor] = useState<{ scope: string | null; pages: ReadonlySet<number> }>({ scope: null, pages: NO_PAGES })
  const reportShown = useCallback((page: number, on: boolean) => {
    const live = scopeRef.current
    setShownFor((previous) => {
      const pages = previous.scope === live ? previous.pages : NO_PAGES
      if (pages.has(page) === on) return previous.scope === live ? previous : { scope: live, pages }
      const next = new Set(pages)
      if (on) next.add(page)
      else next.delete(page)
      return { scope: live, pages: next }
    })
  }, [])
  const shown = shownFor.scope === scope ? shownFor.pages : NO_PAGES

  const reportMissing = useCallback((page: number) => {
    const live = scopeRef.current
    if (!live || statesRef.current.get(page) !== 'ready') return
    setTracked((previous) => {
      if (previous.scope !== live || previous.states.get(page) !== 'ready') return previous
      const states = new Map(previous.states)
      states.delete(page)
      return { ...previous, states }
    })
    setRevived((count) => count + 1)
  }, [])

  const all = current.states
  const states = useMemo(() => {
    if (!plainKey) return all
    const plain = plainKey.split(',').map(Number)
    if (!plain.some((page) => all.has(page))) return all
    const kept = new Map(all)
    for (const page of plain) kept.delete(page)
    return kept
  }, [all, plainKey])
  return useMemo(
    () =>
      active && model && pageVersion !== undefined
        ? { chapterId, model, pageVersion, states, shown, reportShown, reportMissing }
        : null,
    [active, chapterId, model, pageVersion, states, shown, reportShown, reportMissing],
  )
}

/**
 * Fetches and decodes the server upscales of the pages ahead that are ready, so a page turn swaps
 * them in at once. Each one is released as its page leaves `pages`.
 */
export function useWarmServerPages(server: ServerEnhance | null, pages: number[]) {
  const chapterId = server?.chapterId
  const model = server?.model
  const pageVersion = server?.pageVersion
  const ready = server ? pages.filter((page) => server.states.get(page) === 'ready').join(',') : ''
  const [warmed] = useState(() => new Map<string, HTMLImageElement>())

  useEffect(() => {
    const wanted = new Map<string, number>()
    if (chapterId !== undefined && model && pageVersion !== undefined && ready) {
      for (const page of ready.split(',').map(Number)) wanted.set(`${chapterId}|${model}|${pageVersion}|${page}`, page)
    }
    for (const [key, image] of warmed) {
      if (wanted.has(key)) continue
      image.src = ''
      warmed.delete(key)
    }
    for (const [key, page] of wanted) {
      if (warmed.has(key) || chapterId === undefined || !model || pageVersion === undefined) continue
      const image = new Image()
      warmed.set(key, image)
      void serverEnhancedUrl(chapterId, page, model, pageVersion).then((url) => {
        if (warmed.get(key) !== image) return
        image.src = url
        image.decode().catch(() => {})
      })
    }
  }, [warmed, chapterId, model, pageVersion, ready])

  useEffect(
    () => () => {
      for (const image of warmed.values()) image.src = ''
      warmed.clear()
    },
    [warmed],
  )
}
