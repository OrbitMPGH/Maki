import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { getInitialize, setUnauthorizedHandler } from '../api/client'
import { ME_QUERY_KEY, setSetupDoneHandler, useMe, type Me, type Permission } from '../api/auth'

interface AuthState {
  me: Me | null
  loading: boolean
  /** True while the placeholder account from the multi-user migration is unclaimed. */
  setupNeeded: boolean
  /** Admin satisfies every permission, since the server already expands it into `permissionNames`. */
  can: (permission: Permission) => boolean
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
  const { data: me, isLoading, isFetched } = useMe(setupNeeded === false)

  useEffect(() => {
    // Any 401 from anywhere drops the cached identity, which re-renders the guard below into the
    // login screen. Registered here so the ~150 query hooks need no 401 handling of their own.
    setUnauthorizedHandler(() => qc.setQueryData(ME_QUERY_KEY, null))
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
    }),
    [me, isLoading, isFetched, setupNeeded],
  )

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

export function useAuth(): AuthState {
  const context = useContext(AuthContext)
  if (!context) throw new Error('useAuth must be used inside AuthProvider')
  return context
}

/**
 * Hides children the user has no permission for.
 *
 * Cosmetic only: the server enforces every permission independently. This exists so the UI does not
 * offer buttons that answer 403, not as a security boundary.
 */
export function PermissionGate({
  permission,
  children,
  fallback = null,
}: {
  permission: Permission
  children: ReactNode
  fallback?: ReactNode
}) {
  const { can } = useAuth()
  return <>{can(permission) ? children : fallback}</>
}
