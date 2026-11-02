import { useSyncExternalStore } from 'react'

export interface SoFarSyncAsk {
  trackers: { service: string; label: string }[]
  seriesId: number
}

let current: SoFarSyncAsk | null = null
// Waved off without an answer: not asked again until the page reloads. Nothing is stored, so the
// server keeps offering the question and a later visit asks again.
let waivedThisLoad = false
const listeners = new Set<() => void>()
const emit = () => listeners.forEach((listener) => listener())

/** Raised by a "so far" rating whose response names trackers that have never been asked. */
export function askSoFarSync(ask: SoFarSyncAsk) {
  if (current || waivedThisLoad || ask.trackers.length === 0) return
  current = ask
  emit()
}

export function closeSoFarSyncAsk(answered: boolean) {
  if (!answered) waivedThisLoad = true
  current = null
  emit()
}

/** For stories: module state outlives a story under Storybook's shared iframe. */
export function resetSoFarSyncAsk() {
  current = null
  waivedThisLoad = false
  emit()
}

export function useSoFarSyncAsk(): SoFarSyncAsk | null {
  return useSyncExternalStore(
    (listener) => {
      listeners.add(listener)
      return () => listeners.delete(listener)
    },
    () => current,
  )
}
