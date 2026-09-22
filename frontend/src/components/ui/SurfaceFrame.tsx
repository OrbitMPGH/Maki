import type { ComponentPropsWithoutRef, ReactNode } from 'react'

export type SurfaceWidth = 'full' | 'wide'
export type SurfacePageStyle = 'standard' | 'editorial' | 'operational'

type Props = Omit<ComponentPropsWithoutRef<'div'>, 'children'> & {
  children?: ReactNode
  width?: SurfaceWidth
  pageStyle?: SurfacePageStyle
}

export function SurfaceFrame({
  children,
  className,
  width = 'wide',
  pageStyle = 'standard',
  ...others
}: Props) {
  const classes = ['surface-frame', `surface-frame--${width}`, `surface-frame--${pageStyle}`, className]
    .filter(Boolean)
    .join(' ')
  return (
    <div {...others} className={classes} data-page-style={pageStyle}>
      {children}
    </div>
  )
}
