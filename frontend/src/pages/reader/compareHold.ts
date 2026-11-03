import { useEffect, useSyncExternalStore } from 'react'

let held = false
const listeners = new Set<() => void>()

function subscribe(listener: () => void) {
  listeners.add(listener)
  return () => listeners.delete(listener)
}

/** While held, every enhanced page shows its original image. */
export function setCompareHeld(value: boolean) {
  if (held === value) return
  held = value
  for (const listener of listeners) listener()
}

export function useCompareHeld(): boolean {
  return useSyncExternalStore(subscribe, () => held)
}

/** Holding H shows the originals. Released on key up, on losing focus, and when the view goes. */
export function useCompareKey(active: boolean) {
  useEffect(() => {
    if (!active) return
    const isH = (event: KeyboardEvent) => event.key.toLowerCase() === 'h'
    const down = (event: KeyboardEvent) => {
      if (!isH(event) || event.repeat || event.defaultPrevented) return
      if (event.metaKey || event.ctrlKey || event.altKey) return
      const target = event.target as HTMLElement | null
      if (target && (['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName) || target.isContentEditable)) return
      setCompareHeld(true)
    }
    const up = (event: KeyboardEvent) => {
      if (isH(event)) setCompareHeld(false)
    }
    const release = () => setCompareHeld(false)
    window.addEventListener('keydown', down)
    window.addEventListener('keyup', up)
    window.addEventListener('blur', release)
    return () => {
      window.removeEventListener('keydown', down)
      window.removeEventListener('keyup', up)
      window.removeEventListener('blur', release)
      release()
    }
  }, [active])
}
