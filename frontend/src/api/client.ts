import { i18n } from '@lingui/core'
import { t } from '@lingui/core/macro'
import { OFFLINE_CODE, OFFLINE_HEADER, QUEUED_HEADER } from '../pwa/contract'
import { isUnreachable } from '../sw/net'
import { getConnectivity, onConnectivityChange, setConnectivity } from '../lib/connectivity'

interface InitializeInfo {
  apiRoot: string
  /** True while the account the multi-user migration created has never been claimed. */
  setupNeeded: boolean
  /**
   * An admin chose to show a blurred mosaic of Safe-rated covers behind the sign-in card. The image
   * can still 404 (too few covers), and the page then keeps its own art.
   */
  signInCovers: boolean
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

let initialize: Promise<InitializeInfo> | null = null

/**
 * Pre-authentication bootstrap. Carries no credential: it used to return the instance API key to
 * any anonymous caller, which made the key that guarded the API readable by anyone who could load
 * the page.
 */
export function getInitialize(): Promise<InitializeInfo> {
  if (initialize) return initialize
  // The promise is cached, not the value, so concurrent first callers share one fetch. A failure
  // is dropped so the next caller tries again instead of inheriting the rejection.
  const pending = (async () => {
    const res = await fetch('/initialize.json', { cache: 'no-cache' })
    if (!res.ok) throw new Error(t`Failed to initialize`)
    return (await res.json()) as InitializeInfo
  })()
  initialize = pending
  pending.catch(() => {
    if (initialize === pending) initialize = null
  })
  return pending
}

/** Forgets the bootstrap payload so the next caller re-reads it, for state that can change under a signed-out tab (SSO setup). */
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
  /** The stable dotted key behind `message` (`error.upgrades.trashGone`), or null when the body carried none. */
  readonly code: string | null
  /** A delete the recycle bin refused, which the same request with `permanent=true` may carry out instead. */
  readonly permanentDeleteAvailable: boolean
  /** The series a refusal is about (`error.series.archivedExists`), when the body names one. */
  readonly seriesId: number | null

  constructor(
    status: number,
    message: string,
    code: string | null = null,
    permanentDeleteAvailable = false,
    seriesId: number | null = null,
  ) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.code = code
    this.permanentDeleteAvailable = permanentDeleteAvailable
    this.seriesId = seriesId
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

let sessionGeneration = 0

/**
 * Marks a sign-in, sign-out or dropped session. A 401 answering a request sent before it belongs to
 * the session that ended: still thrown, but it must not sign out the account that has signed in since.
 */
export function beginSession(): void {
  sessionGeneration++
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
  return (await apiWithOrigin<T>(path, options)).data
}

/**
 * `api`, plus whether the answer came from this device's offline copy rather than the server. For
 * a caller that must not mistake a cached manifest for a live one (saving a chapter for offline).
 */
export async function apiWithOrigin<T>(
  path: string,
  options: RequestInit = {},
): Promise<{ data: T; servedOffline: boolean }> {
  const generation = sessionGeneration
  const init = await getInitialize()
  const res = await trackedFetch(`${init.apiRoot}${path}`, {
    ...options,
    // Explicit rather than relying on the default: the session cookie is the only credential now,
    // so a request that silently omitted it would fail in a way that looks like a server bug.
    credentials: 'same-origin',
    headers: authHeaders(options.headers),
  })
  return { data: await readResponse<T>(res, generation), servedOffline: servedOffline(res) }
}

/**
 * `api` for an endpoint that answers with bytes: the body as a Blob plus the response headers, for
 * a caller that reads sizes off them. Errors come back the same way as from `api`.
 */
export async function apiBlob(
  path: string,
  options: RequestInit = {},
): Promise<{ blob: Blob; headers: Headers }> {
  const generation = sessionGeneration
  const init = await getInitialize()
  const res = await trackedFetch(`${init.apiRoot}${path}`, {
    ...options,
    credentials: 'same-origin',
    headers: authHeaders(options.headers),
  })
  if (!res.ok) await readResponse<never>(res, generation)
  return { blob: await res.blob(), headers: res.headers }
}

/** True when the service worker answered from the device (or queued a write) instead of the server. */
export function servedOffline(res: Response): boolean {
  return res.headers.has(OFFLINE_HEADER) || res.headers.has(QUEUED_HEADER)
}

/** `fetch` that keeps the connectivity store in step with what it sees. */
export async function trackedFetch(input: string, init: RequestInit): Promise<Response> {
  let res: Response
  try {
    res = await fetch(input, init)
  } catch (err) {
    // A cancelled query aborts its fetch, which says nothing about the server.
    const aborted = init.signal?.aborted || (err instanceof Error && err.name === 'AbortError')
    if (!aborted) setConnectivity('offline')
    throw err
  }
  setConnectivity(servedOffline(res) || isUnreachable(res) ? 'offline' : 'online')
  return res
}

const PROBE_MS = 15_000
let probeTimer: ReturnType<typeof setInterval> | undefined

/** While offline, asks the server whether it is back: a quiet page may send nothing that would tell. */
function probeServer(): void {
  if (getConnectivity() !== 'offline') return
  void getInitialize()
    .then((init) => trackedFetch(`${init.apiRoot}/auth/me`, { credentials: 'same-origin', headers: authHeaders() }))
    .then((res) => res.body?.cancel())
    .catch(() => {})
}

function watchConnectivity(): void {
  if (getConnectivity() === 'offline') {
    probeTimer ??= setInterval(probeServer, PROBE_MS)
  } else {
    clearInterval(probeTimer)
    probeTimer = undefined
  }
}

if (typeof window !== 'undefined') {
  onConnectivityChange(watchConnectivity)
  watchConnectivity()
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible') probeServer()
  })
}

