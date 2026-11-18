import { useEffect, useRef, type ReactNode } from 'react'
import { Stack } from '@mantine/core'
import { DrillPanes } from '../ui/DrillPanes'

/** Sent on `document` with a setting id as its detail just before Settings scrolls to that setting. */
export const SETTINGS_TARGET_EVENT = 'maki:settings-target'

/**
 * Drill rows inside a Settings card: rows that show a setting's current value on the first page,
 * each opening a page of fields that slides in. The card's own Save covers every page.
 */
export function SettingsDrill({
  page,
  depth,
  targets = [],
  onTarget,
  children,
}: {
  page: string
  depth: number
  /** Setting ids anchored on this drill's first page. A deep link to one goes back there first. */
  targets?: string[]
  onTarget?: () => void
  children: ReactNode
}) {
  const latest = useRef({ targets, onTarget })
  latest.current = { targets, onTarget }
  useEffect(() => {
    const listener = (event: Event) => {
      const id = (event as CustomEvent<string>).detail
      if (latest.current.targets.includes(id)) latest.current.onTarget?.()
    }
    document.addEventListener(SETTINGS_TARGET_EVENT, listener)
    return () => document.removeEventListener(SETTINGS_TARGET_EVENT, listener)
  }, [])

  return (
    <div className="settings-drill">
      <DrillPanes page={page} depth={depth} bleed={6}>
        {children}
      </DrillPanes>
    </div>
  )
}

/** The fields on one drill page, inset to line up with the card's other controls. */
export function SettingsDrillPage({ children, ...attrs }: { children: ReactNode; 'data-new-control'?: string }) {
  return (
    <Stack gap="md" className="settings-drill-page" {...attrs}>
      {children}
    </Stack>
  )
}
