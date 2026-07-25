import { useCallback, useEffect, useRef, useState } from 'react'
import { api } from '../../api/client'
import type { ReaderManifest } from '../../api/reader'

export type ReaderMode = 'paged' | 'double' | 'vertical'
export type ReaderDirection = 'ltr' | 'rtl'
export type ReaderFit = 'width' | 'height' | 'screen' | 'original'

export interface ReaderPrefs {
  mode: ReaderMode
  direction: ReaderDirection
  fit: ReaderFit
  /** Gap between pages in continuous mode, in px. */
  pageGap: number
  /** How many pages ahead to warm the browser cache with. */
  preload: number
  tapZones: boolean
  showPageNumber: boolean
  /** Split a double-width page into two in single-page mode. */
  splitWidePages: boolean
  autoNextChapter: boolean
  background: string
}

/**
 * Right-to-left by default: everything Maki packages is tagged
 * `Manga = "YesAndRightToLeft"` in its ComicInfo. Manhwa/manhua want vertical + ltr, which is
 * what the per-series override exists for. Kept in step with ReaderPrefsSpec on the server —
 * the server is authoritative, these only cover the moment before the manifest arrives.
 */
export const DEFAULT_PREFS: ReaderPrefs = {
  mode: 'paged',
  direction: 'rtl',
  fit: 'height',
  pageGap: 0,
  preload: 3,
  tapZones: true,
  showPageNumber: true,
  splitWidePages: false,
  autoNextChapter: true,
  background: '#0a0a0b',
}

export type PrefsScope = 'global' | 'series'

const SAVE_DEBOUNCE_MS = 700

/**
 * Reader preferences, persisted on the server. Edits save to whichever scope is active: the
 * series' own override if it has one, otherwise the global defaults. That way toggling vertical
 * for a manhwa sticks to that manhwa without quietly re-styling the whole library.
 */
export function useReaderPrefs(manifest: ReaderManifest | undefined) {
  const [prefs, setPrefs] = useState<ReaderPrefs>(DEFAULT_PREFS)
  const [scope, setScopeState] = useState<PrefsScope>('global')
  const seriesId = manifest?.seriesId
  // Adopt the server's copy once per series; re-adopting on every manifest (i.e. every chapter
  // turn) would throw away an unsaved in-session change.
  const adoptedFor = useRef<number | null>(null)

  useEffect(() => {
    if (!manifest || adoptedFor.current === manifest.seriesId) return
    adoptedFor.current = manifest.seriesId
    setPrefs({ ...DEFAULT_PREFS, ...manifest.prefs })
    setScopeState(manifest.prefsOverridden ? 'series' : 'global')
  }, [manifest])

  const save = useCallback(
    (next: ReaderPrefs, target: PrefsScope) => {
      const request =
        target === 'series' && seriesId
          ? api(`/reader/series/${seriesId}/prefs`, {
              method: 'PUT',
              body: JSON.stringify({ prefs: next }),
            })
          : api('/settings/reader', {
              method: 'PUT',
              // The reader never edits the Kavita push-back flag; read it back and resend it so
              // saving a display preference can't silently turn it off.
              body: JSON.stringify({ defaults: next, pushToKavita: pushToKavita.current }),
            })
      void request.catch(() => {})
    },
    [seriesId],
  )

  // Carried through so a prefs write doesn't clobber the push-back setting.
  const pushToKavita = useRef(false)
  useEffect(() => {
    void api<{ pushToKavita: boolean }>('/settings/reader')
      .then((s) => {
        pushToKavita.current = s.pushToKavita
      })
      .catch(() => {})
  }, [])

  const timer = useRef<ReturnType<typeof setTimeout> | null>(null)
  const update = useCallback(
    (patch: Partial<ReaderPrefs>) => {
      setPrefs((current) => {
        const next = { ...current, ...patch }
        if (timer.current) clearTimeout(timer.current)
        timer.current = setTimeout(() => save(next, scope), SAVE_DEBOUNCE_MS)
        return next
      })
    },
    [save, scope],
  )

  /** Switches which scope future edits (and the current state) are stored in. */
  const setScope = useCallback(
    (next: PrefsScope) => {
      setScopeState(next)
      if (!seriesId) return
      if (next === 'series') {
        save(prefs, 'series')
      } else {
        // Clearing the override falls back to the global defaults, so persist the current look
        // globally too — otherwise the reader would visibly jump on the next chapter.
        void api(`/reader/series/${seriesId}/prefs`, {
          method: 'PUT',
          body: JSON.stringify({ prefs: null }),
        }).catch(() => {})
        save(prefs, 'global')
      }
    },
    [prefs, save, seriesId],
  )

  useEffect(() => () => {
    if (timer.current) clearTimeout(timer.current)
  }, [])

  return { prefs, update, scope, setScope }
}
