import { useLayoutEffect } from 'react'
import { UNSAFE_createBrowserHistory, useLocation } from 'react-router-dom'

/**
 * Cross-fades between pages with the View Transitions API.
 *
 * BrowserRouter has no hook for this (React Router only offers `viewTransition` on the data
 * routers), so the app runs on its own history object and delays each route change until the
 * browser has captured the old page. The router's listener then runs inside the transition's
 * update callback, which waits for React to commit the new route before the new page is captured.
 * The scroll reset in ScrollMemory happens inside that window too, so the old page fades out where
 * the reader left it rather than jumping to the top first.
 *
 * Only a change of path animates. Filters, tabs and modals ride on search and state, and those
 * stay instant. `localStorage['page-transition'] = 'off'` turns it off, for comparing.
 */

export const appHistory = UNSAFE_createBrowserHistory({ v5Compat: true })

// A lazy route can hold the commit back while its chunk loads. Past this the transition gives up
// waiting and the new page arrives whenever it is ready.
const COMMIT_TIMEOUT_MS = 400

function disabled(): boolean {
  try {
    if (localStorage.getItem('page-transition') === 'off') return true
  } catch { /* storage blocked; animate as usual */ }
  return window.matchMedia('(prefers-reduced-motion: reduce)').matches
}

// The reader pages between chapters with its own motion, and a fade on top of it reads as lag.
const isReader = (path: string) => path.startsWith('/read/')

let shownPath = appHistory.location.pathname
let settle: (() => void) | null = null

/** The new page drifts up into place while it fades in. Without the API it fades in alone. */
function rise(fade: boolean) {
  const main = document.getElementById('main')
  if (!main) return
  main.animate(
    fade
      ? [{ opacity: 0, transform: 'translateY(6px)' }, { opacity: 1, transform: 'none' }]
      : [{ transform: 'translateY(6px)' }, { transform: 'none' }],
    { duration: 320, easing: 'cubic-bezier(0.16, 1, 0.3, 1)' },
  )
}

const listen = appHistory.listen
appHistory.listen = (listener) =>
  listen((update) => {
    const from = shownPath
    const to = update.location.pathname
    shownPath = to
    if (from === to || (isReader(from) && isReader(to)) || disabled()) {
      listener(update)
      return
    }
    if (!document.startViewTransition) {
      listener(update)
      settle = () => {
        settle = null
        rise(true)
      }
      return
    }
    document.startViewTransition(() => {
      listener(update)
      settle?.()
      return new Promise<void>((resolve) => {
        const done = () => {
          window.clearTimeout(timer)
          if (settle === done) settle = null
          rise(false)
          resolve()
        }
        const timer = window.setTimeout(done, COMMIT_TIMEOUT_MS)
        settle = done
      })
    })
  })

/** Rendered inside the router: tells a pending transition the new route has committed. */
export function PageTransitionCommit() {
  const { key } = useLocation()
  useLayoutEffect(() => {
    settle?.()
  }, [key])
  return null
}
