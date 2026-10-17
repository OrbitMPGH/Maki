import { useEffect, useRef } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { useAuth } from '../auth/AuthProvider'
import { useConnectivity } from '../lib/connectivity'
import { pwaSupported } from '../lib/pwa/serviceWorker'
import { loadOfflineChapters, requestOfflineMaintenance, resumeOfflineSaves } from './offlineChapters'
import { replayProgress, setQueueUser } from './progressQueue'

/**
 * Points the offline store and the progress queue at the signed-in user, and whenever the server
 * answers live, sends queued progress and resumes unfinished saves. Mounted once, above the
 * reader and shell split.
 */
export function OfflineSync() {
  const { me } = useAuth()
  const connectivity = useConnectivity()
  const queryClient = useQueryClient()
  const userId = me?.id ?? null
  const maintainedFor = useRef<number | null>(null)

  useEffect(() => {
    setQueueUser(userId, queryClient)
    void loadOfflineChapters(userId)
  }, [userId, queryClient])

  useEffect(() => {
    if (userId === null || connectivity !== 'online' || !pwaSupported()) return
    void replayProgress()
    void resumeOfflineSaves()
    if (maintainedFor.current !== userId) {
      maintainedFor.current = userId
      requestOfflineMaintenance()
    }
  }, [userId, connectivity])

  return null
}
