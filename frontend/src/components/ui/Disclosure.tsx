import { useId, type ReactNode } from 'react'
import { Collapse, UnstyledButton, VisuallyHidden } from '@mantine/core'
import { IconChevronDown } from '@tabler/icons-react'

/**
 * A heading that opens the controls under it. Used where a surface keeps every control but only
 * needs a few on screen at once: the library's filter groups, Health's thresholds and the advanced
 * half of a settings tab.
 *
 * `count` badges how many things inside are set, so a closed group still says it is doing
 * something; `countLabel` is what a screen reader hears for it. `summary` is one line of what is
 * inside, shown under the title while closed. The body stays mounted when closed, so a half-typed
 * edit survives a collapse.
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
