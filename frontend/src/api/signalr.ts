import { HubConnectionBuilder, LogLevel, type HubConnection } from '@microsoft/signalr'
import { useEffect } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { getInitialize } from './client'
import type { QueueItemDto } from './types'

let connection: HubConnection | null = null

async function ensureConnection(): Promise<HubConnection> {
  if (connection) return connection
  const init = await getInitialize()
  connection = new HubConnectionBuilder()
    .withUrl(`/signalr/events?apikey=${init.apiKey}`)
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build()
  await connection.start()
  return connection
}

/** Subscribes the query cache to live queue/import events for the app's lifetime. */
export function useLiveEvents() {
  const queryClient = useQueryClient()

  useEffect(() => {
    let cancelled = false

    void ensureConnection().then((conn) => {
      if (cancelled) return

      conn.on('queueUpdated', (item: QueueItemDto) => {
        queryClient.setQueryData<QueueItemDto[]>(['queue'], (old) => {
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
