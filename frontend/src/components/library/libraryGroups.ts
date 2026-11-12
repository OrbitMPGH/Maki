import { useCallback, useState } from 'react'
import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { IconFolder, IconTag, type Icon } from '../../lib/icons'
import type { RootFolder, SeriesDto, TagDto } from '../../api/types'
import { seriesStatusVisual } from '../ui/status'
import { TYPE_LABELS, TYPE_OPTIONS } from '../CatalogueFilters'

export const GROUP_BY_VALUES = ['none', 'status', 'tag', 'rootFolder', 'type'] as const
export type GroupBy = (typeof GROUP_BY_VALUES)[number]

export const GROUP_BY_LABELS: Record<GroupBy, MessageDescriptor> = {
  none: msg`No grouping`,
  status: msg`Status`,
  tag: msg`Tag`,
  rootFolder: msg`Root folder`,
  type: msg`Type`,
}

/** Lifecycle order: still running first, then paused, then ended. Also the Status sort's order. */
export const STATUS_ORDER = ['Ongoing', 'Hiatus', 'Completed', 'Cancelled', 'Unknown']

export interface LibraryGroup {
  /** Stable within one grouping, so a collapsed group stays collapsed across visits. */
  key: string
  /** A descriptor for words the app chose, a plain string for data (a tag or a folder path). */
  label: string | MessageDescriptor
  /** CSS colour for the group's mark. */
  color: string
  Icon?: Icon
  items: SeriesDto[]
}

const NONE_KEY = '-'

/**
 * Splits an already sorted list into groups, keeping the sort inside each. Empty groups are left
 * out. A series with several tags is listed under each of them, and series with none (or with no
 * type, or in a folder that no longer exists) come last.
 */
export function groupSeries(
  list: readonly SeriesDto[],
  groupBy: GroupBy,
  context: { tags: readonly TagDto[]; rootFolders: readonly RootFolder[] },
): LibraryGroup[] | null {
  if (groupBy === 'none') return null
  const buckets = new Map<string, SeriesDto[]>()
  const add = (key: string, s: SeriesDto) => {
    const bucket = buckets.get(key)
    if (bucket) bucket.push(s)
    else buckets.set(key, [s])
  }
  for (const s of list) {
    if (groupBy === 'status') add(s.status, s)
    else if (groupBy === 'type') add(s.type ?? NONE_KEY, s)
    else if (groupBy === 'rootFolder') add(String(s.rootFolderId), s)
    else {
      const ids = s.tagIds ?? []
      if (ids.length === 0) add(NONE_KEY, s)
      else for (const id of new Set(ids)) add(String(id), s)
    }
  }

  const groups: LibraryGroup[] = []
  const take = (key: string, make: (items: SeriesDto[]) => Omit<LibraryGroup, 'key' | 'items'>) => {
    const items = buckets.get(key)
    if (!items) return
    buckets.delete(key)
    groups.push({ key, items, ...make(items) })
  }

  if (groupBy === 'status') {
    const statusGroup = (status: string) => () => {
      const visual = seriesStatusVisual(status)
      return { label: visual.label, color: visual.color, Icon: visual.Icon }
    }
    for (const status of STATUS_ORDER) take(status, statusGroup(status))
    for (const status of [...buckets.keys()].sort()) take(status, statusGroup(status))
  } else if (groupBy === 'type') {
    for (const type of TYPE_OPTIONS) {
      take(type, () => ({ label: TYPE_LABELS[type] ?? type, color: 'var(--brand)' }))
    }
    for (const type of [...buckets.keys()].filter((k) => k !== NONE_KEY).sort()) {
      take(type, () => ({ label: TYPE_LABELS[type] ?? type, color: 'var(--brand)' }))
    }
    take(NONE_KEY, () => ({ label: msg`No type`, color: 'var(--ink-4)' }))
  } else if (groupBy === 'rootFolder') {
    for (const folder of context.rootFolders) {
      take(String(folder.id), () => ({ label: folder.path, color: 'var(--brand)', Icon: IconFolder }))
    }
    for (const key of [...buckets.keys()].sort()) {
      take(key, () => ({ label: `#${key}`, color: 'var(--ink-4)', Icon: IconFolder }))
    }
  } else {
    const byLabel = [...context.tags].sort((a, b) => a.label.localeCompare(b.label))
    for (const tag of byLabel) {
      take(String(tag.id), () => ({ label: tag.label, color: `var(--mantine-color-${tag.color}-6)` }))
    }
    // A tag deleted since the list loaded still has rows pointing at it until the next refetch.
    for (const key of [...buckets.keys()].filter((k) => k !== NONE_KEY)) buckets.delete(key)
    take(NONE_KEY, () => ({ label: msg`No tag`, color: 'var(--ink-4)', Icon: IconTag }))
  }
  return groups
}

const COLLAPSED_KEY = 'library-collapsed-groups'

function readCollapsed(): Set<string> {
  try {
    const parsed: unknown = JSON.parse(localStorage.getItem(COLLAPSED_KEY) ?? '[]')
    return new Set(Array.isArray(parsed) ? parsed.filter((v): v is string => typeof v === 'string') : [])
  } catch {
    return new Set()
  }
}

/** Which groups are folded away, per browser, keyed by grouping and group. */
export function useCollapsedGroups(groupBy: GroupBy) {
  const [collapsed, setCollapsed] = useState(readCollapsed)
  const isCollapsed = useCallback((key: string) => collapsed.has(`${groupBy}:${key}`), [collapsed, groupBy])
  const toggle = useCallback(
    (key: string) =>
      setCollapsed((prev) => {
        const id = `${groupBy}:${key}`
        const next = new Set(prev)
        if (!next.delete(id)) next.add(id)
        try {
          localStorage.setItem(COLLAPSED_KEY, JSON.stringify([...next]))
        } catch {
          /* a lost preference is not an error */
        }
        return next
      }),
    [groupBy],
  )
  return { isCollapsed, toggle }
}
