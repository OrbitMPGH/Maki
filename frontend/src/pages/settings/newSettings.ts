import { createContext, useContext, useMemo, type ReactNode } from 'react'
import { useSystemStatus, type SystemStatus } from '../../api/hooks'
import type { SettingsEntry, SettingsSince } from './registry'

function core(version: string): number[] {
  return version.split(/[-+]/)[0].split('.').map((part) => Number.parseInt(part, 10) || 0)
}

function compareCore(a: string, b: string): number {
  const [x, y] = [core(a), core(b)]
  for (let i = 0; i < Math.max(x.length, y.length); i++) {
    const d = (x[i] ?? 0) - (y[i] ?? 0)
    if (d !== 0) return d
  }
  return 0
}

/**
 * New means shipped after the version this instance upgraded from, up to the one it runs. A fresh
 * install has no previous version, so nothing is new to it. `'next'` is unreleased work, which only
 * a dev or prerelease build carries, and is always new there.
 */
export function isNewSince(since: SettingsSince | undefined, status: SystemStatus | undefined): boolean {
  if (!since || !status?.upgradedFrom) return false
  if (since === 'next') return true
  return compareCore(since, status.upgradedFrom) > 0 && compareCore(since, status.version) <= 0
}

/** Ids of the given entries that count as new on this instance. */
export function useNewSettingIds(entries: SettingsEntry[]): ReadonlySet<string> {
  const status = useSystemStatus().data
  return useMemo(
    () => new Set(entries.filter((e) => isNewSince(e.since, status)).map((e) => e.id)),
    [entries, status],
  )
}

export const NewSettingsContext = createContext<ReadonlySet<string>>(new Set())

export function useNewSettingSet(): ReadonlySet<string> {
  return useContext(NewSettingsContext)
}

export function useIsNewSetting(id: string): boolean {
  return useContext(NewSettingsContext).has(id)
}

/**
 * Set while a card is drawn on the New tab only for the new controls inside it. The card then
 * shows its title, `note` in place of its description, and the controls marked with
 * `newControlMark`; everything else is hidden.
 */
export const SettingsFocusContext = createContext<{ note?: ReactNode } | null>(null)

/**
 * Spread on the element that holds a new control (or the list a nested one is opened from), so the
 * New tab's focused card keeps it. Empty once none of the ids count as new.
 */
export function newControlMark(newIds: ReadonlySet<string>, ...ids: string[]) {
  return ids.some((id) => newIds.has(id)) ? { 'data-new-control': '' } : {}
}

export function useNewControlMark(...ids: string[]) {
  return newControlMark(useContext(NewSettingsContext), ...ids)
}
