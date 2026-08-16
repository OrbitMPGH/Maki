import { useCallback, useEffect, useRef, useState } from 'react'
import { api } from '../../api/client'
import type { PrefsSource, ReaderManifest, ResolvedReaderPrefs } from '../../api/reader'
import { useReadingProfiles, type ReadingProfile } from '../../api/readingProfiles'

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
  /** Flash the chapter name over the page on entering it. */
  chapterBanner: boolean
  background: string
  /** Percent scale on top of the '1:1' fit; meaningless for the other fits, which already size to the viewport. */
  scale: number
}

/** The two page backgrounds. OLED is true black so the panel edge disappears on an OLED panel. */
export const BACKGROUNDS = {
  dark: '#0a0a0b',
  oled: '#000000',
} as const

/**
 * The fallback under everything, used only for the moment before the manifest arrives and as the
 * base for a merge. What a series actually opens with is resolved on the server: its own override,
 * then a pinned reading profile, then the profile claiming its type, then these.
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
  chapterBanner: true,
  background: BACKGROUNDS.dark,
  scale: 100,
}

/**
 * What the reader's picker is set to. `'auto'` means nothing series-specific: the series' type
 * chooses a profile, or the global defaults apply. A number is a profile pinned to this series by
 * hand, and `'series'` is an ad-hoc override belonging to this series alone.
 */
export type PrefsSelection = 'auto' | 'series' | number

const SAVE_DEBOUNCE_MS = 700

/** The picker value implied by a resolution: a pin beats auto, an override beats both. */
function selectionOf(resolved: Resolution): PrefsSelection {
  if (resolved.source === 'Series') return 'series'
  return resolved.pinnedProfileId ?? 'auto'
}

interface Resolution {
  source: PrefsSource
  profileId: number | null
  pinnedProfileId: number | null
  autoProfileId: number | null
}

/**
 * Reader preferences, persisted on the server. Edits go to whatever is currently in force, which is
 * the whole point of profiles: tuning the reader while a manhwa is open retunes the Webtoon profile
 * and therefore every manhwa, instead of leaving a per-series override behind on each one.
 *
 * The three destinations, in the order the server resolves them:
 * - an ad-hoc override on this series (`PUT reader/series/{id}/prefs`)
 * - the reading profile in force, pinned or auto-selected (`PUT readingprofiles/{id}`)
 * - the user's global defaults (`PUT settings/reader`)
 */
export function useReaderPrefs(manifest: ReaderManifest | undefined) {
  const [prefs, setPrefs] = useState<ReaderPrefs>(DEFAULT_PREFS)
  const [resolved, setResolved] = useState<Resolution>({
    source: 'Global',
    profileId: null,
    pinnedProfileId: null,
    autoProfileId: null,
  })
  const seriesId = manifest?.seriesId
  const { data: profiles } = useReadingProfiles()

  // Adopt the server's copy once per series; re-adopting on every manifest (i.e. every chapter
  // turn) would throw away an unsaved in-session change.
  const adoptedFor = useRef<number | null>(null)

  useEffect(() => {
    if (!manifest || adoptedFor.current === manifest.seriesId) return
    adoptedFor.current = manifest.seriesId
    setPrefs({ ...DEFAULT_PREFS, ...manifest.prefs })
    setResolved({
      source: manifest.prefsSource,
      profileId: manifest.profileId,
      pinnedProfileId: manifest.pinnedProfileId,
      autoProfileId: manifest.autoProfileId,
    })
  }, [manifest])

  // Carried through so a prefs write doesn't clobber the push-back setting, which lives on the same
  // endpoint but is never edited from the reader.
  const pushToKavita = useRef(false)
  useEffect(() => {
    void api<{ pushToKavita: boolean }>('/settings/reader')
      .then((s) => {
        pushToKavita.current = s.pushToKavita
      })
      .catch(() => {})
  }, [])

  // Read through a ref so the debounced save always writes to the destination in force at the time
  // it fires, not the one captured when the first keystroke of a burst landed.
  const target = useRef<{ resolved: Resolution; profiles: ReadingProfile[] }>({
    resolved,
    profiles: [],
  })
  target.current = { resolved, profiles: profiles ?? [] }

  const save = useCallback(
    (next: ReaderPrefs) => {
      const { resolved: current, profiles: known } = target.current

      if (current.source === 'Series' && seriesId) {
        void api(`/reader/series/${seriesId}/prefs`, {
          method: 'PUT',
          body: JSON.stringify({ prefs: next }),
        }).catch(() => {})
        return
      }

      // A profile write is a full replace, so its name and type claims have to be resent. If the
      // list hasn't loaded the edit would blank both, so fall through to the global defaults
      // rather than corrupting a profile.
      const profile = known.find((p) => p.id === current.profileId)
      if (current.source === 'Profile' && profile) {
        void api(`/readingprofiles/${profile.id}`, {
          method: 'PUT',
          body: JSON.stringify({ name: profile.name, prefs: next, seriesTypes: profile.seriesTypes }),
        }).catch(() => {})
        return
      }

      void api('/settings/reader', {
        method: 'PUT',
        body: JSON.stringify({ defaults: next, pushToKavita: pushToKavita.current }),
      }).catch(() => {})
    },
    [seriesId],
  )

  const timer = useRef<ReturnType<typeof setTimeout> | null>(null)
  const update = useCallback(
    (patch: Partial<ReaderPrefs>) => {
      setPrefs((current) => {
        const next = { ...current, ...patch }
        if (timer.current) clearTimeout(timer.current)
        timer.current = setTimeout(() => save(next), SAVE_DEBOUNCE_MS)
        return next
      })
    },
    [save],
  )

  /**
   * Repoints the series at a different source of settings. The server re-resolves and hands back
   * the answer, so the reader shows what it will actually open with next time rather than the
   * client guessing.
   */
  const setSelection = useCallback(
    (next: PrefsSelection) => {
      if (!seriesId) return

      // Any queued knob edit belongs to the destination being left behind. Flushing it would write
      // it somewhere new; dropping it is what the user asked for by switching.
      if (timer.current) clearTimeout(timer.current)

      const request =
        next === 'series'
          ? api<ResolvedReaderPrefs>(`/reader/series/${seriesId}/prefs`, {
              method: 'PUT',
              // Carry the current look across, so switching to a per-series override starts from
              // what is on screen instead of snapping to defaults.
              body: JSON.stringify({ prefs }),
            })
          : api<ResolvedReaderPrefs>(`/reader/series/${seriesId}/profile`, {
              method: 'PUT',
              body: JSON.stringify({ profileId: next === 'auto' ? null : next }),
            })

      void request
        .then((answer) => {
          setPrefs({ ...DEFAULT_PREFS, ...answer.prefs })
          setResolved(answer)
        })
        .catch(() => {})
    },
    [prefs, seriesId],
  )

  return {
    prefs,
    update,
    selection: selectionOf(resolved),
    setSelection,
    /** Which of the three destinations an edit lands in, for the "applies to" hint. */
    source: resolved.source,
    /** The profile the series' type picks, so "Auto" can say which one that is. */
    autoProfileId: resolved.autoProfileId,
    profiles: profiles ?? [],
  }
}
