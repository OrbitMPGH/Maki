import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from './client'

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

export function useMe(enabled = true) {
  return useQuery({
    queryKey: ME_QUERY_KEY,
    queryFn: () => api<Me>('/auth/me'),
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
      if (!result.requiresTwoFactor) qc.setQueryData(ME_QUERY_KEY, result)
    },
  })
}

export function useVerifyTwoFactor() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (body: { code: string; rememberMachine: boolean }) =>
      api<Me>('/auth/2fa', { method: 'POST', body: JSON.stringify(body) }),
    onSuccess: (me) => qc.setQueryData(ME_QUERY_KEY, me),
  })
}

export function useSetup() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (body: { username: string; password: string; displayName?: string }) =>
      api<Me>('/auth/setup', { method: 'POST', body: JSON.stringify(body) }),
    onSuccess: (me) => {
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
      // Clear everything except the identity query itself: qc.clear() tears down every Query
      // instance, including the one the mounted useMe observer is attached to, so a setQueryData
      // right after builds a fresh instance the observer was never subscribed to: data updates
      // in the cache, but nothing re-renders and AuthGate never swaps to the login screen. Keeping
      // ME_QUERY_KEY's instance alive lets setData below notify that same observer directly.
      qc.removeQueries({
        predicate: (query) =>
          query.queryKey.length !== ME_QUERY_KEY.length ||
          !ME_QUERY_KEY.every((k, i) => query.queryKey[i] === k),
      })
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
      api<{ enabled: boolean; hasAuthenticator: boolean; recoveryCodesLeft: number }>('/account/2fa'),
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
    mutationFn: (code: string) =>
      api<{ recoveryCodes: string[] }>('/account/2fa/enable', {
        method: 'POST',
        body: JSON.stringify({ code }),
      }),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ['account', '2fa'] })
      void qc.invalidateQueries({ queryKey: ME_QUERY_KEY })
    },
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
    mutationFn: (body: { name: string; scope: ApiKeyScope }) =>
      api<CreatedApiKey>('/account/apikeys', { method: 'POST', body: JSON.stringify(body) }),
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

export function useUsers() {
  return useQuery({
    queryKey: ['users'],
    queryFn: () => api<UserSummary[]>('/users'),
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
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (body: SecuritySettings) =>
      api<SecuritySettings>('/settings/security', { method: 'PUT', body: JSON.stringify(body) }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ['settings', 'security'] }),
  })
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
