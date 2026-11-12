import { type CSSProperties, type ReactNode } from 'react'
import { plural } from '@lingui/core/macro'
import { IconChevronDown } from '../../lib/icons'
import { useLabel } from '../../i18n-context'
import { formatNumber } from '../../format'
import { useWindowedRows } from '../ui/useWindowedRows'
import type { LibraryGroup } from './libraryGroups'

/**
 * A group's full-width header: its mark, name and count. The whole row is the button that folds
 * the group away or opens it again.
 */
export function LibraryGroupHeader({
  group,
  collapsed,
  onToggle,
  asRow,
}: {
  group: LibraryGroup
  collapsed: boolean
  onToggle: () => void
  /** Inside the table, the header is a row of its own. */
  asRow?: boolean
}) {
  const renderLabel = useLabel()
  const count = group.items.length
  const shown = formatNumber(count)
  const button = (
    <button
      type="button"
      className="library-group-header"
      aria-expanded={!collapsed}
      onClick={onToggle}
      style={{ '--group-color': group.color } as CSSProperties}
    >
      <IconChevronDown size={16} className="library-group-chevron" data-collapsed={collapsed || undefined} aria-hidden />
      {group.Icon ? (
        <group.Icon size={16} className="library-group-icon" aria-hidden />
      ) : (
        <span className="library-group-dot" aria-hidden />
      )}
      <span className="library-group-name">{renderLabel(group.label)}</span>
      <span className="library-group-count tnum" aria-label={plural(count, { one: '# series', other: '# series' })}>
        {shown}
      </span>
    </button>
  )
  return asRow ? (
    <div role="row" className="library-group library-group-row">
      <div role="cell">{button}</div>
    </div>
  ) : (
    <div className="library-group">{button}</div>
  )
}

/**
 * One run of items rendered through the windowing hook. Each group gets its own, so a collapsed
 * group costs nothing and a long one only mounts what is on screen.
 */
export function WindowedItems<T>({
  items,
  enabled,
  children,
}: {
  items: readonly T[]
  enabled: boolean
  children: (slice: T[], innerRef: (el: HTMLDivElement | null) => void) => ReactNode
}) {
  const windowed = useWindowedRows(items.length, enabled)
  return (
    <div ref={windowed.outerRef} style={{ paddingTop: windowed.padTop, paddingBottom: windowed.padBottom }}>
      {children(items.slice(windowed.start, windowed.end), windowed.innerRef)}
    </div>
  )
}
