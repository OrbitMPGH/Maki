import { useEffect } from 'react'
import { requestWakeLock, wakeLockSupported } from '../../lib/wakeLock'
import { ACTIVITY_EVENTS, IDLE_MS } from './useReadingClock'

/**
 * Keeps the screen on while the reader is open, visible and being used. The browser drops the lock
 * by itself when the tab is hidden, so it is taken again on return, and it is let go after the same
 * stretch without input that stops the reading clock: a reader left open on a desk should dim.
 */
export function useWakeLock(enabled: boolean) {
  useEffect(() => {
    if (!enabled || !wakeLockSupported()) return

    let held: WakeLockSentinel | null = null
    let requesting = false
    let disposed = false
    let idle = false
    let lastActivity = Date.now()
    let idleTimer: ReturnType<typeof setTimeout> | undefined

    const wanted = () => document.visibilityState === 'visible' && !idle

    const release = () => {
      const lock = held
      held = null
      void lock?.release().catch(() => {})
    }

    const sync = () => {
      if (disposed) return
      if (!wanted()) {
        release()
        return
      }
      if (held || requesting) return
      requesting = true
      void requestWakeLock().then((lock) => {
        requesting = false
        if (!lock) return
        if (disposed || !wanted()) {
          void lock.release().catch(() => {})
          return
        }
        held = lock
        lock.addEventListener('release', () => {
          if (held === lock) held = null
        })
      })
    }

    const watchIdle = () => {
      const remaining = IDLE_MS - (Date.now() - lastActivity)
      idleTimer = setTimeout(() => {
        if (Date.now() - lastActivity >= IDLE_MS) {
          idle = true
          sync()
        } else {
          watchIdle()
        }
      }, Math.max(remaining, 1000))
    }

    const poke = () => {
      lastActivity = Date.now()
      if (!idle) return
      idle = false
      watchIdle()
      sync()
    }

    for (const name of ACTIVITY_EVENTS) {
      window.addEventListener(name, poke, { capture: true, passive: true })
    }
    document.addEventListener('visibilitychange', sync)
    watchIdle()
    sync()

    return () => {
      disposed = true
      clearTimeout(idleTimer)
      document.removeEventListener('visibilitychange', sync)
      for (const name of ACTIVITY_EVENTS) {
        window.removeEventListener(name, poke, { capture: true })
      }
      release()
    }
  }, [enabled])
}
