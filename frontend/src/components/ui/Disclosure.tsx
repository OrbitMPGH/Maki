import { useId, type ReactNode } from 'react'
import { Collapse, UnstyledButton, VisuallyHidden } from '@mantine/core'
import { IconChevronDown } from '@tabler/icons-react'

/**
 * A heading that opens the controls under it, for surfaces that keep every control but only need a
 * few on screen at once. `count` badges how many are set, so a closed group still says it is doing
 * something; `countLabel` is what a screen reader hears for it. `summary` shows under the title
 * while closed. The body stays mounted when closed, so a half-typed edit survives a collapse.
 */
export function Disclosure({
  title,
  count,
  countLabel,
  summary,
  opened,
  onToggle,
  children,
}: {
  title: ReactNode
  count?: number
  countLabel?: string
  summary?: ReactNode
  opened: boolean
  onToggle: () => void
  children: ReactNode
}) {
  const bodyId = useId()

  return (
    <div className="disclosure" data-open={opened || undefined}>
      <UnstyledButton
        className="disclosure-head"
        aria-expanded={opened}
        aria-controls={bodyId}
        onClick={onToggle}
      >
        <span className="disclosure-text">
          <span className="disclosure-line">
            <span className="disclosure-title">{title}</span>
            {count != null && count > 0 && (
              <>
                <span className="disclosure-count tnum" aria-hidden>
                  {count}
                </span>
                {countLabel && <VisuallyHidden>{countLabel}</VisuallyHidden>}
              </>
            )}
          </span>
          {summary && !opened && <span className="disclosure-summary">{summary}</span>}
        </span>
        <IconChevronDown size={16} className="disclosure-chevron" aria-hidden />
      </UnstyledButton>
      <Collapse expanded={opened} id={bodyId}>
        <div className="disclosure-body">{children}</div>
      </Collapse>
    </div>
  )
}
