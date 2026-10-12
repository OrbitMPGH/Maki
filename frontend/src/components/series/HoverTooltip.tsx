import { cloneElement, useState } from 'react'
import type { PointerEvent, ReactElement } from 'react'
import { Tooltip, type TooltipProps } from '@mantine/core'

type ChildProps = {
  onPointerEnter?: (event: PointerEvent<HTMLElement>) => void
  onPointerLeave?: (event: PointerEvent<HTMLElement>) => void
}

/**
 * A Mantine Tooltip that mounts the first time a mouse reaches its target. Every mounted Tooltip
 * sets up its own floating state, and the chapter table renders several per row on pages hundreds
 * of rows long, where they were a fifth of the render. Opens on mouse hover only, which is what
 * Mantine's own defaults do: focus and touch don't open a Tooltip either.
 */
export function HoverTooltip({ children, disabled, ...props }: TooltipProps & { children: ReactElement<ChildProps> }) {
  const [mounted, setMounted] = useState(false)
  const [hovered, setHovered] = useState(false)
  const target = cloneElement(children, {
    onPointerEnter: (event: PointerEvent<HTMLElement>) => {
      children.props.onPointerEnter?.(event)
      if (event.pointerType !== 'mouse') return
      setMounted(true)
      setHovered(true)
    },
    onPointerLeave: (event: PointerEvent<HTMLElement>) => {
      children.props.onPointerLeave?.(event)
      setHovered(false)
    },
  })
  if (!mounted || disabled) return target
  return (
      <Tooltip {...props} opened={hovered}>
        {target}
      </Tooltip>
  )
}
