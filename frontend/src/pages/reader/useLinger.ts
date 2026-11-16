import { useEffect, useState } from 'react'

/** True while `on` is, and for `ms` after it turns off, so a card can fade out before it unmounts. */
export function useLinger(on: boolean, ms: number): boolean {
  const [held, setHeld] = useState(on)
  useEffect(() => {
    if (on) {
      setHeld(true)
      return
    }
    const timer = setTimeout(() => setHeld(false), ms)
    return () => clearTimeout(timer)
  }, [on, ms])
  return on || held
}
