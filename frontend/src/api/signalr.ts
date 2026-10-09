import { HubConnectionBuilder, LogLevel, type HubConnection } from '@microsoft/signalr'
import { useEffect, useRef } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { notifications } from '@mantine/notifications'
import { inboxPrefsQuery, type InboxPush } from './inbox'
import type { SourceMatchProgress, SourceMatchState } from './hooks'
import type { QueueHistoryDto, QueueItemDto } from './types'

let connection: HubConnection | null = null
let connectionPromise: Promise<HubConnection> | null = null
// Bumped by stopConnection so a start loop that was in flight when the account changed gives up
// instead of handing the old account's socket to the new one.
let generation = 0

const reconnectListeners = new Set<() => void>()

// 0, 2, 5, 10 and then 30 s for as long as it takes. The built-in policy gives up after four tries
// (about 45 s), which is shorter than an update or a container restart, and once it has given up
// nothing ever starts the connection again. A tab left open through a restart went deaf until a
// reload without saying so.
const RETRY_DELAYS_MS = [0, 2000, 5000, 10000, 30000]
function retryDelay(attempt: number): number {
  const base = RETRY_DELAYS_MS[Math.min(attempt, RETRY_DELAYS_MS.length - 1)]
  return base + Math.floor(Math.random() * 1000)
}

function build(): HubConnection {
  // No credential in the URL: the handshake is same-origin, so the browser sends the session
  // cookie with it. The hub requires an authenticated user and puts the connection in that user's
  // group, which is how instance events reach admins only.
  const conn = new HubConnectionBuilder()
    .withUrl('/signalr/events')
    .withAutomaticReconnect({
      nextRetryDelayInMilliseconds: (ctx) => retryDelay(ctx.previousRetryCount),
    })
    .configureLogging(LogLevel.Warning)
    .build()
  conn.onreconnected(() => {
    for (const listener of reconnectListeners) listener()
  })
  return conn
}

class ConnectionStopped extends Error {}

function ensureConnection(): Promise<HubConnection> {
  // Cache the promise, not the connection: concurrent callers during startup
  // must not each build their own connection.
  connectionPromise ??= (async () => {
    const mine = generation
    for (let attempt = 0; ; attempt++) {
      const conn = build()
      try {
        await conn.start()
        if (mine !== generation) {
          void conn.stop()
          throw new ConnectionStopped()
        }
        connection = conn
        return conn
      } catch (err) {
        if (err instanceof ConnectionStopped) throw err
        // A rejected start used to be cached in connectionPromise for good, so a hiccup at page
        // load (API still starting, proxy not ready) meant no live updates until a reload.
        await new Promise((resolve) => setTimeout(resolve, retryDelay(attempt)))
        if (mine !== generation) throw new ConnectionStopped()
      }
    }
  })()
  return connectionPromise
}

/**
 * Closes the live connection and forgets it, so the next subscriber opens a fresh one under the
 * account that is signed in by then. Group membership on the server is fixed when a connection
 * opens, so a socket that outlived a sign-out would keep delivering the previous account's inbox
 * and admin events to whoever signed in next on the same tab.
 */
export function stopConnection(): void {
  generation++
  const conn = connection
  connection = null
  connectionPromise = null
  if (conn) void conn.stop()
}

function subscribe(cb: (conn: HubConnection) => void): () => void {
  let cancelled = false
  ensureConnection()
    .then((conn) => {
      if (!cancelled) cb(conn)
    })
    .catch(() => {
      // Only ConnectionStopped reaches here: the start loop never gives up otherwise.
    })
  return () => {
    cancelled = true
  }
}

/** Subscribes to a single hub event while the calling component is mounted. */
export function useHubEvent<T>(event: string, handler: (payload: T) => void) {
  const handlerRef = useRef(handler)
  handlerRef.current = handler

  useEffect(() => {
    const listener = (payload: T) => handlerRef.current(payload)
    const unsubscribe = subscribe((conn) => conn.on(event, listener))

    return () => {
      unsubscribe()
      connection?.off(event, listener)
    }
  }, [event])
}

