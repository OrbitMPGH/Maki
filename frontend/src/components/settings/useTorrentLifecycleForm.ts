import { useReducer, useState } from 'react'
import { useSliceSync } from '../../pages/settings/sharedRecord'
import type { SaveGroup } from '../ConnectionSettingsCard'
import { useSaveTorrentLifecycle, useTorrentLifecycle } from '../../api/torrentLifecycle'

/** A number field as typed: a number, or text that is empty or not one yet ("1."). */
export type NumberDraft = number | string

export interface TorrentLifecycleForm {
  stallHours: number
  setStallHours: (hours: number) => void
  seedRatio: NumberDraft
  setSeedRatio: (ratio: NumberDraft) => void
  seedMinutes: NumberDraft
  setSeedMinutes: (minutes: NumberDraft) => void
  loaded: boolean
  /** Saves with the download client card's own Save button. */
  group: SaveGroup
}

/** Anything that is not a number yet means no target. */
export const targetOf = (draft: NumberDraft) => (typeof draft === 'number' ? draft : null)

/** The stall limit and seed targets as edited in the download client card, saved through their own endpoint. */
export function useTorrentLifecycleForm(): TorrentLifecycleForm {
  const { data } = useTorrentLifecycle()
  const save = useSaveTorrentLifecycle()
  const [stallHours, setStallHours] = useState(1)
  const [seedRatio, setSeedRatio] = useState<NumberDraft>('')
  const [seedMinutes, setSeedMinutes] = useState<NumberDraft>('')
  const [discarded, discard] = useReducer((n: number) => n + 1, 0)

  useSliceSync(
    data,
    (slice) => {
      setStallHours(slice.stallHours)
      setSeedRatio(slice.seedRatio ?? '')
      setSeedMinutes(slice.seedMinutes ?? '')
    },
    discarded,
  )

  const dirty =
    data !== undefined &&
    (stallHours !== data.stallHours ||
      targetOf(seedRatio) !== data.seedRatio ||
      targetOf(seedMinutes) !== data.seedMinutes)

  return {
    stallHours,
    setStallHours,
    seedRatio,
    setSeedRatio,
    seedMinutes,
    setSeedMinutes,
    loaded: data !== undefined,
    group: {
      dirty,
      saving: save.isPending,
      commit: () =>
        save.mutateAsync({ stallHours, seedRatio: targetOf(seedRatio), seedMinutes: targetOf(seedMinutes) }),
      reset: discard,
    },
  }
}
