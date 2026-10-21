import { forwardRef } from 'react'
import type { ReactNode } from 'react'
import { Indicator } from '@mantine/core'
import type { IndicatorProps } from '@mantine/core'

/**
 * The unread or in-progress count on an icon button: one pill shape, size and placement for every
 * icon that carries a number, hidden at zero. The offset tucks it onto the glyph's corner rather
 * than the button's, so it never pokes past the row it sits in. It sits inside Tooltip and
 * Popover.Target, so the ref and the props they inject pass through to the Indicator.
 */
export const CountIndicator = forwardRef<
  HTMLDivElement,
  Omit<IndicatorProps, 'label' | 'disabled'> & {
    count: number
    tone?: 'brand' | 'warn' | 'danger'
    children: ReactNode
  }
>(function CountIndicator({ count, tone = 'brand', children, className, ...rest }, ref) {
  return (
    <Indicator
      ref={ref}
      size={16}
      offset={5}
      withBorder
      {...rest}
      label={count > 99 ? '99+' : count}
      disabled={count <= 0}
      className={className ? `count-indicator ${className}` : 'count-indicator'}
      data-tone={tone}
    >
      {children}
    </Indicator>
  )
})
