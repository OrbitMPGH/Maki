import type { MessageDescriptor } from '@lingui/core'
import type { Icon } from '@tabler/icons-react'
import { isRailKey, railIdOf, type PageSection, type RailKey } from '../../api/hooks'

/** What the layout editor knows about one built-in section. */
export interface SectionDef {
  icon: Icon
  label: MessageDescriptor
  description?: MessageDescriptor
  /** Offers the "large tiles" switch. */
  hero?: boolean
  /** The section's panels, each with its own switch. */
  panels?: readonly { key: string; label: MessageDescriptor }[]
}

export type SectionRegistry = Record<string, SectionDef>

/** One page's layout rules. Mirrors `PageLayoutDefinition` on the server. */
export interface LayoutConfig {
  canonical: readonly string[]
  heroDefaults: Record<string, boolean>
  panels: Record<string, readonly string[]>
  /** Where a rail the layout has not seen goes: after the last rail, or before this key. Null = at the end. */
  railAnchor: string | null
}

function railInsertAt(sections: PageSection[], anchor: string | null): number {
  if (anchor == null) return sections.length
  let lastRail = -1
  sections.forEach((s, i) => {
    if (isRailKey(s.key)) lastRail = i
  })
  if (lastRail >= 0) return lastRail + 1
  const at = sections.findIndex((s) => s.key === anchor)
  return at >= 0 ? at : sections.length
}

/**
 * The client's copy of the server's `PageLayouts.Merge`, minus the stored-blob upgrades the server
 * has already applied. Used so a layout being edited always shows exactly the sections and rails
 * that exist right now, including a rail created a moment ago.
 */
export function reconcileLayout(
  sections: PageSection[],
  config: LayoutConfig,
  railIds: number[],
): PageSection[] {
  const seen = new Set<string>()
  const out: PageSection[] = []
  const normalize = (s: PageSection): PageSection => {
    const panels = config.panels[s.key]
    return {
      ...s,
      hero: s.key in config.heroDefaults ? (s.hero ?? config.heroDefaults[s.key]) : null,
      panels: panels
        ? [
            ...(s.panels ?? []).filter((p) => panels.includes(p.key)),
            ...panels
              .filter((key) => !(s.panels ?? []).some((p) => p.key === key))
              .map((key) => ({ key, enabled: true })),
          ]
        : null,
    }
  }

  for (const s of sections) {
    const known =
      config.canonical.includes(s.key) || (isRailKey(s.key) && railIds.includes(railIdOf(s.key)))
    if (known && !seen.has(s.key)) {
      seen.add(s.key)
      out.push(normalize(s))
    }
  }
  for (const key of config.canonical) {
    if (!seen.has(key)) {
      seen.add(key)
      out.push(normalize({ key, enabled: true }))
    }
  }
  for (const id of railIds) {
    const key: RailKey = `rail:${id}`
    if (seen.has(key)) continue
    seen.add(key)
    out.splice(railInsertAt(out, config.railAnchor), 0, { key, enabled: true, hero: null, panels: null })
  }
  return out
}

export function moveSection(sections: PageSection[], from: number, to: number): PageSection[] {
  if (from === to || to < 0 || to >= sections.length) return sections
  const next = [...sections]
  const [moved] = next.splice(from, 1)
  next.splice(to, 0, moved)
  return next
}

export function patchSection(sections: PageSection[], key: string, patch: Partial<PageSection>): PageSection[] {
  return sections.map((s) => (s.key === key ? { ...s, ...patch } : s))
}

export function sameLayout(a: PageSection[], b: PageSection[]): boolean {
  return JSON.stringify(a) === JSON.stringify(b)
}

/** Whether a section shows anything at all: switched on, and for a panel section, some panel on. */
export function sectionVisible(section: PageSection): boolean {
  return section.enabled && (section.panels == null || section.panels.some((p) => p.enabled))
}
