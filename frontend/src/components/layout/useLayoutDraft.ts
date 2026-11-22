import { useMemo, useState } from 'react'
import type { PageSection } from '../../api/hooks'
import { countChanges, moveSection, reconcileLayout, type LayoutConfig } from './pageLayout'

export interface LayoutDraft {
  /** The draft in page order, reconciled against the rails that exist right now. */
  sections: PageSection[]
  /** Where editing started, reconciled the same way. */
  baseline: PageSection[]
  changes: number
  set: (sections: PageSection[]) => void
  update: (key: string, patch: Partial<PageSection>) => void
  move: (from: number, to: number) => void
}

/**
 * The layout being edited, held by the page so it can draw its own sections in the draft's order
 * and fetch for the sections the draft shows. Captured once when editing starts: rail mutations
 * invalidate the saved layout, and re-syncing mid-edit would throw the draft away.
 *
 * @param saved The saved layout, or undefined while it (or the rails list) is still loading.
 */
export function useLayoutDraft({
  editing,
  saved,
  config,
  railIds,
}: {
  editing: boolean
  saved: PageSection[] | undefined
  config: LayoutConfig
  railIds: number[]
}): LayoutDraft | null {
  const [draft, setDraft] = useState<PageSection[] | null>(null)
  const [start, setStart] = useState<PageSection[] | null>(null)

  if (editing && draft === null && saved) {
    setDraft(saved)
    setStart(saved)
  } else if (!editing && draft !== null) {
    setDraft(null)
    setStart(null)
  }

  const sections = useMemo(() => (draft ? reconcileLayout(draft, config, railIds) : null), [draft, config, railIds])
  const baseline = useMemo(() => (start ? reconcileLayout(start, config, railIds) : null), [start, config, railIds])

  if (!editing || !sections || !baseline) return null
  return {
    sections,
    baseline,
    changes: countChanges(sections, baseline),
    set: setDraft,
    update: (key, patch) => setDraft(sections.map((s) => (s.key === key ? { ...s, ...patch } : s))),
    move: (from, to) => setDraft(moveSection(sections, from, to)),
  }
}
