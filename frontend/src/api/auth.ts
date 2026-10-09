import { useMutation, useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query'
import { stopConnection } from './signalr'
import { api, invalidateInitialize } from './client'
import { noteSignedInUser, noteSignedOut } from '../lib/pageState'
import { useSaveSettingsRecord } from './settingsRecord'

type SetupDoneHandler = () => void
let onSetupDone: SetupDoneHandler | null = null

/**
 * Registered once by AuthProvider, which owns `setupNeeded` state. `POST auth/setup` succeeding is
 * the only place that state needs to flip without a page reload.
 */
export function setSetupDoneHandler(handler: SetupDoneHandler | null): void {
  onSetupDone = handler
}

function setSetupDone(): void {
  onSetupDone?.()
}

/**
 * The permission names the server sends in `MeDto.permissionNames`. Admin is expanded server-side, so
 * an admin's list already contains every other name and the client never has to know that Admin
 * implies the rest.
 */
export type Permission =
  | 'Admin'
  | 'AddSeries'
  | 'DeleteSeries'
  | 'DownloadChapters'
  | 'ManageDownloadQueue'
  | 'ManageSources'
  | 'EditMetadata'
  | 'ManageTags'
  | 'ChangeContentRating'
  | 'UseTrackers'
  | 'UseOpds'
  | 'ImportLibrary'

export interface Me {
  id: number
  userName: string
  displayName: string | null
  permissions: number
  permissionNames: Permission[]
  isAdmin: boolean
  maxContentRating: string
  allRootFolders: boolean
  rootFolderIds: number[]
  twoFactorEnabled: boolean
  oidcLinked: boolean
  oidcUserName: string | null
}

export interface UserSummary extends Me {
  disabled: boolean
  pendingSetup: boolean
  createdAt: string
  lastLoginAt: string | null
}

export interface LoginResult {
  requiresTwoFactor?: boolean
}

export type ApiKeyScope = 'Full' | 'Opds'

export interface ApiKey {
  id: number
  name: string
  prefix: string
  scope: ApiKeyScope
  createdAt: string
  lastUsedAt: string | null
  revokedAt: string | null
}

export interface CreatedApiKey {
  key: ApiKey
  /** Shown once. Only the digest is stored, so there is no way to retrieve it later. */
  secret: string
}

export interface AuthEvent {
  timestamp: string
  type: string
  userId: number | null
  userName: string
  clientIp: string | null
  userAgent: string | null
  detail: string | null
}

export const ME_QUERY_KEY = ['auth', 'me'] as const

/**
 * Drops every cached query except the identity one, so nothing one account fetched is shown to the
 * next. Runs on logout, on any 401 and on every sign-in.
 * The tab's own memory (filters, scroll, back links) is handled separately, by who signed in.
 *
 * Not qc.clear(): that tears down every Query instance, including the one the mounted useMe
 * observer is attached to, so a setQueryData right after builds a fresh instance the observer was
 * never subscribed to. Data updates in the cache, but nothing re-renders and AuthGate never swaps
 * screens. Keeping ME_QUERY_KEY's instance alive lets the caller's setQueryData notify it directly.
 */
export function dropAccountData(qc: QueryClient): void {
  // The live socket is account data too: it is in the old account's hub groups and would keep
  // delivering that account's inbox and admin events to whoever signs in next on this tab.
  stopConnection()
  // The sign-in page reads SSO state from the bootstrap payload, which an admin may have changed
  // since this tab loaded it.
  invalidateInitialize()
  qc.removeQueries({
    predicate: (query) =>
      query.queryKey.length !== ME_QUERY_KEY.length ||
      !ME_QUERY_KEY.every((k, i) => query.queryKey[i] === k),
  })
}

// The server stores this as the user's zone when none is set yet, so streaks use local days
// before anybody opens Progress settings.
function browserTimeZoneHeader(): Record<string, string> {
  try {
    const zone = Intl.DateTimeFormat().resolvedOptions().timeZone
    return zone ? { 'X-Maki-TimeZone': zone } : {}
  } catch {
    return {}
  }
}

export function useMe(enabled = true) {
  return useQuery({
    queryKey: ME_QUERY_KEY,
    // Noted before the data reaches any page, so a different user's first render never reads the
    // previous one's remembered filters.
    queryFn: async () => {
      const me = await api<Me>('/auth/me', { headers: browserTimeZoneHeader() })
      noteSignedInUser(me.id)
      return me
    },
    enabled,
    // A 401 here is the normal signed-out state, not a transient failure, so retrying it just delays
    // the login screen.
    retry: false,
    staleTime: 30_000,
  })
}

export function useLogin() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (body: { username: string; password: string }) =>
      api<Me & LoginResult>('/auth/login', { method: 'POST', body: JSON.stringify(body) }),
    onSuccess: (result) => {
      // Two-factor is still pending, so there is no session yet and nothing to cache.
      if (result.requiresTwoFactor) return
      noteSignedInUser(result.id)
      dropAccountData(qc)
      qc.setQueryData(ME_QUERY_KEY, result)
    },
  })
}

