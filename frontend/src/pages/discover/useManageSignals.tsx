import { useState } from 'react'
import { ManageSignalsModal } from './ManageSignalsModal'

/**
 * The Manage signals modal and a way to open it. The modal fetches its own data on mount, so it is
 * only mounted once someone has opened it.
 */
export function useManageSignals() {
  const [manage, setManage] = useState<'titles' | 'anime' | null>(null)
  const [manageOpened, setManageOpened] = useState(false)
  if (manage !== null && !manageOpened) setManageOpened(true)
  const modal = manageOpened ? (
    <ManageSignalsModal opened={manage !== null} initialTab={manage ?? 'titles'} onClose={() => setManage(null)} />
  ) : null
  return { open: setManage, modal }
}
