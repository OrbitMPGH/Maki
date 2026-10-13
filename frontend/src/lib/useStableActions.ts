import { useLayoutEffect, useRef, useState } from 'react'

/**
 * A fixed object whose methods always call the latest render's `actions`. Memoized children can take
 * handlers that close over current state without re-rendering every time that state changes. Only
 * for event handlers: a call made during render would still see the previous render's closures.
 */
export function useStableActions<T extends Record<string, (...args: never[]) => unknown>>(actions: T): T {
  const latest = useRef(actions)
  useLayoutEffect(() => {
    latest.current = actions
  })
  const [stable] = useState(() => {
    const out: Record<string, (...args: unknown[]) => unknown> = {}
    for (const key of Object.keys(actions)) {
      out[key] = (...args) => (latest.current[key] as (...args: unknown[]) => unknown)(...args)
    }
    return out as unknown as T
  })
  return stable
}
