import { createContext, useContext } from 'react'
import { Title } from '@mantine/core'
import type { Icon } from '../../lib/icons'
import { formatNumber } from '../../format'

/** A short label drawn after the title of every header under the provider. */
export const SectionTagContext = createContext<string | null>(null)

/**
 * The heading above a rail or grid: an accent icon, the title, an optional count, a rule running to
 * the optional action ("Show more", "Refresh") at the far end, and the slot the rail below it fills
 * with its prev/next arrows (see `Rail`), just before the action. The rule is the tag buckets' idiom at
 * section scale: it ties the title to its action and gives a run of rails a line to read down.
 *
 * Shared by Discover, the series page's related rail and the Home dashboard, which is why `count`
 * is optional: Home's rails already say how many items they hold by showing them. `chevron` adds the
 * trailing arrow for a single text action that opens a bigger view.
 */
export function SectionHeader({
  icon: SectionIcon,
  title,
  count,
  action,
  chevron,
}: {
  icon: Icon
  title: string
  count?: number
  action?: React.ReactNode
  chevron?: boolean
}) {
  const tag = useContext(SectionTagContext)
  return (
    <div className="section-header">
      <span className="section-header-lead">
        <span className="section-header-icon" aria-hidden>
          <SectionIcon size={21} />
        </span>
        <Title order={2} className="section-header-title">
          {title}
        </Title>
        {count != null && <span className="section-header-count tnum">{formatNumber(count)}</span>}
        {tag && <span className="section-header-tag">{tag}</span>}
      </span>
      <span className="section-header-rule" aria-hidden />
      <div className="section-header-end">
        <div className="section-header-arrows" />
        {action && (
          <div className="section-header-action" data-chevron={chevron || undefined}>
            {action}
          </div>
        )}
      </div>
    </div>
  )
}
