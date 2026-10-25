import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { ApiError, trackedFetch } from '../../api/client'
import {
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
}

const MAX_PAGES = 12
const DEBOUNCE_MS = 250
const HUB_GRACE_MS = 10_000
const POLL_MS = 5_000
const SETTLED: ReadonlySet<ServerPageState | undefined> = new Set<ServerPageState>(['ready', 'failed', 'skipped'])
const NONE: ReadonlyMap<number, ServerPageState> = new Map()

/**
 * The server model a reader's method runs on, or null when the server tier cannot be used and the
 * browser's Balanced preset stands in: the tier is off, the model is not enabled or not downloaded,
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

/**
 * Asks the server to upscale the reading window (current page first, at most 12) a moment after
 * each page change, and follows each page from `queued` or `working` to `ready`, `failed` or
 * `skipped` through the response and the `pageEnhanced` push. If the live connection has been down
 * for 10 s, the window's unfinished pages are polled every 5 s instead. A 409 (tier switched off or
 * model gone) stops asking for the rest of the visit. Null whenever `model` is.
 */
export function useServerEnhance(
  chapterId: number,
  model: string | null,
  pages: number[],
  pageVersion: string | undefined,
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
        // A late response must not walk a page back from a push that already settled it.
        if (known === state || (SETTLED.has(known) && !SETTLED.has(state))) continue
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

  const windowKey = pages
    .filter((page) => Number.isInteger(page) && page >= 0)
    .filter((page, index, all) => all.indexOf(page) === index)
    .slice(0, MAX_PAGES)
    .join(',')
  const connected = useHubConnected()
  const active = scope !== null && model !== null && !current.refused

  // Re-runs on reconnect too: pushes sent while the socket was down are gone, so the window's
  // unfinished pages are asked about again.
  useEffect(() => {
    if (!active || !scope || !model || !windowKey) return
    const timer = setTimeout(() => {
      const wanted = windowKey.split(',').map(Number).filter((page) => !SETTLED.has(statesRef.current.get(page)))
      if (wanted.length === 0) return
      requestServerEnhance(chapterId, model, wanted).then(
        (response) => merge(scope, response.pages),
        (error: unknown) => {
          if (error instanceof ApiError && error.status === 409) refuse(scope)
        },
      )
    }, DEBOUNCE_MS)
    return () => clearTimeout(timer)
  }, [active, scope, chapterId, model, windowKey, connected, merge, refuse])

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

  const states = current.states
  return useMemo(
    () => (active && model && pageVersion !== undefined ? { chapterId, model, pageVersion, states } : null),
    [active, chapterId, model, pageVersion, states],
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