export function useVerifyTwoFactor() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (body: { code: string; rememberMachine: boolean }) =>
      api<Me>('/auth/2fa', { method: 'POST', body: JSON.stringify(body) }),
    onSuccess: (me) => {
      noteSignedInUser(me.id)
      dropAccountData(qc)
      qc.setQueryData(ME_QUERY_KEY, me)
    },
  })
}

export function useSetup() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (body: { username: string; password: string; displayName?: string }) =>
      api<Me>('/auth/setup', { method: 'POST', body: JSON.stringify(body) }),
    onSuccess: (me) => {
      noteSignedInUser(me.id)
      dropAccountData(qc)
      qc.setQueryData(ME_QUERY_KEY, me)
      setSetupDone()
    },
  })
}

export function useLogout() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: () => api<void>('/auth/logout', { method: 'POST' }),
    onSuccess: () => {
      noteSignedOut()
      dropAccountData(qc)
      qc.setQueryData(ME_QUERY_KEY, null)
    },
  })
}

export function useChangePassword() {
  return useMutation({
    mutationFn: (body: { currentPassword: string; newPassword: string }) =>
      api<void>('/account/password', { method: 'POST', body: JSON.stringify(body) }),
  })
}

export function useTwoFactorStatus() {
  return useQuery({
    queryKey: ['account', '2fa'],
    queryFn: () =>
      api<{
        enabled: boolean
        hasAuthenticator: boolean
        recoveryCodesLeft: number
        available: boolean
        hasPassword: boolean
        ssoDelegated: boolean
      }>('/account/2fa'),
  })
}

export function useStartTwoFactorSetup() {
  return useMutation({
    mutationFn: () =>
      api<{ sharedKey: string; authenticatorUri: string }>('/account/2fa/setup', { method: 'POST' }),
  })
}

export function useEnableTwoFactor() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (body: { code: string; password: string }) =>
      api<{ recoveryCodes: string[] }>('/account/2fa/enable', {
        method: 'POST',
        body: JSON.stringify(body),
      }),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ['account', '2fa'] })
      void qc.invalidateQueries({ queryKey: ME_QUERY_KEY })
    },
  })
}

export function useRegenerateRecoveryCodes() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (body: { code: string; password: string }) =>
      api<{ recoveryCodes: string[] }>('/account/2fa/recovery-codes', {
        method: 'POST',
        body: JSON.stringify(body),
      }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['account', '2fa'] }),
  })
}

export function useDisableTwoFactor() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (password: string) =>
      api<void>('/account/2fa/disable', { method: 'POST', body: JSON.stringify({ password }) }),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ['account', '2fa'] })
      void qc.invalidateQueries({ queryKey: ME_QUERY_KEY })
    },
  })
}

export function useApiKeys() {
  return useQuery({
    queryKey: ['account', 'apikeys'],
    queryFn: () => api<ApiKey[]>('/account/apikeys'),
  })
}

export function useCreateApiKey() {
  const qc = useQueryClient()
  return useMutation({
    // Full keys only: the OPDS feed token is minted and rotated on the OPDS settings card.
    mutationFn: (body: { name: string; password: string }) =>
      api<CreatedApiKey>('/account/apikeys', {
        method: 'POST',
        body: JSON.stringify({ ...body, scope: 'Full' }),
      }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['account', 'apikeys'] }),
  })
}

export function useRevokeApiKey() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => api<void>(`/account/apikeys/${id}`, { method: 'DELETE' }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['account', 'apikeys'] }),
  })
}

/**
 * Confirms the password before linking single sign-on. The link itself is a browser navigation
 * that cannot carry one, so the server answers with a short-lived cookie the link then requires.
 */
