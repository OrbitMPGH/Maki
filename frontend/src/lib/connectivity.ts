import { useSyncExternalStore } from 'react'

export type Connectivity = 'online' | 'offline'

/**
 * Whether Maki's server is reachable, as far as this tab can tell. The API client flips it on what
 * it sees (a fetch that rejects, a response served from the device, a live answer), and the
 * browser's own online and offline events feed it too.
 */
let state: Connectivity = typeof navigator !== 'undefined' && navigator.onLine === false ? 'offline' : 'online'
const listeners = new Set<() => void>()

export function getConnectivity(): Connectivity {
  return state
}

export function setConnectivity(next: Connectivity): void {
  if (next === state) return
  state = next
  for (const listener of listeners) listener()
}

export function onConnectivityChange(listener: () => void): () => void {
  listeners.add(listener)
  return () => {
    listeners.delete(listener)
  }
}

export function useConnectivity(): Connectivity {
  return useSyncExternalStore(onConnectivityChange, getConnectivity, getConnectivity)
}

if (typeof window !== 'undefined') {
  window.addEventListener('online', () => setConnectivity('online'))
  window.addEventListener('offline', () => setConnectivity('offline'))
}
