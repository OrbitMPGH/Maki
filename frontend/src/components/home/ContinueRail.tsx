import { useCallback, useEffect, useRef, useState } from 'react'
import { ActionIcon, Group } from '@mantine/core'
import { IconChevronLeft, IconChevronRight } from '@tabler/icons-react'
import { useLingui } from '@lingui/react/macro'
import type { HomeReadingItem } from '../../api/hooks'
import { ReadingRail } from './ReadingRail'

/**
 * The spill-over rail under {@link ContinueLead}: the same `ReadingRail` with scroll-aware
 * prev/next arrows above it.
 *
 * The arrows wrap the rail rather than living inside it, so every other `ReadingRail` caller
 * (Home's Jump back in) is untouched. The scroller is found by class instead of by ref for the
 * same reason.
 */
export function ContinueRail({ items }: { items: HomeReadingItem[] }) {
  const { t } = useLingui()
  const shellRef = useRef<HTMLDivElement>(null)
  // A rail that fits gets no arrows at all, and an arrow at the end of its travel is disabled
  // rather than a button that silently does nothing. Sub-pixel scroll positions make the far end
  // land a fraction short of the arithmetic, hence the 1px slack.
  const [reach, setReach] = useState({ left: false, right: false })

  const railOf = () => shellRef.current?.querySelector<HTMLElement>('.discover-rail') ?? null

  const measure = useCallback(() => {
    const rail = shellRef.current?.querySelector<HTMLElement>('.discover-rail')
    if (!rail) return
    const max = rail.scrollWidth - rail.clientWidth
    const left = rail.scrollLeft > 1
    const right = max > 1 && rail.scrollLeft < max - 1
    setReach((prev) => (prev.left === left && prev.right === right ? prev : { left, right }))
  }, [])

  useEffect(() => {
    const rail = shellRef.current?.querySelector<HTMLElement>('.discover-rail')
    if (!rail) return
    measure()
    rail.addEventListener('scroll', measure, { passive: true })
    // Covers both the container resizing and covers loading in and changing the content width.
    const observer = new ResizeObserver(measure)
    observer.observe(rail)
    const content = rail.firstElementChild
    if (content) observer.observe(content)
    // Covers loading changes the scroller's content width without resizing the scroller's own
    // box, so ResizeObserver alone misses it; catch image loads in the capture phase instead.
    rail.addEventListener('load', measure, { capture: true, passive: true })
    return () => {
      rail.removeEventListener('scroll', measure)
      rail.removeEventListener('load', measure, { capture: true })
      observer.disconnect()
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps -- items is a fresh array each render; length is what matters
  }, [measure, items.length])

  const move = (direction: -1 | 1) => {
    const rail = railOf()
    const first = rail?.querySelector<HTMLElement>('.discover-rail-item')
    const distance = (first?.getBoundingClientRect().width ?? 180) + 12
    const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches
    rail?.scrollBy({ left: direction * distance * 2, behavior: reduced ? 'auto' : 'smooth' })
  }

  const scrollable = reach.left || reach.right

  return (
    <div className="reading-rail-shell reading-rail-shell--carousel" ref={shellRef}>
      {scrollable && (
        <Group className="reading-rail-controls" justify="flex-end" gap="xs">
          <ActionIcon
            variant="subtle"
            color="var(--neutral)"
            aria-label={t`Show earlier chapters`}
            disabled={!reach.left}
            onClick={() => move(-1)}
          >
            <IconChevronLeft size={17} />
          </ActionIcon>
          <ActionIcon
            variant="subtle"
            color="var(--neutral)"
            aria-label={t`Show later chapters`}
            disabled={!reach.right}
            onClick={() => move(1)}
          >
            <IconChevronRight size={17} />
          </ActionIcon>
        </Group>
      )}
      <ReadingRail items={items} />
    </div>
  )
}
