const KEY = 'update-skipped-version'
// Written by the old update banner; read so a version dismissed there stays skipped.
const LEGACY_KEY = 'update-banner-dismissed'

const listeners = new Set<() => void>()
let memory: string | null = null

export function getSkippedVersion(): string | null {
  try {
    return localStorage.getItem(KEY) ?? localStorage.getItem(LEGACY_KEY)
  } catch {
    return memory
  }
}

export function setSkippedVersion(version: string | null) {
  memory = version
  try {
    if (version) localStorage.setItem(KEY, version)
    else localStorage.removeItem(KEY)
    localStorage.removeItem(LEGACY_KEY)
  } catch {
    /* private mode: skipped for this visit only */
  }
  listeners.forEach((cb) => cb())
}

export function subscribeSkippedVersion(cb: () => void) {
  listeners.add(cb)
  return () => {
    listeners.delete(cb)
  }
}
