import type { AriaAttributes, CSSProperties, MouseEventHandler, ReactNode } from 'react'
import { Link } from 'react-router-dom'

export interface TagChipProps {
  children: ReactNode
  /** Any CSS colour value. Present means the chip shows its 6px bucket dot. */
  dot?: string
  active?: boolean
  onClick?: MouseEventHandler<HTMLButtonElement>
  href?: string
  size?: 'sm' | 'md'
  className?: string
  title?: string
  style?: CSSProperties
}

/**
 * The `.tag-chip` pill as a component: bordered, dotted, and interactive when given an `onClick`
 * or an `href`. Plain chips stay spans so a row of labels is not a row of controls.
 */
export function TagChip({
  children,
  dot,
  active,
  onClick,
  href,
  size = 'md',
  className,
  ...rest
}: TagChipProps & Pick<AriaAttributes, 'aria-label' | 'aria-pressed' | 'aria-current'>) {
  const cls = className ? `tag-chip ${className}` : 'tag-chip'
  const inner = (
    <>
      {dot && <span className="tag-dot" style={{ '--bucket': dot } as CSSProperties} />}
      <span className="tag-chip-body">{children}</span>
    </>
  )
  const shared = {
    className: cls,
    'data-active': active || undefined,
    'data-size': size === 'sm' ? 'sm' : undefined,
    ...rest,
  }

  if (href) {
    return (
      <Link to={href} data-interactive {...shared}>
        {inner}
      </Link>
    )
  }
  if (onClick) {
    return (
      <button
        type="button"
        onClick={onClick}
        aria-pressed={active === undefined ? undefined : active}
        data-interactive
        {...shared}
      >
        {inner}
      </button>
    )
  }
  return <span {...shared}>{inner}</span>
}

/** The flex-wrap row the chips live in. */
export function TagChips({
  children,
  className,
  style,
}: {
  children: ReactNode
  className?: string
  style?: CSSProperties
}) {
  return (
    <div className={className ? `tag-chips ${className}` : 'tag-chips'} style={style}>
      {children}
    </div>
  )
}
