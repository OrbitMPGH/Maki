/**
 * `navigator.wakeLock` only exists in secure contexts, so a self-hosted instance reached over plain
 * HTTP on a LAN address has none. Callers hide the option when this says no.
 */
export function wakeLockSupported(): boolean {
  return typeof navigator !== 'undefined' && 'wakeLock' in navigator
}

/** A screen wake lock, or null when the browser refuses (battery saver, hidden tab) or has none. */
export async function requestWakeLock(): Promise<WakeLockSentinel | null> {
  if (!wakeLockSupported()) return null
  try {
    return await navigator.wakeLock.request('screen')
  } catch {
    return null
  }
}
