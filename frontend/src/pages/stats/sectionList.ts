import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import type { Icon } from '@tabler/icons-react'
import {
  IconBook2,
  IconBooks,
  IconChartLine,
  IconClock,
  IconHeart,
  IconTrophy,
} from '@tabler/icons-react'

export type StatsSectionKey = 'reading' | 'rhythm' | 'taste' | 'habits' | 'library' | 'progress'

export interface StatsSectionDef {
  key: StatsSectionKey
  label: MessageDescriptor
  icon: Icon
  /** A short note beside the rail link for a section that is not about the selected reader. */
  railChip?: MessageDescriptor
}

/** The page's sections, in order. Shared by the rail and the page so the two cannot drift. */
export const STATS_SECTIONS: StatsSectionDef[] = [
  { key: 'reading', label: msg`Reading`, icon: IconBook2 },
  { key: 'rhythm', label: msg`Rhythm`, icon: IconClock },
  { key: 'taste', label: msg`Taste`, icon: IconHeart },
  { key: 'habits', label: msg`Habits`, icon: IconChartLine },
  {
    key: 'library',
    label: msg`Library`,
    icon: IconBooks,
    railChip: msg({ message: `everyone`, comment: `Beside the Library link: that section covers the whole instance, not one reader.` }),
  },
  { key: 'progress', label: msg`Progress`, icon: IconTrophy },
]

export function sectionElementId(key: StatsSectionKey): string {
  return `stats-${key}`
}

export function isStatsSectionKey(value: string | null): value is StatsSectionKey {
  return STATS_SECTIONS.some((s) => s.key === value)
}
