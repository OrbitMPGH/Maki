import type { ComponentPropsWithoutRef, ReactNode } from 'react'

export type SurfaceWidth = 'full' | 'wide'

type Props = Omit<ComponentPropsWithoutRef<'div'>, 'children'> & {
  children?: ReactNode
  width?: SurfaceWidth
}

export function SurfaceFrame({ children, className, width = 'wide', ...others }: Props) {
  const classes = ['surface-frame', `surface-frame--${width}`, className].filter(Boolean).join(' ')
  return (
    <div {...others} className={classes}>
      {children}
    </div>
  )
}
