import { SW_URL, type PageToWorker } from '../../pwa/contract'

/**
 * Service workers, Cache Storage and Push exist only in a secure context. Most self-hosters reach
 * Maki over plain HTTP on the LAN, where all of this is absent and every caller has to cope.
 */
export function pwaSupported(): boolean {
  try {
    return window.isSecureContext && 'serviceWorker' in navigator && 'caches' in window
  } catch {
    return false
  }
}

let registered = false

/**
 * Registers `/sw.js` once, from main.tsx, in production builds. `currentUser` reads the `me` query:
 * a number or null once known, undefined while it is still loading.
 */
export function registerServiceWorker(currentUser: () => number | null | undefined): void {
  if (registered || !pwaSupported()) return
  registered = true
  const container = navigator.serviceWorker
  // A worker that installs after sign-in, or replaces one that never heard of this user, still has
  // to be told who is signed in.
  const repost = () => {
    const userId = currentUser()
    if (userId !== undefined) void postToWorker({ type: 'user', userId })
  }
  container.register(SW_URL, { scope: '/', updateViaCache: 'none' }).catch((err: unknown) => {
    console.error('Service worker registration failed', err)
  })
  void container.ready.then(repost)
  container.addEventListener('controllerchange', () => {
    repost()
    void warmBootstrap(currentUser())
  })
}

/**
 * On the first visit the page's bootstrap requests go out before the worker controls it, so neither
 * is cached and the next offline launch shows the login form. Once the worker takes control, both
 * are fetched again through it.
 */
async function warmBootstrap(userId: number | null | undefined): Promise<void> {
  try {
    const res = await fetch('/initialize.json', { cache: 'no-cache' })
    if (!res.ok || userId === null) return
    const { apiRoot } = (await res.json()) as { apiRoot?: unknown }
    if (typeof apiRoot !== 'string') return
    const me = await fetch(`${apiRoot}/auth/me`, { credentials: 'same-origin' })
    await me.body?.cancel()
  } catch {
    // Unreachable right now; the next controlled load caches both.
  }
}

/**
 * Waits for an active worker rather than posting to `controller`, which stays null on the first
 * visit until a reload, so a message sent while the worker installs is not lost.
 */
export async function postToWorker(message: PageToWorker): Promise<void> {
  if (!registered) return
  try {
    const registration = await navigator.serviceWorker.ready
    registration.active?.postMessage(message)
  } catch {
    // No worker after all; it reads the session from IDB when it does start.
  }
}
