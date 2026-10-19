import { useLayoutEffect, useRef, useState, type ReactNode, type RefObject } from 'react'
import { Group, Stack, Text, Title } from '@mantine/core'

/**
 * Consistent page header: title (+ optional description) on the left, actions
 * on the right, wrapping gracefully on narrow screens. `compact` is the smaller
 * header for operational pages, where the content below is the point.
 *
 * `center` sits between the title and the actions, at its natural width, whenever it fits there,
 * and drops to its own full-width row underneath when it does not (`.page-header-split`).
 */
export function PageHeader({
  title,
  description,
  actions,
  center,
  compact,
  className,
}: {
  title: ReactNode
  description?: ReactNode
  actions?: ReactNode
  center?: ReactNode
  compact?: boolean
  className?: string
}) {
  const heading = (
    <Stack gap={2} style={{ minWidth: 0 }} className="page-header-title">
      <Title order={1}>{title}</Title>
      {description && (
        <Text size="sm" c="var(--ink-3)" maw={620}>
          {description}
        </Text>
      )}
    </Stack>
  )

  if (center) {
    return (
      <SplitHeader heading={heading} center={center} actions={actions} compact={compact} className={className} />
    )
  }

  return (
    <Group
      className={className ? `page-header ${className}` : 'page-header'}
      data-compact={compact || undefined}
      justify="space-between"
      align="flex-end"
      wrap="wrap"
      gap="sm"
      mb={compact ? 'md' : 'lg'}
    >
      {heading}
      {actions && <Group gap="xs">{actions}</Group>}
    </Group>
  )
}

function SplitHeader({
  heading,
  center,
  actions,
  compact,
  className,
}: {
  heading: ReactNode
  center: ReactNode
  actions?: ReactNode
  compact?: boolean
  className?: string
}) {
  const wrapRef = useRef<HTMLDivElement>(null)
  const centerRef = useRef<HTMLDivElement>(null)
  const inline = useFitsInline(wrapRef, centerRef)

  return (
    <div ref={wrapRef} className="page-header-wrap" data-compact={compact || undefined}>
      <div
        className={className ? `page-header page-header-split ${className}` : 'page-header page-header-split'}
        data-inline={inline || undefined}
      >
        {heading}
        <div ref={centerRef} className="page-header-center">
          {center}
        </div>
        {actions && (
          <Group gap="xs" wrap="nowrap" className="page-header-actions">
            {actions}
          </Group>
        )}
      </div>
    </div>
  )
}

/**
 * Whether the middle fits on the title row. The only way to know is to lay it out there, so the
 * row is tried and dropped again inside a layout effect, before anything paints. While it is
 * dropped, the width it overflowed by says how wide the header has to get before it is worth
 * trying again; new content resets that, since it may be narrower.
 */
function useFitsInline(wrapRef: RefObject<HTMLDivElement | null>, centerRef: RefObject<HTMLDivElement | null>) {
  const [inline, setInline] = useState(true)
  const needed = useRef(0)

  useLayoutEffect(() => {
    const wrap = wrapRef.current
    const center = centerRef.current
    if (!wrap || !center) return
    const check = () => {
      const width = wrap.clientWidth
      if (inline) {
        const content = center.firstElementChild
        const over = content ? content.getBoundingClientRect().width - center.clientWidth : 0
        if (over > 0.5) {
          needed.current = width + over
          setInline(false)
        }
      } else if (width >= needed.current) {
        setInline(true)
      }
    }
    const retry = () => {
      needed.current = 0
      check()
    }
    check()
    if (typeof ResizeObserver === 'undefined') return
    const resize = new ResizeObserver(check)
    resize.observe(wrap)
    if (center.firstElementChild) resize.observe(center.firstElementChild)
    const mutation = new MutationObserver(retry)
    mutation.observe(center, { childList: true, subtree: true, characterData: true })
    return () => {
      resize.disconnect()
      mutation.disconnect()
    }
  }, [inline, wrapRef, centerRef])

  return inline
}