export function useConfirmOidcLink() {
  return useMutation({
    mutationFn: (password: string) =>
      api<void>('/auth/oidc/link', { method: 'POST', body: JSON.stringify({ password }) }),
  })
}

export function useUnlinkOidc() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: () => api<void>('/account/oidc', { method: 'DELETE' }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ME_QUERY_KEY }),
  })
}

export function useRevokeSessions() {
  return useMutation({
    mutationFn: () => api<void>('/account/sessions/revoke-all', { method: 'POST' }),
  })
}

/**
 * Which account Kavita's reading is attributed to. Instance-wide by necessity, since Kavita is one server
 * behind one API key, so everything it reports is one person's reading and there is no way to tell
 * two Kavita users apart from this side.
 */
export function useKavitaUser() {
  return useQuery({
    queryKey: ['settings', 'kavita', 'user'],
    queryFn: () => api<{ userId: number | null }>('/settings/kavita'),
  })
}

export function useSetKavitaUser() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (userId: number | null) =>
      api<{ userId: number | null }>('/settings/kavita/user', {
        method: 'PUT',
        body: JSON.stringify({ userId }),
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['settings', 'kavita', 'user'] })
      void queryClient.invalidateQueries({ queryKey: ['settings', 'reader'] })
    },
  })
}

/**
 * The account list. Admin-only server-side, so `enabled` exists for the callers that render for
 * everybody and only need it when the viewer is an admin, without it a normal user fires a request
 * that can only ever 403.
 */
export function useUsers(enabled = true) {
  return useQuery({
    queryKey: ['users'],
    queryFn: () => api<UserSummary[]>('/users'),
    enabled,
  })
}

export interface SaveUserBody {
  username?: string
  password?: string
  displayName?: string
  permissions?: number
  maxContentRating?: string
  allRootFolders?: boolean
  rootFolderIds?: number[]
  disabled?: boolean
}

export function useCreateUser() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (body: SaveUserBody) =>
      api<UserSummary>('/users', { method: 'POST', body: JSON.stringify(body) }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['users'] }),
  })
}

export function useUpdateUser() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ id, ...body }: SaveUserBody & { id: number }) =>
      api<UserSummary>(`/users/${id}`, { method: 'PUT', body: JSON.stringify(body) }),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ['users'] })
      // The edited account may be the caller's own, and its permissions drive the whole nav.
      void qc.invalidateQueries({ queryKey: ME_QUERY_KEY })
    },
  })
}

export function useDeleteUser() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => api<void>(`/users/${id}`, { method: 'DELETE' }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['users'] }),
  })
}

export function useResetUserTwoFactor() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => api<void>(`/users/${id}/2fa/reset`, { method: 'POST' }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['users'] }),
  })
}

export function useUnlinkUserOidc() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => api<void>(`/users/${id}/oidc`, { method: 'DELETE' }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['users'] }),
  })
}

export function useAuditLog(limit = 200) {
  return useQuery({
    queryKey: ['users', 'auditlog', limit],
    queryFn: () => api<AuthEvent[]>(`/users/auditlog?limit=${limit}`),
  })
}

export interface SecuritySettings {
  requireHttps: boolean
  trustedProxies: string
  lockoutMaxAttempts: number
  lockoutMinutes: number
  sessionDays: number
}

export function useSecuritySettings() {
  return useQuery({
    queryKey: ['settings', 'security'],
    queryFn: () => api<SecuritySettings>('/settings/security'),
  })
}

export function useSaveSecuritySettings() {
  return useSaveSettingsRecord<SecuritySettings>(['settings', 'security'], '/settings/security')
}

export interface OidcSettings {
  enabled: boolean
  authority: string
  clientId: string
  clientSecret: string
  scopes: string
  displayName: string
  oidcOnly: boolean
  autoProvision: boolean
  usernameClaim: string
  adminClaim: string
  permissionClaim: string
  /** Read-only: the redirect URI to register with the provider. */
  redirectPath: string
  /** Read-only: MAKI_ALLOW_LOCAL_LOGIN is set, so `oidcOnly` is currently being ignored. */
  breakGlassActive: boolean
}

export function useOidcSettings() {
  return useQuery({
    queryKey: ['settings', 'oidc'],
    queryFn: () => api<OidcSettings>('/settings/oidc'),
  })
}

export function useSaveOidcSettings() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (body: OidcSettings) =>
      api<OidcSettings>('/settings/oidc', { method: 'PUT', body: JSON.stringify(body) }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['settings', 'oidc'] }),
  })
}
