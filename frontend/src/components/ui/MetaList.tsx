import { Children, isValidElement, type ReactNode } from 'react'

/**
 * A run of short facts ("Ongoing | 2019 | 142 chapters") separated by a hairline, never a glyph.
 * Falsy items are dropped, so callers can pass conditionals inline. `inline` renders spans instead
 * of a list, for text that truncates or line-clamps around it.
 */
export function MetaList({
  items,
  inline,
  className,
  style,
}: {
  items: ReactNode[]
  inline?: boolean
  className?: string
  style?: React.CSSProperties
}) {
  const shown = items.filter((item) => item !== null && item !== undefined && item !== false && item !== '')
  if (shown.length === 0) return null
  const classes = [inline ? 'meta-list meta-list-inline' : 'meta-list', className].filter(Boolean).join(' ')
  const Item = inline ? 'span' : 'li'
  const children = Children.map(shown, (item, i) => (
    <Item key={isValidElement(item) && item.key != null ? item.key : i}>{item}</Item>
  ))
  return inline ? (
    <span className={classes} style={style}>
      {children}
    </span>
  ) : (
    <ul className={classes} style={style}>
      {children}
    </ul>
  )
}
