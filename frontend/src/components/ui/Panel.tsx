import { forwardRef } from 'react'
import type { ComponentPropsWithoutRef } from 'react'
import { Paper } from '@mantine/core'
import type { PaperProps } from '@mantine/core'

export type PanelProps = PaperProps & Omit<ComponentPropsWithoutRef<'div'>, keyof PaperProps | 'color'>

/**
 * The house panel: bordered, `radius="md"`, `p="lg"` and the 1px card shadow. Everything else
 * passes through to Mantine `Paper`.
 */
export const Panel = forwardRef<HTMLDivElement, PanelProps>(function Panel({ className, p = 'lg', ...rest }, ref) {
  return (
    <Paper
      ref={ref}
      withBorder
      radius="md"
      p={p}
      className={className ? `panel ${className}` : 'panel'}
      {...rest}
    />
  )
})
