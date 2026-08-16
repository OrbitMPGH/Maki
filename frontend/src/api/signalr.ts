import { HubConnectionBuilder, LogLevel, type HubConnection } from '@microsoft/signalr'
import { useEffect, useRef } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { notifications } from '@mantine/notifications'
import type { InboxPrefs, InboxPush } from './inbox'
import type { QueueHistoryDto, QueueItemDto } from './types'

let connection: HubConnection | null = null
let connectionPromise: Promise<HubConnection> | null = null

function ensureConnection(): Promise<HubConnection> {
  // Cache the promise, not the connection: concurrent callers during startup
  // must not each build their own connection.
  connectionPromise ??= (async () => {
    // No credential in the URL: the handshake is same-origin, so the browser sends the session
    // cookie with it. The hub requires an authenticated user and puts the connection in that user's
    // group, which is how instance events reach admins only.
    const conn = new HubConnectionBuilder()
      .withUrl('/signalr/events')
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build()
    await conn.start()
    connection = conn
    return conn
  })()
  return connectionPromise
}

/** Subscribes to a single hub event while the calling component is mounted. */
export function useHubEvent<T>(event: string, handler: (payload: T) => void) {
  const handlerRef = useRef(handler)
  handlerRef.current = handler

  useEffect(() => {
    let cancelled = false
    const listener = (payload: T) => handlerRef.current(payload)

    void ensureConnection().then((conn) => {
      if (cancelled) return
      conn.on(event, listener)
    })

    return () => {
      cancelled = true
      connection?.off(event, listener)
    }
  }, [event])
}

/** Subscribes the query cache to live queue/import events for the app's lifetime. */
export function useLiveEvents() {
  const queryClient = useQueryClient()

  useEffect(() => {
    let cancelled = false

    void ensureConnection().then((conn) => {
      if (cancelled) return

      conn.on('queueUpdated', (item: QueueItemDto) => {
        const isDone = item.status === 'Completed' || item.status === 'Cancelled'
        queryClient.setQueriesData<QueueHistoryDto>({ queryKey: ['queue'] }, (old) => {
          if (!old) return old
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
      })

      conn.on('chapterImported', ({ seriesId }: { seriesId: number }) => {
        void queryClient.invalidateQueries({ queryKey: ['chapters', seriesId] })
        void queryClient.invalidateQueries({ queryKey: ['series'] })
        // Home's recently-added rail is keyed on ChapterFile.DateAdded, which this import just
        // wrote; without this the rail only catches up on the next reload.
        void queryClient.invalidateQueries({ queryKey: ['home', 'recently-added'] })
        // The detail page's Read button gates on this; without it the button only appears
        // after a manual reload once the first chapter finishes downloading.
        void queryClient.invalidateQueries({ queryKey: ['reader-continue', seriesId] })
      })

      // Auto-matching finished for a series added a moment ago. The sources card, the chapter
      // table and the series row itself (which carries the pending flag the spinner reads) all
      // change at once, so all three are refetched.
      conn.on('sourceMatchFinished', ({ seriesId }: { seriesId: number }) => {
        void queryClient.invalidateQueries({ queryKey: ['sourcemappings', seriesId] })
        void queryClient.invalidateQueries({ queryKey: ['chapters', seriesId] })
        // Prefix match, so this covers ['series', id] — the detail row carrying the pending flag —
        // as well as the library list.
        void queryClient.invalidateQueries({ queryKey: ['series'] })
      })

      conn.on('updateAvailable', () => {
        void queryClient.invalidateQueries({ queryKey: ['system', 'update'] })
      })

      // Admins only: the hub puts this one in the admin group. Covers both the nav badge and an
      // open Requests page, so a request filed while an admin is looking at it lands without a
      // reload.
      conn.on('seriesRequested', () => {
        void queryClient.invalidateQueries({ queryKey: ['requests'] })
      })

      // Addressed to one user's group, not a broadcast — this is somebody's own mail.
      conn.on('inboxNotification', (item: InboxPush) => {
        // The push carries the recipient's new unread count, so the badge updates without a
        // round trip. The feed is invalidated rather than patched: it is paged and filtered, and
        // splicing a row into every cached filter combination is more ways to be wrong than it is
        // worth for a refetch of 25 rows.
        queryClient.setQueryData(['inbox', 'unread'], { count: item.unread })
        void queryClient.invalidateQueries({ queryKey: ['inbox', 'feed'] })

        // Read from the cache rather than a hook: this handler is registered once for the app's
        // lifetime and must not re-subscribe every time the preference changes. Absent prefs
        // (first load, still fetching) default to showing the toast, matching the server default.
        const prefs = queryClient.getQueryData<InboxPrefs>(['inbox', 'prefs'])
        if (prefs?.toasts === false) return

        notifications.show({
          title: item.title,
          message: item.body,
          color: item.level === 'error' ? 'red' : item.level === 'warning' ? 'yellow' : undefined,
        })
      })
    })

    return () => {
      cancelled = true
      connection?.off('queueUpdated')
      connection?.off('chapterImported')
      connection?.off('sourceMatchFinished')
      connection?.off('updateAvailable')
      connection?.off('seriesRequested')
      connection?.off('inboxNotification')
    }
  }, [queryClient])
}
