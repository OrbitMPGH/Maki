import { useEffect, useRef } from 'react'

// Pops this module caused itself (closing by button steps back over the modal's entry). They expire,
// because a back() that never delivers a popstate would otherwise swallow the next real Back.
let ownPops: number[] = []
let installed = false

function swallowOwnPop(event: PopStateEvent) {
  const now = Date.now()
  ownPops = ownPops.filter((expiry) => expiry > now)
  if (ownPops.shift() !== undefined) event.stopImmediatePropagation()
}

/**
 * Gives a large modal a history entry of its own, so the browser's or phone's Back closes it instead
 * of leaving the page. The entry keeps the URL and the router's index, and the pops in and out of it
 * are stopped before the router sees them, so the page underneath never re-renders for them.
 * Closing by button steps back over the entry. A modal left by following a link inside it keeps its
 * entry, which costs one extra Back later rather than a lost page.
 */
export function useModalHistory(opened: boolean, onClose: () => void) {
  const latest = useRef(onClose)
  latest.current = onClose

  useEffect(() => {
    if (!opened) return
    if (!installed) {
      window.addEventListener('popstate', swallowOwnPop, true)
      installed = true
    }
    const key = `modal-${Date.now()}-${Math.random().toString(36).slice(2)}`
    const below = window.history.state
    let open = false

    const onPop = (event: PopStateEvent) => {
      if (!open || (event.state as { modal?: string } | null)?.modal === key) return
      open = false
      // Back to the entry this modal was opened over: nothing for the router to do.
      if ((event.state as { idx?: number } | null)?.idx === below?.idx) event.stopImmediatePropagation()
      latest.current()
    }
    // A task later, so StrictMode's mount, unmount, mount in development pushes one entry, not two
    // with a step back racing between them.
    const timer = window.setTimeout(() => {
      window.history.pushState({ ...below, modal: key }, '')
      open = true
      window.addEventListener('popstate', onPop, true)
    }, 0)

    return () => {
      window.clearTimeout(timer)
      window.removeEventListener('popstate', onPop, true)
      if (open && (window.history.state as { modal?: string } | null)?.modal === key) {
        ownPops.push(Date.now() + 1000)
        window.history.back()
      }
      open = false
    }
  }, [opened])
}
