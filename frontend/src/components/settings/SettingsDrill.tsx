import type { ReactNode } from 'react'
import { Stack } from '@mantine/core'
import { DrillPanes } from '../ui/DrillPanes'

/**
 * Drill rows inside a Settings card: rows that show a setting's current value on the first page,
 * each opening a page of fields that slides in. The card's own Save covers every page.
 */
export function SettingsDrill({ page, depth, children }: { page: string; depth: number; children: ReactNode }) {
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