/** The service worker had no offline copy of what was asked for. */
export function isOfflineUnavailable(error: unknown): boolean {
  return error instanceof ApiError && error.code === OFFLINE_CODE
}

/**
 * `api` for a multipart POST. The browser writes the Content-Type with its boundary, so only the
 * language and antiforgery headers are sent; errors come back the same way as from `api`.
 */
export async function apiUpload<T>(path: string, form: FormData): Promise<T> {
  const generation = sessionGeneration
  const init = await getInitialize()
  const res = await trackedFetch(`${init.apiRoot}${path}`, {
    method: 'POST',
    credentials: 'same-origin',
    headers: { ...languageHeader(), ...xsrfHeader() },
    body: form,
  })
  return readResponse<T>(res, generation)
}

async function readResponse<T>(res: Response, generation: number): Promise<T> {
  if (res.status === 401) {
    // A 401 body may be missing, non-JSON, or JSON without an `error` field; any of those falls
    // back to UnauthorizedError's own generic message.
    let message: string | undefined
    try {
      const body = await res.text()
      message = (JSON.parse(body) as { error?: string }).error
    } catch { /* no body, not JSON, or no error field */ }
    if (generation === sessionGeneration) onUnauthorized?.()
    throw new UnauthorizedError(message)
  }
  if (!res.ok) {
    const body = await res.text()
    // The status stays on ApiError.status. A body that is not the server's own JSON (a reverse
    // proxy's HTML error page, say) is never shown.
    const status = res.status
    const code = errorCode(body)
    if (code === OFFLINE_CODE) throw new ApiError(status, t`Not available offline`, code)
    throw new ApiError(
      status,
      errorMessage(body) ?? t`The server returned an error (${status}).`,
      code,
      offersPermanentDelete(body),
      errorSeriesId(body),
    )
  }
  // 204, and any 200 whose handler wrote no body, have nothing to parse.
  const body = await res.text()
  if (!body) return undefined as T
  try {
    return JSON.parse(body) as T
  } catch {
    const status = res.status
    throw new ApiError(status, t`The server sent a response Maki could not read (${status}).`)
  }
}

/**
 * Controllers answer failures as `{ "error": "...", "code": "..." }`. `error` is already localized by
 * the server, so it is displayed as-is; a body that is not JSON, or carries none of the known
 * fields, yields null and the caller words a generic message. `code` is the
 * stable dotted key behind it (see `errorCode`), for a caller that wants to branch on a specific
 * failure rather than show it.
 */
function errorMessage(body: string): string | null {
  if (!body) return null
  try {
    // `detail`/`title` are ASP.NET ProblemDetails, which bare `NotFound()` and model binding produce.
    const parsed = JSON.parse(body) as { error?: string; message?: string; detail?: string; title?: string }
    return parsed.error ?? parsed.message ?? parsed.detail ?? parsed.title ?? null
  } catch {
    return null
  }
}

/** The `code` field from a `{ "error": "...", "code": "..." }` body, or null when there isn't one. */
function offersPermanentDelete(body: string): boolean {
  if (!body) return false
  try {
    return (JSON.parse(body) as { permanentDeleteAvailable?: boolean }).permanentDeleteAvailable === true
  } catch {
    return false
  }
}

/** The archived series an add or an approval ran into, so the caller can offer Restore. */
export function archivedExistsSeriesId(error: unknown): number | null {
  return error instanceof ApiError && error.code === 'error.series.archivedExists' ? error.seriesId : null
}

function errorSeriesId(body: string): number | null {
  if (!body) return null
  try {
    const id = (JSON.parse(body) as { seriesId?: unknown }).seriesId
    return typeof id === 'number' ? id : null
  } catch {
    return null
  }
}

function errorCode(body: string): string | null {
  if (!body) return null
  try {
    return (JSON.parse(body) as { code?: string }).code ?? null
  } catch {
    return null
  }
}
