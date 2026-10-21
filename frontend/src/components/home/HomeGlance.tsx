import { useId, type ReactNode } from 'react'
import { VisuallyHidden } from '@mantine/core'
import type { Icon } from '@tabler/icons-react'
import { useLingui } from '@lingui/react/macro'

/**
 * Home's labelled figures in one raised card. Placed two ways: floating on the right of the Continue
 * hero (`hosted`, wide screens) or full width above it, where the groups sit side by side when there
 * is room. Which one is a CSS container query on `.continue-lead`, so the markup is the same at
 * every width. See `.home-glance` in theme.css.
 */
export function HomeGlance({ children, hosted }: { children: ReactNode; hosted?: boolean }) {
  const { t } = useLingui()
  const headingId = useId()
  return (
    <section className="home-glance" data-hosted={hosted || undefined} aria-labelledby={headingId}>
      <VisuallyHidden id={headingId}>{t`At a glance`}</VisuallyHidden>
      <div className="glance-card">{children}</div>
    </section>
  )
}

export function GlanceRow({ children }: { children: ReactNode }) {
  return <div className="glance-row">{children}</div>
}

export interface GlanceFigure {
  label: string
  value: string | number
  /** A status token name (`ok`, `warn`, `danger`); omitted leaves the figure at `--ink-hi`. */
  tone?: 'ok' | 'warn' | 'danger'
  icon?: Icon
}

/**
 * One number per cell with a hairline between them. Only the ones that carry a judgement take a
 * colour: none of these is a state anyone acts on.
 */
export function GlanceFigures({ figures }: { figures: GlanceFigure[] }) {
  return (
    <div className="glance-figures">
      {figures.map(({ label, value, tone, icon: FigIcon }) => (
        <div className="glance-fig" key={label}>
          <span className="glance-fig-n figure tnum" style={tone ? { color: `var(--${tone})` } : undefined}>
            {FigIcon && <FigIcon size={14} className="glance-fig-icon" aria-hidden />}
            {value}
          </span>
          <span className="glance-fig-l">{label}</span>
        </div>
      ))}
    </div>
  )
}
