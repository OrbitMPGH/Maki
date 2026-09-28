import type { ComponentPropsWithRef, CSSProperties } from 'react'

/**
 * A status as a coloured dot and a word, for dense tables where a filled badge on every row turns
 * the column into a stripe of colour. `tone` is a token stem (`ok`, `warn`, `danger`, ...), as
 * returned by `statusToken`. `live` pulses the dot for work in flight. Takes a ref and the rest of
 * a span's props so it can sit directly inside a Tooltip.
 */
export function StatusDot({
  tone,
  live,
  style,
  ...rest
}: ComponentPropsWithRef<'span'> & { tone: string; live?: boolean }) {
  return (
    <span
      {...rest}
      className="status-dot"
      data-live={live || undefined}
      style={{ ...style, '--dot': `var(--${tone})` } as CSSProperties}
    />
  )
}
