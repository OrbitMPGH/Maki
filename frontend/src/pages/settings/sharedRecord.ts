import { useEffect, useRef } from 'react'
import { notifications } from '@mantine/notifications'
import { t as now } from '@lingui/core/macro'

/**
 * Several cards edit slices of one settings record that saves with one PUT (`useSaveSettingsRecord`).
 * A card reloads its fields only when its own slice changes on the server, so another card saving
 * the same record never wipes edits here. `discarded` forces a reload for Discard.
 */
export function useSliceSync<S>(slice: S | undefined, load: (slice: S) => void, discarded = 0) {
  const key = slice === undefined ? undefined : JSON.stringify(slice)
  const loadRef = useRef(load)
  useEffect(() => {
    loadRef.current = load
  })
  useEffect(() => {
    if (key !== undefined) loadRef.current(JSON.parse(key) as S)
  }, [key, discarded])
}

export const savedToast = () => notifications.show({ message: now`Saved`, color: 'var(--ok)' })
