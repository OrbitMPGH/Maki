import { useEffect, useRef, useState } from 'react'

/** How long the mouse has to sit still over the page before its cursor goes. */
export const CURSOR_IDLE_MS = 2500

/**
 * True once a mouse has sat still for a moment, so the page can hide its cursor. Only a mouse
 * counts: touch and pen never show one. `suspended` (the pointer is over the toolbar, a menu is
 * open) shows it again and restarts the wait when it ends. The wait only starts after the mouse
 * has moved at all, so a cursor nobody is using stays out of it.
 */
export function useIdleCursor(suspended: boolean, delay = CURSOR_IDLE_MS): boolean {
  const [idle, setIdle] = useState(false)
  const moved = useRef(false)
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined)

  useEffect(() => {
    clearTimeout(timer.current)
    if (suspended) {
      setIdle(false)
      return
    }
    const arm = () => {
      clearTimeout(timer.current)
      timer.current = setTimeout(() => setIdle(true), delay)
    }
    if (moved.current) arm()
    const onMove = (event: PointerEvent) => {
      if (event.pointerType !== 'mouse') return
      // Some browsers repeat a move when the page changes under a still pointer.
      if (moved.current && event.movementX === 0 && event.movementY === 0 && event.type === 'pointermove') return
      moved.current = true
      setIdle(false)
      arm()
    }
    window.addEventListener('pointermove', onMove)
    window.addEventListener('pointerdown', onMove)
    return () => {
      window.removeEventListener('pointermove', onMove)
      window.removeEventListener('pointerdown', onMove)
      clearTimeout(timer.current)
    }
  }, [suspended, delay])

  return idle && !suspended
}
