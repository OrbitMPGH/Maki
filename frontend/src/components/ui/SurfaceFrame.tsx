import type { ComponentPropsWithoutRef, ReactNode } from 'react'

export type PageStyle = 'standard' | 'editorial' | 'operational'

export type SurfaceFrameProps = Omit<ComponentPropsWithoutRef<'div'>, 'children' | 'className'> & {
  children?: ReactNode
  className?: string
  pageStyle?: PageStyle
}

/**
 * Route-level framing for the Maki visual language. It keeps page composition
 * decisions at the surface boundary instead of adding style props to every
 * shared component.
 */
export function SurfaceFrame({
  children,
  className,
  pageStyle = 'standard',
  ...others
}: SurfaceFrameProps) {
  const classes = ['surface-frame', `surface-frame--${pageStyle}`, className].filter(Boolean).join(' ')

  return (
    <div {...others} className={classes} data-page-style={pageStyle}>
      {children}
    </div>
  )
}
