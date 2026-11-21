import { useSyncExternalStore } from 'react'

/**
 * What a library cover's progress bar leads with, resolved from the user's setting (and, on
 * "auto", from whether they read in Maki's reader). Kept here rather than read from the settings
 * query in every card, since a grid mounts hundreds of cards. Set by `CardProgressSync`.
 */
export type CardProgressMode = 'downloads' | 'reading'

let progress: CardProgressMode = 'downloads'
const listeners = new Set<() => void>()

export function setCardProgress(next: CardProgressMode) {
  if (next === progress) return
  progress = next
  listeners.forEach((l) => l())
}

function subscribe(listener: () => void) {
  listeners.add(listener)
  return () => listeners.delete(listener)
}

export function useCardProgress(): CardProgressMode {
  return useSyncExternalStore(subscribe, () => progress, () => progress)
}
