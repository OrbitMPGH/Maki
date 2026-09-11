import { useEffect, useState, type Dispatch, type SetStateAction } from 'react'

const PREFIX = 'maki-page:'

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
  const [value, setValue] = useState<T>(() => {
    try {
      const raw = key == null ? null : sessionStorage.getItem(PREFIX + key)
      // Wrapped rather than stored bare so `null`, `0` and `""` are all distinguishable from
      // "nothing stored", which `getItem` reports the same way.
      if (raw) return (JSON.parse(raw) as { v: T }).v
    } catch { /* unparseable or unavailable; the default is a fine answer */ }
    return typeof initial === 'function' ? (initial as () => T)() : initial
  })

  useEffect(() => {
    if (key == null) return
    try {
      sessionStorage.setItem(PREFIX + key, JSON.stringify({ v: value }))
    } catch { /* private mode or a full quota; losing the snapshot beats failing the render */ }
  }, [key, value])

  return [value, setValue]
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
