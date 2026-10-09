import { useCallback, useEffect, useState, type Dispatch, type SetStateAction } from 'react'

const PREFIX = 'maki-page:'
export const SCROLL_PREFIX = 'maki-scroll:'

/**
 * Forgets everything the tab remembered about where the previous account was: page filters, search
 * text and scroll offsets. sessionStorage outlives a sign-out on the same tab, so without this the
 * next account opens Library with the last one's search applied.
 */
export function clearTabState(): void {
  try {
    for (const key of Object.keys(sessionStorage)) {
      if (key.startsWith(PREFIX) || key.startsWith(SCROLL_PREFIX)) sessionStorage.removeItem(key)
    }
  } catch { /* storage unavailable; there is nothing remembered to leak */ }
  for (const listener of clearListeners) listener()
}

const clearListeners = new Set<() => void>()

/** Lets in-memory per-tab state (the nav stack) reset together with the stored state. */
export function onTabStateCleared(listener: () => void): () => void {
  clearListeners.add(listener)
  return () => {
    clearListeners.delete(listener)
  }
}

const USER_KEY = 'maki-user'

/**
 * Records who is signed in on this tab and clears the tab's remembered state when it is somebody
 * else. Kept in sessionStorage, so it survives a 401 (an expired session signing back in as the
 * same person keeps their filters, scroll and back links), a reload, and the SSO redirect.
 */
export function noteSignedInUser(id: number): void {
  try {
    const previous = sessionStorage.getItem(USER_KEY)
    if (previous !== null && previous !== String(id)) clearTabState()
    sessionStorage.setItem(USER_KEY, String(id))
  } catch { /* storage unavailable; nothing is remembered between users either */ }
}

/** An explicit sign-out: the next person to sign in starts clean even if it is the same account. */
export function noteSignedOut(): void {
  try {
    sessionStorage.removeItem(USER_KEY)
  } catch { /* see noteSignedInUser */ }
  clearTabState()
}

/**
 * `useState` that survives the page being unmounted and mounted again.
 *
 * Every list page in the app owns its filters in local state, so leaving one and coming back
 * remounts it empty: the twelve filters you set on Discover to find something worth adding are
 * gone by the time you have added it. Backing that state with sessionStorage is what lets the
 * series page's back link land you on the page you actually left rather than on its default view.
 *
 * sessionStorage, not localStorage: this is "where I was", which belongs to one tab and should not
 * outlive it or leak sideways into a second window someone opened to look at something else.
 *
 * Only serialisable state belongs here. Selections, open modals and anything holding a DOM node or
 * a fetched object stay on plain `useState`, both because they will not survive `JSON` and because
 * restoring a half-finished modal is not "where I was".
 */
export function usePageState<T>(
  /** Where to remember this under. `null` opts out, for a caller whose scope is conditional. */
  key: string | null,
  initial: T | (() => T),
): [T, Dispatch<SetStateAction<T>>] {
  const load = (k: string | null): T => {
    try {
      const raw = k == null ? null : sessionStorage.getItem(PREFIX + k)
      // Wrapped rather than stored bare so `null`, `0` and `""` are all distinguishable from
      // "nothing stored", which `getItem` reports the same way.
      if (raw) return (JSON.parse(raw) as { v: T }).v
    } catch { /* unparseable or unavailable; the default is a fine answer */ }
    return typeof initial === 'function' ? (initial as () => T)() : initial
  }

  const [slot, setSlot] = useState<{ key: string | null; value: T }>(() => ({ key, value: load(key) }))

  // A page that stays mounted while its scope changes (one component, many creators) must show the
  // new scope's remembered value, not carry the old one across and write it under the new key.
  // Moving to or from an opted-out `null` keeps the value, as it always did.
  let current = slot
  if (slot.key !== key) {
    current = key != null && slot.key != null ? { key, value: load(key) } : { key, value: slot.value }
    setSlot(current)
  }

  const setValue = useCallback<Dispatch<SetStateAction<T>>>((action) => {
    setSlot((prev) => ({
      key: prev.key,
      value: typeof action === 'function' ? (action as (p: T) => T)(prev.value) : action,
    }))
  }, [])

  useEffect(() => {
    if (current.key == null) return
    try {
      sessionStorage.setItem(PREFIX + current.key, JSON.stringify({ v: current.value }))
    } catch { /* private mode or a full quota; losing the snapshot beats failing the render */ }
  }, [current.key, current.value])

  return [current.value, setValue]
}

/**
 * Remembers what an effect's dependencies were on the first render, so the effect can tell an
 * actual change from the values it was mounted with.
 *
 * For the effects that reset something when their inputs change ("a new filter set starts the list
 * over"): with restored state those inputs arrive already non-default, so a plain effect fires on
 * mount and undoes the restore. A boolean "skip the first run" flag does not work either, because
 * StrictMode mounts twice and the second pass would not skip. Comparing against the mount-time
 * values is stable across both.
 */
export function useUnchangedSinceMount(deps: readonly unknown[]): boolean {
  const [atMount] = useState(deps)
  return deps.length === atMount.length && deps.every((d, i) => Object.is(d, atMount[i]))
}
