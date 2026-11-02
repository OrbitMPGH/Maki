import { useRef, useState, type DragEvent, type ReactNode } from 'react'
import { ActionIcon, Divider, Group, Stack, Switch, Text } from '@mantine/core'
import { IconChevronDown, IconChevronUp, IconGripVertical } from '@tabler/icons-react'
import { useLingui } from '@lingui/react/macro'

interface PriorityListProps {
  /** Ids in rank order, switched-off ones included. */
  items: string[]
  /** The subset of `items` that is switched off. */
  disabled: string[]
  onChange: (order: string[], disabled: string[]) => void
  renderLabel: (id: string) => string
  /** Badges and hints shown after the label. */
  renderExtra?: (id: string) => ReactNode
  toggleLabel: (id: string) => string
}

/**
 * A drag-ordered list with a per-row on/off switch, shared by the source and language sections of
 * Settings. A row stays in the order while switched off, so turning it back on returns it to
 * exactly the rank it had.
 */
export function PriorityList({
  items,
  disabled,
  onChange,
  renderLabel,
  renderExtra,
  toggleLabel,
}: PriorityListProps) {
  const { t } = useLingui()
  // The real order only changes on drop. While dragging, rows are shifted purely
  // visually (transform) to open a gap; reordering the DOM mid-drag made rows
  // slide past the stationary cursor and re-trigger, causing a feedback loop.
  const [dragFromIndex, setDragFromIndex] = useState<number | null>(null)
  const [hoverIndex, setHoverIndex] = useState<number | null>(null)
  const [rowHeight, setRowHeight] = useState(0)
  const containerRef = useRef<HTMLDivElement>(null)

  function handleContainerDragOver(e: DragEvent) {
    e.preventDefault()
    if (dragFromIndex === null || !containerRef.current || rowHeight === 0) return
    const rect = containerRef.current.getBoundingClientRect()
    const rawIndex = Math.floor((e.clientY - rect.top) / rowHeight)
    const clamped = Math.min(Math.max(rawIndex, 0), items.length - 1)
    setHoverIndex(clamped)
  }

  function commitDrag() {
    if (dragFromIndex !== null && hoverIndex !== null && dragFromIndex !== hoverIndex) {
      const next = [...items]
      const [moved] = next.splice(dragFromIndex, 1)
      next.splice(hoverIndex, 0, moved)
      onChange(next, disabled)
    }
    setDragFromIndex(null)
    setHoverIndex(null)
  }

  // Dragging needs a mouse; these buttons are the keyboard and touch path to the same order.
  function move(index: number, delta: number) {
    const next = [...items]
    const [moved] = next.splice(index, 1)
    next.splice(index + delta, 0, moved)
    onChange(next, disabled)
  }

  function toggle(id: string, on: boolean) {
    onChange(items, on ? disabled.filter((n) => n !== id) : [...disabled, id])
  }

  return (
    <Stack gap={0} mb="md" ref={containerRef} onDragOver={handleContainerDragOver}>
      {items.map((id, i) => {
        let shift = 0
        if (dragFromIndex !== null && hoverIndex !== null && i !== dragFromIndex) {
          if (dragFromIndex < hoverIndex && i > dragFromIndex && i <= hoverIndex) shift = -1
          else if (dragFromIndex > hoverIndex && i >= hoverIndex && i < dragFromIndex) shift = 1
        }
        const label = renderLabel(id)
        return (
          <div
            key={id}
            className="priority-row"
            style={{
              position: 'relative',
              transform: shift ? `translateY(${shift * rowHeight}px)` : undefined,
              transition: 'transform var(--dur-base) var(--ease)',
              pointerEvents: dragFromIndex !== null && i !== dragFromIndex ? 'none' : undefined,
            }}
          >
            <Group
              justify="space-between"
              align="center"
              wrap="nowrap"
              py={12}
              px={4}
              draggable
              onDragStart={(e) => {
                // setDragImage on the live node still tracks it, so the ghost goes
                // invisible along with the row once opacity flips to 0. Use a detached
                // clone instead, it's an independent snapshot.
                const original = e.currentTarget
                const clone = original.cloneNode(true) as HTMLElement
                clone.style.position = 'fixed'
                clone.style.top = '-9999px'
                clone.style.left = '-9999px'
                clone.style.width = `${original.offsetWidth}px`
                clone.style.pointerEvents = 'none'
                document.body.appendChild(clone)
                e.dataTransfer.setDragImage(clone, e.nativeEvent.offsetX, e.nativeEvent.offsetY)
                setTimeout(() => document.body.removeChild(clone), 0)
                setDragFromIndex(i)
                setHoverIndex(i)
                setRowHeight(original.getBoundingClientRect().height)
              }}
              onDragEnd={commitDrag}
              style={{
                cursor: 'grab',
                borderRadius: 'var(--mantine-radius-xs)',
                opacity: dragFromIndex === i ? 0 : 1,
              }}
            >
              <Group gap="sm" wrap="nowrap">
                <IconGripVertical size={14} opacity={0.5} />
                <Text size="sm" c="var(--ink-3)" w={20}>
                  {i + 1}
                </Text>
                <Text size="sm" fw={500} c={disabled.includes(id) ? 'var(--ink-3)' : undefined}>
                  {label}
                </Text>
                {renderExtra?.(id)}
              </Group>
              <Group gap={4} wrap="nowrap">
                <span className="priority-moves">
                  <ActionIcon
                    variant="subtle" color="var(--neutral)" disabled={i === 0}
                    aria-label={t`Move ${label} up`}
                    onClick={() => move(i, -1)}
                    draggable={false}
                  >
                    <IconChevronUp size={14} />
                  </ActionIcon>
                  <ActionIcon
                    variant="subtle" color="var(--neutral)" disabled={i === items.length - 1}
                    aria-label={t`Move ${label} down`}
                    onClick={() => move(i, 1)}
                    draggable={false}
                  >
                    <IconChevronDown size={14} />
                  </ActionIcon>
                </span>
                <Switch
                  size="xs"
                  checked={!disabled.includes(id)}
                  onChange={(e) => toggle(id, e.currentTarget.checked)}
                  aria-label={toggleLabel(id)}
                  // The row is draggable; without this a drag started on the switch swallows the click.
                  onMouseDown={(e) => e.stopPropagation()}
                  draggable={false}
                />
              </Group>
            </Group>
            {i < items.length - 1 && <Divider />}
          </div>
        )
      })}
    </Stack>
  )
}
