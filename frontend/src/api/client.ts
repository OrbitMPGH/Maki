interface InitializeInfo {
  apiRoot: string
  version: string
  /** True while the account the multi-user migration created has never been claimed. */
  setupNeeded: boolean
  /**
   * Enough to draw the login page and no more. The issuer, client id and secret stay behind the
   * admin settings endpoint; this one is anonymous.
   */
  oidc: {
    enabled: boolean
    displayName: string
    /** Password sign-in is admin-only. Admins keep it so a broken provider is never a lockout. */
    localLoginRestricted: boolean
  }
}

let initialize: InitializeInfo | null = null

/**
 * Pre-authentication bootstrap. Carries no credential: it used to return the instance API key to
 * any anonymous caller, which made the key that guarded the API readable by anyone who could load
 * the page.
 */
export async function getInitialize(): Promise<InitializeInfo> {
  if (!initialize) {
    const res = await fetch('/initialize.json', { cache: 'no-cache' })
    if (!res.ok) throw new Error('Failed to initialize')
    initialize = (await res.json()) as InitializeInfo
  }
  return initialize
}

export function invalidateInitialize(): void {
  initialize = null
}

/** Raised for a 401 so callers can distinguish "signed out" from a genuine request failure. */
export class UnauthorizedError extends Error {
  constructor() {
    super('Unauthorized')
    this.name = 'UnauthorizedError'
  }
}

type UnauthorizedHandler = () => void
let onUnauthorized: UnauthorizedHandler | null = null

/**
 * Registered once by AuthProvider. Kept as a module-level hook rather than threaded through every
 * call site because a 401 can surface from any of ~150 queries and they all need the same answer:
 * drop to the login screen.
 */
export function setUnauthorizedHandler(handler: UnauthorizedHandler | null): void {
  onUnauthorized = handler
}

const XSRF_COOKIE = 'XSRF-TOKEN'

function readCookie(name: string): string | null {
  const prefix = `${name}=`
  for (const part of document.cookie.split('; ')) {
    if (part.startsWith(prefix)) return decodeURIComponent(part.slice(prefix.length))
  }
  return null
}

/**
 * Headers every request needs: JSON content type plus, on mutations, the antiforgery token echoed
 * from a cookie the server set.
 *
 * The session itself travels as an HttpOnly cookie the browser attaches on its own; there is no
 * credential in JavaScript's reach, which is the point. That also reintroduces CSRF, so the server
 * requires this header on any cookie-authenticated mutation; being able to read the cookie at all is
 * what same-origin policy denies an attacker's page.
 */
export function authHeaders(extra?: HeadersInit): HeadersInit {
  return {
    'Content-Type': 'application/json',
    ...xsrfHeader(),
    ...(extra as Record<string, string> | undefined),
  }
}

/**
 * The antiforgery header alone, with no Content-Type. For multipart uploads, where setting a
 * Content-Type would suppress the boundary the browser needs to generate.
 */
export function xsrfHeader(): Record<string, string> {
  const token = readCookie(XSRF_COOKIE)
  return token ? { 'X-XSRF-TOKEN': token } : {}
}

export async function api<T>(path: string, options: RequestInit = {}): Promise<T> {
  const init = await getInitialize()
  const res = await fetch(`${init.apiRoot}${path}`, {
    ...options,
    // Explicit rather than relying on the default: the session cookie is the only credential now,
    // so a request that silently omitted it would fail in a way that looks like a server bug.
    credentials: 'same-origin',
    headers: authHeaders(options.headers),
  })
  if (res.status === 401) {
    onUnauthorized?.()
    throw new UnauthorizedError()
  }
  if (!res.ok) {
    const body = await res.text()
    throw new Error(`API ${res.status}: ${errorMessage(body) ?? res.statusText}`)
  }
  // 204, and any 200 whose handler wrote no body, have nothing to parse.
  const body = await res.text()
  if (!body) return undefined as T
  return JSON.parse(body) as T
}

/** Controllers answer failures as `{ "error": "..." }`; fall back to the raw body when they don't. */
function errorMessage(body: string): string | null {
  if (!body) return null
  try {
    const parsed = JSON.parse(body) as { error?: string; message?: string }
    return parsed.error ?? parsed.message ?? body
  } catch {
    return body
  }
}
