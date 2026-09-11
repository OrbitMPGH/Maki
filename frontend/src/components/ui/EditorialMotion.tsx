import type { ReactNode } from 'react'

export type EditorialMotionMode = 'image-scale'

type EditorialMotionProps = {
  mode: EditorialMotionMode
  children?: ReactNode
  className?: string
}

/**
 * The one editorial moment Maki animates: a featured cover scaling and fading in
 * as it mounts. The animation itself lives in the stylesheet, so
 * `prefers-reduced-motion` is honoured without a JS guard and without an
 * animation library in the bundle.
 *
 * The wrapper stays a component rather than a bare class so the two callers that
 * use it (the series hero and the discovery detail modal) keep reading as one
 * shared decision.
 */
export function EditorialMotion({ mode, children, className }: EditorialMotionProps) {
  const classes = ['editorial-motion', `editorial-motion--${mode}`, className]
    .filter(Boolean)
    .join(' ')

  return (
    <div className={classes} data-editorial-motion={mode}>
      {children}
    </div>
  )
}
