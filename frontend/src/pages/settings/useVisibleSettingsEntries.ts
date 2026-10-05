import { useMemo } from 'react'
import { useAuth } from '../../auth/AuthProvider'
import { useReaderUsed } from '../../api/reader'
import { SETTINGS_ENTRIES, entryVisible } from './registry'

/**
 * The cards this account may see. Everything an admin-only card writes is rejected by the server for
 * anyone else, so listing one would only lead to failed requests. Kavita read sync renders nothing
 * without a Kavita connection, so it is left out until one exists.
 */
export function useVisibleSettingsEntries() {
  const { me, can } = useAuth()
  const isAdmin = me?.isAdmin ?? false
  const kavitaConfigured = useReaderUsed().data?.kavita ?? false
  return useMemo(
    () =>
      SETTINGS_ENTRIES.filter(
        (e) => entryVisible(e, isAdmin, can) && (e.id !== 'kavita-sync' || kavitaConfigured),
      ),
    [isAdmin, can, kavitaConfigured],
  )
}
