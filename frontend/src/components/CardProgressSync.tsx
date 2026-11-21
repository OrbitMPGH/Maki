import { useEffect } from 'react'
import { useUiSettings } from '../api/hooks'
import { setCardProgress } from '../lib/cardProgress'

/** Resolves the "Series cards" setting once for the whole app and hands it to the card store. */
export function CardProgressSync() {
  const { data: ui } = useUiSettings()
  const chosen = ui?.cardProgress ?? 'auto'
  const mode = chosen === 'auto' ? (ui?.cardProgressAuto ?? 'downloads') : chosen
  useEffect(() => setCardProgress(mode), [mode])
  return null
}
