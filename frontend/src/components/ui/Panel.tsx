import { forwardRef } from 'react'
import type { ComponentPropsWithoutRef } from 'react'
import { Paper } from '@mantine/core'
import type { PaperProps } from '@mantine/core'

export type PanelEdge = 'brand' | 'info' | 'ok' | 'warn' | 'danger' | 'strong'

export interface PanelProps
  extends PaperProps,
    Omit<ComponentPropsWithoutRef<'div'>, keyof PaperProps | 'color'> {
  /** Draws the 2px accent rule. `strong` is the neutral `--border-strong` variant. */
  edge?: PanelEdge
  /** Which side the accent rule sits on. Left drops the right border, top keeps all four. */
  edgeSide?: 'top' | 'left'
}

/**
 * The house panel: bordered, `radius="lg"`, `p="lg"`, no shadow, with the optional accent edge
 * that stands in for one. Everything else passes through to Mantine `Paper`.
 */
export const Panel = forwardRef<HTMLDivElement, PanelProps>(function Panel(
  { edge, edgeSide = 'top', className, p = 'lg', ...rest },
  ref,
) {
  return (
    <Paper
      ref={ref}
      withBorder
      radius="lg"
      p={p}
      className={className ? `panel ${className}` : 'panel'}
      data-edge={edge}
      data-edge-side={edge ? edgeSide : undefined}
      {...rest}
    />
  )
})
