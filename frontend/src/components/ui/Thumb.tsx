import type { CSSProperties, HTMLAttributes } from 'react'

export type ThumbSize = 'sm' | 'md' | 'lg'

type ThumbProps = HTMLAttributes<HTMLSpanElement> & {
  src?: string | null
  alt?: string
  size?: ThumbSize
  /** Overrides the size's width, in px; the 2:3 ratio and the size's radius stay. */
  width?: number
}

/**
 * A small cover for rows, lists and tables: 2:3, one radius per size, the cover rim, and the brand
 * tile when there is no cover. Poster-sized covers use `CoverCard`.
 */
export function Thumb({ src, alt = '', size = 'md', width, className, style, children, ...rest }: ThumbProps) {
  const sized = width != null ? ({ '--thumb-w': `${width}px`, ...style } as CSSProperties) : style
  return (
    <span
      {...rest}
      className={className ? `thumb ${className}` : 'thumb'}
      data-size={size}
      data-empty={src ? undefined : ''}
      style={sized}
    >
      {src && <img src={src} alt={alt} loading="lazy" decoding="async" />}
      {children}
    </span>
  )
}
