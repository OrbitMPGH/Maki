import type { ReactNode } from 'react'
import type { Icon } from '@tabler/icons-react'
import { SectionHeader } from '../../components/ui/SectionHeader'
import { sectionElementId, type StatsSectionKey } from './sectionList'

/**
 * One section of the Stats page: the house section header, an optional scope chip for a section
 * that does not follow the page's window or reader, the insight sentence, then its panels.
 */
export function StatsSection({
  sectionKey,
  icon,
  title,
  scope,
  insight,
  children,
}: {
  sectionKey: StatsSectionKey
  icon: Icon
  title: string
  /** "All time", "Whole library". */
  scope?: string
  insight?: ReactNode
  children: ReactNode
}) {
  return (
    <section id={sectionElementId(sectionKey)} className="stats-section" aria-label={title}>
      <SectionHeader
        icon={icon}
        title={title}
        action={scope ? <span className="stats-scope">{scope}</span> : undefined}
      />
      {insight && <StatsInsight>{insight}</StatsInsight>}
      {children}
    </section>
  )
}

/**
 * The sentence that opens a section. Wrap the figures it leads with in `<em>`; a quieter trailing
 * clause goes in `<span className="stats-insight-dim">`.
 */
export function StatsInsight({ children }: { children: ReactNode }) {
  return <div className="stats-insight">{children}</div>
}
