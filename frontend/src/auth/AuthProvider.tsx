import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { getInitialize, isOfflineUnavailable, setUnauthorizedHandler } from '../api/client'
import { dropAccountData, ME_QUERY_KEY, setSetupDoneHandler, useMe, type Me, type Permission } from '../api/auth'
import { clearQueueUser } from '../pwa/progressQueue'

interface AuthState {
  me: Me | null
  loading: boolean
  /** True while the placeholder account from the multi-user migration is unclaimed. */
  setupNeeded: boolean
  /** Admin satisfies every permission, since the server already expands it into `permissionNames`. */
  can: (permission: Permission) => boolean
  /** Signed-in state unknown: the server is unreachable and this device holds no copy of it. */
  offline: boolean
  /** Asks the server who is signed in again, for the offline screen. */
  retry: () => void
}

const AuthContext = createContext<AuthState | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const qc = useQueryClient()
  const [setupNeeded, setSetupNeeded] = useState<boolean | null>(null)

  useEffect(() => {
    let cancelled = false
    void getInitialize()
      .then((init) => {
        if (!cancelled) setSetupNeeded(init.setupNeeded)
      })
      // If the bootstrap endpoint is unreachable there is nothing useful to show; treating it as
      // "setup done" lets the normal signed-out path render its own error.
      .catch(() => {
        if (!cancelled) setSetupNeeded(false)
      })
    return () => {
      cancelled = true
    }
  }, [])

  // Don't ask who the user is until we know an account even exists; during first-run setup /auth/me
  // is guaranteed to 401 and would only produce a spurious redirect.
  // Only a 401 signs anybody out (the unauthorized handler below). A server that cannot be reached
  // leaves whatever identity is cached, and with none cached the gate shows the offline screen
  // rather than a login form that could only fail.
  const { data: me, isLoading, isFetched, error, refetch } = useMe(setupNeeded === false)
  const offline = !me && isOfflineUnavailable(error)
  const isAdmin = me?.permissionNames.includes('Admin') ?? false
  useEffect(() => {
    qc.removeQueries({ queryKey: ['health'] })
    qc.removeQueries({ queryKey: ['health-workspace'] })
  }, [qc, me?.id, isAdmin])

  useEffect(() => {
    // Any 401 from anywhere drops the cached identity, which re-renders the guard below into the
    // login screen. Registered here so the ~150 query hooks need no 401 handling of their own.
    // An expired session takes the rest of the cache with it, or the next account to sign in on
    // this tab would see the previous one's data until each query refetched.
    setUnauthorizedHandler(() => {
      clearQueueUser()
      dropAccountData(qc)
      qc.setQueryData(ME_QUERY_KEY, null)
    })
    return () => setUnauthorizedHandler(null)
  }, [qc])

  useEffect(() => {
    setSetupDoneHandler(() => setSetupNeeded(false))
    return () => setSetupDoneHandler(null)
  }, [])

  const value = useMemo<AuthState>(
    () => ({
      me: me ?? null,
      loading: setupNeeded === null || (setupNeeded === false && isLoading && !isFetched),
      setupNeeded: setupNeeded === true,
      can: (permission) => me?.permissionNames.includes(permission) ?? false,
      offline,
      retry: () => void refetch(),
    }),
    [me, isLoading, isFetched, setupNeeded, offline, refetch],
  )

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

export function useAuth(): AuthState {
  const context = useContext(AuthContext)
  if (!context) throw new Error('useAuth must be used inside AuthProvider')
  return context
}
