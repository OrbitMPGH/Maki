import { HubConnectionBuilder, LogLevel, type HubConnection } from '@microsoft/signalr'
import { useEffect, useRef } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { getInitialize } from './client'
import type { QueueItemDto } from './types'

let connection: HubConnection | null = null
let connectionPromise: Promise<HubConnection> | null = null

function ensureConnection(): Promise<HubConnection> {
  // Cache the promise, not the connection: concurrent callers during startup
  // must not each build their own connection.
  connectionPromise ??= (async () => {
    const init = await getInitialize()
    const conn = new HubConnectionBuilder()
      .withUrl(`/signalr/events?apikey=${init.apiKey}`)
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
        // Matches both ['queue', false] and ['queue', true] caches.
        queryClient.setQueriesData<QueueItemDto[]>({ queryKey: ['queue'] }, (old) => {
          if (!old) return old
          const idx = old.findIndex((q) => q.id === item.id)
          if (idx === -1) return [item, ...old]
          const next = [...old]
          next[idx] = item
          return next
        })
      })

      conn.on('chapterImported', ({ seriesId }: { seriesId: number }) => {
        void queryClient.invalidateQueries({ queryKey: ['chapters', seriesId] })
        void queryClient.invalidateQueries({ queryKey: ['series'] })
      })
    })

    return () => {
      cancelled = true
      connection?.off('queueUpdated')
      connection?.off('chapterImported')
    }
  }, [queryClient])
}
