import { useEffect, useSyncExternalStore } from 'react'
import { comboFromEvent, useShortcutBindings } from '../../lib/shortcuts'

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

/**
 * Holding the compare key (H) shows the originals. Released when the physical key that started the
 * hold comes up (its character can differ by then: Shift+/ is ? going down and / coming up), when a
 * modifier of the binding comes up (a Mac sends no key-up for a key held with Cmd), on losing focus,
 * and when the view goes.
 */
export function useCompareKey(active: boolean) {
  const keys = useShortcutBindings()['reader.compare']
  useEffect(() => {
    if (!active || keys.length === 0) return
    let heldCode: string | null = null
    const down = (event: KeyboardEvent) => {
      if (event.repeat || event.defaultPrevented) return
      const combo = comboFromEvent(event)
      if (!combo || !keys.includes(combo)) return
      const target = event.target as HTMLElement | null
      if (target && (['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName) || target.isContentEditable)) return
      heldCode = event.code
      setCompareHeld(true)
    }
    const release = () => {
      heldCode = null
      setCompareHeld(false)
    }
    const up = (event: KeyboardEvent) => {
      if (heldCode === null) return
      if (event.code === heldCode || ['Meta', 'Control', 'Alt'].includes(event.key)) release()
    }
    window.addEventListener('keydown', down)
    window.addEventListener('keyup', up)
    window.addEventListener('blur', release)
    return () => {
      window.removeEventListener('keydown', down)
      window.removeEventListener('keyup', up)
      window.removeEventListener('blur', release)
      release()
    }
  }, [active, keys])
}
