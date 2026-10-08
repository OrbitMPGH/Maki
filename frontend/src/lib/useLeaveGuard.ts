import { useContext, useEffect, useRef } from 'react'
import { UNSAFE_NavigationContext, parsePath, type To } from 'react-router-dom'

export type LeaveTarget = { pathname: string; search: string }

// BrowserRouter has no useBlocker, so this wraps the history object's push/replace/go and catches
// the browser's own Back/Forward at the window. `blocks` decides per destination; `onBlock` receives
// a `proceed` that replays the held navigation past the guard.
export function useLeaveGuard(
  blocks: (target: LeaveTarget) => boolean,
  onBlock: (proceed: () => void) => void,
) {
  const { navigator } = useContext(UNSAFE_NavigationContext)
  const latest = useRef({ blocks, onBlock })
  latest.current = { blocks, onBlock }

  useEffect(() => {
    const nav = navigator as typeof navigator & { go(delta: number): void }
    const { push, replace, go } = nav
    let bypass = false
    // A popstate this hook triggered itself. Entries expire because a go() that leaves the document
    // (or a cancelled beforeunload prompt) never delivers one, and a stale entry would let the next
    // real Back through unguarded.
    let expectedPops: number[] = []
    const expectPop = () => expectedPops.push(Date.now() + 1000)
    const takeExpectedPop = () => {
      const now = Date.now()
      expectedPops = expectedPops.filter((expiry) => expiry > now)
      return expectedPops.shift() !== undefined
    }
    let idx: number | null = window.history.state?.idx ?? null
    const readIdx = () => {
      idx = window.history.state?.idx ?? null
    }
    const pass = (run: () => void) => {
      bypass = true
      try {
        run()
      } finally {
        bypass = false
      }
      readIdx()
    }
    const toTarget = (to: To): LeaveTarget => {
      const path = typeof to === 'string' ? parsePath(to) : to
      return { pathname: path.pathname ?? window.location.pathname, search: path.search ?? '' }
    }

    nav.push = (to, state, opts) => {
      if (!bypass && latest.current.blocks(toTarget(to))) {
        latest.current.onBlock(() => pass(() => push.call(nav, to, state, opts)))
        return
      }
      push.call(nav, to, state, opts)
      readIdx()
    }
    nav.replace = (to, state, opts) => {
      if (!bypass && latest.current.blocks(toTarget(to))) {
        latest.current.onBlock(() => pass(() => replace.call(nav, to, state, opts)))
        return
      }
      replace.call(nav, to, state, opts)
      readIdx()
    }
    nav.go = (delta) => {
      if (!bypass && latest.current.blocks({ pathname: '', search: '' })) {
        latest.current.onBlock(() => {
          expectPop()
          go.call(nav, delta)
        })
        return
      }
      go.call(nav, delta)
    }

    // Registered in the capture phase so it runs before the router's own listener. A blocked
    // Back/Forward is stopped there and undone with history.go; the router never saw it.
    const onPop = (event: PopStateEvent) => {
      if (takeExpectedPop()) {
        readIdx()
        return
      }
      const next: number | null = window.history.state?.idx ?? null
      if (idx === null || next === null || next === idx) {
        readIdx()
        return
      }
      const delta = next - idx
      if (!latest.current.blocks({ pathname: window.location.pathname, search: window.location.search })) {
        idx = next
        return
      }
      event.stopImmediatePropagation()
      expectPop()
      window.history.go(-delta)
      latest.current.onBlock(() => {
        expectPop()
        window.history.go(delta)
      })
    }
    window.addEventListener('popstate', onPop, true)

    return () => {
      nav.push = push
      nav.replace = replace
      nav.go = go
      window.removeEventListener('popstate', onPop, true)
    }
  }, [navigator])
}
