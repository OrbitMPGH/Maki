import { useCallback, useEffect, useRef, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { api } from '../../api/client'
import { readerSettingsQuery } from '../../api/reader'
import type { PrefsSource, ReaderManifest, ResolvedReaderPrefs } from '../../api/reader'
import { useReadingProfiles, type ReadingProfile } from '../../api/readingProfiles'

export type ReaderMode = 'paged' | 'double' | 'vertical'
export type ReaderDirection = 'ltr' | 'rtl'
export type ReaderFit = 'width' | 'height' | 'screen' | 'original'
export type ReaderFilter = 'none' | 'grayscale' | 'sepia' | 'invert'

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
  autoNextChapter: boolean
  /** Flash the chapter name over the page on entering it. */
  chapterBanner: boolean
  background: string
  /** Percent scale on top of the '1:1' fit; meaningless for the other fits, which already size to the viewport. */
  scale: number
  /** Percent the page images are dimmed by; 0 leaves them untouched. */
  dim: number
  filter: ReaderFilter
  /** Hold a screen wake lock while reading, where the browser offers one. */
  keepAwake: boolean
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
  autoNextChapter: true,
  chapterBanner: true,
  background: BACKGROUNDS.dark,
  scale: 100,
  dim: 0,
  filter: 'none',
  keepAwake: true,
}

/** The CSS filter for the page images, or undefined when the prefs ask for none. */
export function toneFilter(prefs: Pick<ReaderPrefs, 'dim' | 'filter'>): string | undefined {
  const parts: string[] = []
  if (prefs.dim > 0) parts.push(`brightness(${(100 - prefs.dim) / 100})`)
  if (prefs.filter === 'grayscale') parts.push('grayscale(1)')
  else if (prefs.filter === 'sepia') parts.push('sepia(1)')
  else if (prefs.filter === 'invert') parts.push('invert(1) hue-rotate(180deg)')
  return parts.length > 0 ? parts.join(' ') : undefined
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
export function useReaderPrefs(manifest: ReaderManifest | undefined, settled = true) {
  const [prefs, setPrefs] = useState<ReaderPrefs>(DEFAULT_PREFS)
  const [resolved, setResolved] = useState<Resolution>({
    source: 'Global',
    profileId: null,
    pinnedProfileId: null,
    autoProfileId: null,
  })
  const seriesId = manifest?.seriesId
  const { data: profiles } = useReadingProfiles()
  const queryClient = useQueryClient()

  // Adopt the server's copy once per series; re-adopting on every manifest (i.e. every chapter
  // turn) would throw away an unsaved in-session change. Waits for the manifest fetch to settle: a
  // reopen is served the cached manifest first, and its prefs predate whatever was saved since.
  const adoptedFor = useRef<number | null>(null)

  useEffect(() => {
    if (!manifest || !settled || adoptedFor.current === manifest.seriesId) return
    adoptedFor.current = manifest.seriesId
    setPrefs({ ...DEFAULT_PREFS, ...manifest.prefs })
    setResolved({
      source: manifest.prefsSource,
      profileId: manifest.profileId,
      pinnedProfileId: manifest.pinnedProfileId,
      autoProfileId: manifest.autoProfileId,
    })
  }, [manifest, settled])

  // Read through a ref so the debounced save always writes to the destination in force at the time
  // it fires, not the one captured when the first keystroke of a burst landed.
  const target = useRef<{ resolved: Resolution; profiles: ReadingProfile[] | undefined }>({
    resolved,
    profiles,
  })
  target.current = { resolved, profiles }
  // An edit aimed at a profile whose list has not loaded yet, written once it has.
  const deferred = useRef<ReaderPrefs | null>(null)

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

      // A profile write is a full replace, so its name and type claims have to be resent. Without
      // the list the edit waits for it: writing the global defaults instead would overwrite them
      // with this profile's values.
      if (current.source === 'Profile') {
        if (!known) {
          deferred.current = next
          return
        }
        const profile = known.find((p) => p.id === current.profileId)
        if (!profile) return
        void api(`/readingprofiles/${profile.id}`, {
          method: 'PUT',
          body: JSON.stringify({ name: profile.name, prefs: next, seriesTypes: profile.seriesTypes }),
        })
          .then(() => queryClient.invalidateQueries({ queryKey: ['reading-profiles'] }))
          .catch(() => {})
        return
      }

      // The global write carries the push-back setting, which lives on the same endpoint but is
      // never edited here. It is read fresh at write time (silently: the reader has no place for a
      // toast), falls back to the last copy the app saw, and drops the save when there is none
      // rather than guessing a value that would switch push-back off.
      void queryClient
        .fetchQuery({ ...readerSettingsQuery, staleTime: 0, meta: { silent: true } })
        .catch(() => queryClient.getQueryData(readerSettingsQuery.queryKey))
        .then((settings) => {
          if (!settings) return
          return api('/settings/reader', {
            method: 'PUT',
            body: JSON.stringify({ defaults: next, pushToKavita: settings.pushToKavita }),
          }).then(() => queryClient.invalidateQueries({ queryKey: ['settings', 'reader'] }))
        })
        .catch(() => {})
    },
    [seriesId, queryClient],
  )

  useEffect(() => {
    if (!profiles || !deferred.current) return
    const next = deferred.current
    deferred.current = null
    save(next)
  }, [profiles, save])

  const timer = useRef<ReturnType<typeof setTimeout> | null>(null)
  const prefsRef = useRef(prefs)
  prefsRef.current = prefs
  const update = useCallback(
    (patch: Partial<ReaderPrefs>) => {
      // Before the server's copy is adopted `current` is DEFAULT_PREFS, and saving that plus one
      // change would overwrite the user's real settings.
      if (adoptedFor.current === null) return
      const next = { ...prefsRef.current, ...patch }
      prefsRef.current = next
      setPrefs(next)
      if (timer.current) clearTimeout(timer.current)
      timer.current = setTimeout(() => save(next), SAVE_DEBOUNCE_MS)
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
      if (!seriesId || adoptedFor.current === null) return

      // Any queued knob edit belongs to the destination being left behind. Flushing it would write
      // it somewhere new; dropping it is what the user asked for by switching.
      if (timer.current) clearTimeout(timer.current)
      deferred.current = null

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