/** Subscribes the query cache to live queue/import events for the app's lifetime. */
export function useLiveEvents() {
  const queryClient = useQueryClient()

  useEffect(() => {
    let summaryTimer: ReturnType<typeof setTimeout> | null = null
    let seriesTimer: ReturnType<typeof setTimeout> | null = null
    const importedSeries = new Set<number>()

    // A bulk download fires one chapterImported per chapter. The library list is the heaviest thing
    // they touch, so every event just marks work as pending and one timer refreshes it per second.
    const scheduleSeriesRefresh = () => {
      if (seriesTimer !== null) return
      seriesTimer = setTimeout(() => {
        seriesTimer = null
        const ids = [...importedSeries]
        importedSeries.clear()
        for (const id of ids) {
          void queryClient.invalidateQueries({ queryKey: ['chapters', id] })
          void queryClient.invalidateQueries({ queryKey: ['reader-continue', id] })
        }
        if (ids.length > 0) {
          void queryClient.invalidateQueries({ queryKey: ['home', 'recently-added'] })
        }
        void queryClient.invalidateQueries({ queryKey: ['series'] })
      }, 1000)
    }

    // Events sent while the socket was down are gone, and the queue lists are patched in place from
    // events rather than refetched, so a download that finished during a blip stayed "in progress"
    // and a Sources card waited forever for a sourceMatchFinished nobody would resend. Everything
    // the handlers below maintain is refetched once the socket is back.
    const onReconnected = () => {
      for (const queryKey of [
        ['queue'],
        ['queue-summary'],
        ['queue-history'],
        ['inbox'],
        ['series'],
        ['chapters'],
        ['sourcemappings'],
        ['sourcematch-progress'],
        ['requests'],
        ['home'],
        ['system', 'update'],
        ['settings', 'metadata'],
      ]) {
        void queryClient.invalidateQueries({ queryKey })
      }
    }
    reconnectListeners.add(onReconnected)

    const registered: Array<[string, Parameters<HubConnection['off']>[1]]> = []
    let liveConn: HubConnection | null = null
    const listen = (conn: HubConnection, name: string, handler: Parameters<HubConnection['on']>[1]) => {
      conn.on(name, handler)
      registered.push([name, handler])
    }

    const unsubscribe = subscribe((conn) => {
      liveConn = conn
      listen(conn, 'queueUpdated', (item: QueueItemDto) => {
        const isDone = item.status === 'Completed' || item.status === 'Cancelled'
        // Only the paged lists ['queue', page, pageSize] hold `items`; ['queue', 'import-plan', id] does not.
        queryClient.setQueriesData<QueueHistoryDto>({
          queryKey: ['queue'],
          predicate: (q) => typeof q.queryKey[1] === 'number',
        }, (old) => {
          if (!old || !Array.isArray(old.items)) return old
          if (isDone) {
            const items = old.items.filter((q) => q.id !== item.id)
            // Only decrement when the item was actually on this page, or repeated events
            // would walk the total below the real count.
            const removed = items.length !== old.items.length
            return { ...old, items, total: removed ? Math.max(0, old.total - 1) : old.total }
          }

          const idx = old.items.findIndex((q) => q.id === item.id)
          if (idx === -1) {
            return { ...old, items: [item, ...old.items], total: old.total + 1 }
          }

          const next = [...old.items]
          next[idx] = item
          return { ...old, items: next }
        })
        if (isDone) {
          // The item moved into history, so refresh the paginated history feed.
          void queryClient.invalidateQueries({ queryKey: ['queue-history'] })
        }
        // The shell badge reads the summary endpoint; a download burst fires many of these, so
        // coalesce to one refetch per second.
        if (summaryTimer === null) {
          summaryTimer = setTimeout(() => {
            summaryTimer = null
            void queryClient.invalidateQueries({ queryKey: ['queue-summary'] })
          }, 1000)
        }
      })

      // The flush also refreshes Home's recently-added rail (keyed on ChapterFile.DateAdded, which
      // an import just wrote) and the detail page's Read button gate (`reader-continue`).
      listen(conn, 'chapterImported', ({ seriesId }: { seriesId: number }) => {
        importedSeries.add(seriesId)
        scheduleSeriesRefresh()
      })

      // Auto-matching finished for a series added a moment ago. The sources card, the chapter
      // table and the series row itself (which carries the pending flag the spinner reads) all
      // change at once, so all three are refetched.
      listen(conn, 'sourceMatchFinished', ({ seriesId }: { seriesId: number }) => {
        void queryClient.invalidateQueries({ queryKey: ['sourcemappings', seriesId] })
        void queryClient.invalidateQueries({ queryKey: ['chapters', seriesId] })
        // The detail row carries the pending flag the spinner reads, so it refreshes at once; the
        // library list joins the coalesced refresh.
        void queryClient.invalidateQueries({ queryKey: ['series', seriesId] })
        scheduleSeriesRefresh()
        // The per-source states the card drew while it waited. Cleared here rather than when the
        // next match starts: the first `Searching` pushes of a run arrive *before* the client has
        // noticed the series is matching again, so a clear at that point would wipe them.
        queryClient.setQueryData(['sourcematch-progress', seriesId], {})
      })

      // One source's progress inside a match that's still running. Decoration on top of
      // `sourceMatchFinished`, which is still what makes the real rows appear. A client that
      // misses these just sees the finished table, as it did before.
      listen(conn, 
        'sourceMatchProgress',
        ({
          seriesId,
          sourceName,
          state,
        }: {
          seriesId: number
          sourceName: string
          state: SourceMatchState
        }) => {
          queryClient.setQueryData<SourceMatchProgress>(
            ['sourcematch-progress', seriesId],
            (prev) => {
              // The pushes are sent in order but not delivered under any guarantee, so a late
              // 'Searching' must never walk back a source that has already resolved.
              if (state === 'Searching' && prev?.[sourceName]) return prev
              return { ...prev, [sourceName]: state }
            },
          )
        },
      )

      // Kavita's live sync marked chapters read. Same queries a manual mark-read invalidates.
      listen(conn, 'readProgressChanged', ({ seriesId }: { seriesId: number }) => {
        void queryClient.invalidateQueries({ queryKey: ['reader-progress', seriesId] })
        void queryClient.invalidateQueries({ queryKey: ['reader-continue', seriesId] })
        void queryClient.invalidateQueries({ queryKey: ['series', seriesId] })
        scheduleSeriesRefresh()
        void queryClient.invalidateQueries({ queryKey: ['home'] })
      })

      listen(conn, 'updateAvailable', () => {
        void queryClient.invalidateQueries({ queryKey: ['system', 'update'] })
      })

      // Admins only: the hub puts this one in the admin group. Covers both the nav badge and an
      // open Requests page, so a request filed while an admin is looking at it lands without a
      // reload.
      listen(conn, 'seriesRequested', () => {
        void queryClient.invalidateQueries({ queryKey: ['requests'] })
      })

      // Addressed to one user's group, not a broadcast: this is somebody's own mail.
      listen(conn, 'inboxNotification', async (item: InboxPush) => {
        // The push carries the recipient's new unread count, so the badge updates without a
        // round trip. The feed is invalidated rather than patched: it is paged and filtered, and
        // splicing a row into every cached filter combination is more ways to be wrong than it is
        // worth for a refetch of 25 rows.
        queryClient.setQueryData(['inbox', 'unread'], { count: item.unread })
        void queryClient.invalidateQueries({ queryKey: ['inbox', 'feed'] })

        // Read through the query client rather than a hook: this handler is registered once for
        // the app's lifetime and must not re-subscribe every time the preference changes. The
        // bell keeps the query cached; ensureQueryData covers a push that beats its first fetch.
        // If the prefs cannot be loaded the toast shows, matching the server default.
        const prefs = await queryClient.ensureQueryData(inboxPrefsQuery).catch(() => null)
        if (prefs?.toasts === false) return
        // The reader owns the whole viewport and the toast stack sits over its bottom bar and tap
        // zone; the badge and feed above are already updated, so the mail is there afterwards.
        if (window.location.pathname.startsWith('/read/')) return

        notifications.show({
          title: item.title,
          message: item.body,
          color: item.level === 'error' ? 'var(--danger)' : item.level === 'warning' ? 'var(--warn)' : undefined,
        })
      })
    })

    return () => {
      unsubscribe()
      reconnectListeners.delete(onReconnected)
      if (summaryTimer !== null) clearTimeout(summaryTimer)
      if (seriesTimer !== null) clearTimeout(seriesTimer)
      for (const [name, handler] of registered) liveConn?.off(name, handler)
    }
  }, [queryClient])
}
