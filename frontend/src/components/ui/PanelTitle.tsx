import type { ReactNode } from 'react'
import type { Icon } from '../../lib/icons'

/**
 * The title inside a panel or card: one tier under a section header, in the display face at 16px,
 * with an optional 16px accent icon. Every panel title on a page shares it, so two panels side by
 * side never disagree. A label over a panel from outside (Settings sections) stays a section title.
 */
export function PanelTitle({
  icon: TitleIcon,
  order = 3,
  className,
  children,
}: {
  icon?: Icon
  order?: 2 | 3 | 4
  className?: string
  children: ReactNode
}) {
  const Heading = `h${order}` as const
  return (
    <Heading className={className ? `panel-title ${className}` : 'panel-title'}>
      {TitleIcon && <TitleIcon size={16} className="panel-title-icon" aria-hidden />}
      {children}
    </Heading>
  )
}
