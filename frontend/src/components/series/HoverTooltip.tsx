import { cloneElement, useEffect, useRef, useState } from 'react'
import type { PointerEvent as ReactPointerEvent, ReactElement, Ref } from 'react'
import { Tooltip, getRefProp, type TooltipProps } from '@mantine/core'
import { useMergedRef } from '@mantine/hooks'

type ChildProps = {
  ref?: Ref<HTMLElement>
  onPointerOver?: (event: ReactPointerEvent<HTMLElement>) => void
  onPointerLeave?: (event: ReactPointerEvent<HTMLElement>) => void
}

/** Closes whichever HoverTooltip is open, so a new one never joins it on screen. */
let closeOpenTooltip: (() => void) | null = null

/**
 * A Mantine Tooltip that mounts the first time a mouse reaches its target. Every mounted Tooltip
 * sets up its own floating state, and the chapter table renders several per row on pages hundreds
 * of rows long, where they were a fifth of the render. Opens on mouse hover only, which is what
 * Mantine's own defaults do: focus and touch don't open a Tooltip either.
 *
 * Mounting the Tooltip replaces the target's DOM node, and the browser keeps treating the removed
 * node as the one under the pointer until its next hit test. A scroll in that gap sends the pointerout
 * to the removed node, which React never sees, and React then also drops the matching pointerover on
 * the next element because its relatedTarget is that removed node. So the replaced target never got
 * onPointerLeave (its tooltip stayed open) and the next one never got onPointerEnter. An open tooltip
 * therefore also closes when a pointer move or a scroll finds the pointer outside it, and opens on
 * onPointerOver, which React dispatches as is rather than pairing it with an out.
 */
export function HoverTooltip({ children, disabled, ...props }: TooltipProps & { children: ReactElement<ChildProps> }) {
  const [mounted, setMounted] = useState(false)
  const [opened, setOpened] = useState(false)
  const targetRef = useRef<HTMLElement>(null)
  const pointer = useRef({ x: 0, y: 0 })
  const ref = useMergedRef(targetRef, getRefProp(children) as Ref<HTMLElement> | undefined)

  useEffect(() => {
    if (!opened) return
    const close = () => setOpened(false)
    closeOpenTooltip?.()
    closeOpenTooltip = close
    const outside = (node: Node | null) => !node || !targetRef.current?.contains(node)
    const onMove = (event: PointerEvent) => {
      pointer.current = { x: event.clientX, y: event.clientY }
      if (outside(event.target as Node)) close()
    }
    const onScroll = () => {
      if (outside(document.elementFromPoint(pointer.current.x, pointer.current.y))) close()
    }
    document.addEventListener('pointermove', onMove, { capture: true, passive: true })
    document.addEventListener('scroll', onScroll, { capture: true, passive: true })
    document.documentElement.addEventListener('pointerleave', close)
    return () => {
      if (closeOpenTooltip === close) closeOpenTooltip = null
      document.removeEventListener('pointermove', onMove, { capture: true })
      document.removeEventListener('scroll', onScroll, { capture: true })
      document.documentElement.removeEventListener('pointerleave', close)
    }
  }, [opened])

  const target = cloneElement(children, {
    ref,
    onPointerOver: (event: ReactPointerEvent<HTMLElement>) => {
      children.props.onPointerOver?.(event)
      if (event.pointerType !== 'mouse' || opened) return
      pointer.current = { x: event.clientX, y: event.clientY }
      setMounted(true)
      setOpened(true)
    },
    onPointerLeave: (event: ReactPointerEvent<HTMLElement>) => {
      children.props.onPointerLeave?.(event)
      setOpened(false)
    },
  })
  if (!mounted || disabled) return target
  return (
      <Tooltip {...props} opened={opened}>
        {target}
      </Tooltip>
  )
}
