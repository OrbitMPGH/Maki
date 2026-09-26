import { i18n } from '@lingui/core'
import { t } from '@lingui/core/macro'

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
    if (!res.ok) throw new Error(t`Failed to initialize`)
    initialize = (await res.json()) as InitializeInfo
  }
  return initialize
}

export function invalidateInitialize(): void {
  initialize = null
}

/** Raised for a 401 so callers can distinguish "signed out" from a genuine request failure. */
export class UnauthorizedError extends Error {
  constructor(message?: string) {
    super(message ?? t`Unauthorized`)
    this.name = 'UnauthorizedError'
  }
}

export class ApiError extends Error {
  readonly status: number

  constructor(status: number, message: string) {
    super(message)
    this.name = 'ApiError'
    this.status = status
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
    ...languageHeader(),
    ...xsrfHeader(),
    ...(extra as Record<string, string> | undefined),
  }
}

/**
 * Tells the API which language to answer in. Error messages, notification bodies and queue labels
 * are all rendered server-side, so without this they would come back in whatever the server guessed.
 *
 * Every request goes through `authHeaders`, which is why one line here is enough. It matters most
 * before sign-in, where there is no stored preference for the server to read and this is the only
 * thing that says what the browser resolved.
 */
function languageHeader(): Record<string, string> {
  return i18n.locale ? { 'X-Maki-Language': i18n.locale } : {}
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
    // A 401 body may be missing, non-JSON, or JSON without an `error` field; any of those falls
    // back to UnauthorizedError's own generic message.
    let message: string | undefined
    try {
      const body = await res.text()
      message = (JSON.parse(body) as { error?: string }).error
    } catch { /* no body, not JSON, or no error field */ }
    onUnauthorized?.()
    throw new UnauthorizedError(message)
  }
  if (!res.ok) {
    const body = await res.text()
    throw new ApiError(res.status, `API ${res.status}: ${errorMessage(body) ?? res.statusText}`)
  }
  // 204, and any 200 whose handler wrote no body, have nothing to parse.
  const body = await res.text()
  if (!body) return undefined as T
  return JSON.parse(body) as T
}

/**
 * Controllers answer failures as `{ "error": "...", "code": "..." }`; fall back to the raw body when
 * they don't. `error` is already localized by the server, so it is displayed as-is. `code` is the
 * stable dotted key behind it, for a caller that wants to branch on a specific failure rather than
 * show it; nothing reads it yet.
 */
function errorMessage(body: string): string | null {
  if (!body) return null
  try {
    // `detail`/`title` are ASP.NET ProblemDetails, which bare `NotFound()` and model binding produce.
    const parsed = JSON.parse(body) as { error?: string; message?: string; detail?: string; title?: string }
    return parsed.error ?? parsed.message ?? parsed.detail ?? parsed.title ?? body
  } catch {
    return body
  }
}
