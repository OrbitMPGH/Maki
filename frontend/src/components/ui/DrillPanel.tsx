import { useEffect, useId, useRef, useState, type ComponentType, type ReactNode, type Ref } from 'react'
import { UnstyledButton, VisuallyHidden } from '@mantine/core'
import { useLingui } from '@lingui/react/macro'
import { IconArrowLeft, IconCheck, IconChevronRight } from '../../lib/icons'

/**
 * The pieces of a drill panel: the reader settings, the series page's "More actions" menu, the
 * Discover add card and the Pause downloads panel. A first page of rows that each show their current value, sub-pages that slide
 * in with a back header, and on them either a list of choices with a hint each or a labelled field.
 * `DrillPanes` does the sliding; `useDrillPages` keeps the page and puts focus back where it was.
 */

type IconComponent = ComponentType<{ size?: number; className?: string; 'aria-hidden'?: boolean }>

/** A row that opens a sub-page: the setting's icon in the accent, its name, its current value. */
export function DrillRow({
  icon: Icon,
  label,
  value,
  active,
  chevron = true,
  onClick,
  ref,
}: {
  icon: IconComponent
  label: ReactNode
  value?: ReactNode
  /** The value is one the user set, not the default: it reads in the accent. */
  active?: boolean
  /** Off for a row that runs an action rather than opening a page. */
  chevron?: boolean
  onClick: () => void
  ref?: Ref<HTMLButtonElement>
}) {
  return (
    <UnstyledButton ref={ref} className="drill-row" onClick={onClick}>
      <Icon size={16} className="drill-row-icon" aria-hidden />
      <span className="drill-row-label">{label}</span>
      {value != null && (
        <span className="drill-row-value" data-active={active || undefined}>
          {value}
        </span>
      )}
      {chevron && <IconChevronRight size={16} className="drill-row-chevron" aria-hidden />}
    </UnstyledButton>
  )
}

/** A sub-page's header: back, and the page's name. The hidden word keeps "Back" in its name. */
export function DrillBack({
  title,
  onBack,
  ref,
  className = 'drill-panel-back',
}: {
  title: ReactNode
  onBack: () => void
  ref?: Ref<HTMLButtonElement>
  /** Replaces the panel spacing, for a panel whose content is not inset like rows are. */
  className?: string
}) {
  const { t } = useLingui()
  return (
    <UnstyledButton ref={ref} className={`drill-back ${className}`} onClick={onBack}>
      <span className="drill-back-icon" aria-hidden>
        <IconArrowLeft size={16} />
      </span>
      <VisuallyHidden>{t`Back`}</VisuallyHidden>
      <span>{title}</span>
    </UnstyledButton>
  )
}

export interface DrillChoice {
  value: string
  label: ReactNode
  /** One line on what choosing it does, or why it can't be chosen. */
  hint?: ReactNode
  disabled?: boolean
}

/** One choice from a list, each with its hint under it. The current one carries the check. */
export function DrillChoices({
  label,
  heading,
  value,
  options,
  onPick,
}: {
  /** Names the group for a screen reader; the back header shows it to everyone else. */
  label: string
  /** A visible name over the list, for a page that splits its choices into groups. */
  heading?: ReactNode
  value: string
  options: DrillChoice[]
  onPick: (value: string) => void
}) {
  const headingId = useId()
  return (
    <div
      role="radiogroup"
      aria-label={heading ? undefined : label}
      aria-labelledby={heading ? headingId : undefined}
      className="drill-choices"
    >
      {heading && (
        <div id={headingId} className="drill-choices-heading">
          {heading}
        </div>
      )}
      {options.map((o) => {
        const checked = o.value === value
        return (
          <UnstyledButton
            key={o.value}
            role="radio"
            aria-checked={checked}
            data-checked={checked || undefined}
            disabled={o.disabled}
            className="drill-choice"
            onClick={() => onPick(o.value)}
          >
            <span className="drill-choice-check" aria-hidden>
              {checked && <IconCheck size={16} />}
            </span>
            <span className="drill-choice-text">
              <span className="drill-choice-label">{o.label}</span>
              {o.hint && <span className="drill-choice-hint">{o.hint}</span>}
            </span>
          </UnstyledButton>
        )
      })}
    </div>
  )
}

/**
 * A setting changed in place: its icon and name over the control (a segmented control for a short
 * choice), and one line under it saying what the current pick does.
 */
export function DrillField({
  icon: Icon,
  title,
  help,
  children,
}: {
  icon: IconComponent
  title: ReactNode
  help?: ReactNode
  children: ReactNode
}) {
  return (
    <div className="drill-field">
      <div className="drill-field-title" aria-hidden>
        <Icon size={16} />
        <span>{title}</span>
      </div>
      {children}
      {help && <div className="drill-field-help">{help}</div>}
    </div>
  )
}

/**
 * The page a drill panel shows, its depth for `DrillPanes`, and focus handling: opening a page
 * moves focus to its back header, going back returns it to the row that opened the page.
 */
export function useDrillPages<P extends string>(root: P, parents: Record<P, P>) {
  const [page, setPage] = useState<P>(root)
  const from = useRef<P | null>(null)
  const backRef = useRef<HTMLButtonElement>(null)
  const rows = useRef<Partial<Record<P, HTMLButtonElement | null>>>({})
  const depthOf = (p: P): number => (p === root ? 0 : 1 + depthOf(parents[p]))

  useEffect(() => {
    const came = from.current
    if (came === null) return
    if (depthOf(page) > depthOf(came)) backRef.current?.focus({ preventScroll: true })
    else rows.current[came]?.focus({ preventScroll: true })
    // depthOf is derived from props that never change for a panel.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [page])

  return {
    page,
    depth: depthOf(page),
    open: (to: P) => {
      from.current = page
      setPage(to)
    },
    back: () => {
      from.current = page
      setPage(parents[page])
    },
    /** Straight back to the first page without moving focus, for a panel that is reopening. */
    reset: () => {
      from.current = null
      setPage(root)
    },
    backRef,
    /** The ref for the row that opens `p`, so going back can focus it. */
    rowRef: (p: P) => (node: HTMLButtonElement | null) => {
      rows.current[p] = node
    },
  }
}
