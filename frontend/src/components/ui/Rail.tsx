import {
  useCallback,
  useEffect,
  useLayoutEffect,
  useRef,
  useState,
  type HTMLAttributes,
} from 'react'
import { createPortal } from 'react-dom'
import { IconChevronLeft, IconChevronRight } from '@tabler/icons-react'
import { useLingui } from '@lingui/react/macro'

const RAIL_GAP = 14
const HEADER_SEARCH_LIMIT = 8

/**
 * The `.section-header` a rail belongs to: the nearest one before it among its siblings, looking
 * through single-child wrappers (Discover's ranked wrapper) and stopping at another rail. A rail
 * with no header of its own, such as a group inside a panel, finds none and gets no arrows.
 */
function findHeader(rail: HTMLElement): HTMLElement | null {
  let node: HTMLElement | null = rail
  while (node) {
    let sibling = node.previousElementSibling
    for (let i = 0; sibling && i < HEADER_SEARCH_LIMIT; i++) {
      if (sibling.classList.contains('section-header')) return sibling as HTMLElement
      if (sibling.classList.contains('discover-rail') || sibling.querySelector('.discover-rail')) return null
      sibling = sibling.previousElementSibling
    }
    const parent: HTMLElement | null = node.parentElement
    if (!parent || parent.children.length !== 1) return null
    node = parent
  }
  return null
}

/**
 * The scroller behind every horizontal rail. It fades its edges when it overflows, and puts a pair
 * of prev/next buttons into the slot at the right end of the rail's `SectionHeader`, found by
 * position so the pages that render a header and a rail side by side need no wiring.
 */
export function Rail({
  engine,
  className,
  children,
  ...rest
}: HTMLAttributes<HTMLDivElement> & { engine?: boolean }) {
  const { t } = useLingui()
  const ref = useRef<HTMLDivElement>(null)
  const [reach, setReach] = useState({ left: false, right: false })
  const [slot, setSlot] = useState<HTMLElement | null>(null)
  const frame = useRef(0)

  const measure = useCallback(() => {
    const rail = ref.current
    if (!rail) return
    const max = rail.scrollWidth - rail.clientWidth
    const left = rail.scrollLeft > 1
    const right = max > 1 && rail.scrollLeft < max - 1
    setReach((prev) => (prev.left === left && prev.right === right ? prev : { left, right }))
  }, [])

  const schedule = useCallback(() => {
    cancelAnimationFrame(frame.current)
    frame.current = requestAnimationFrame(measure)
  }, [measure])

  // The header can mount after the rail, and a rail can be replaced under a stale slot, so the
  // lookup repeats whenever the children change or the stored slot has left the document.
  useLayoutEffect(() => {
    const rail = ref.current
    if (!rail) return
    const found = findHeader(rail)?.querySelector<HTMLElement>('.section-header-arrows') ?? null
    setSlot(found)
  }, [children, slot?.isConnected])

  // Offscreen rails are content-visibility: auto, so their scrollWidth is only real once they have
  // been laid out. Children resizing is what reports that, since the scroller's own box never changes.
  useLayoutEffect(() => {
    measure()
    const rail = ref.current
    if (!rail) return
    rail.addEventListener('scroll', schedule, { passive: true })
    rail.addEventListener('load', schedule, { capture: true, passive: true })
    window.addEventListener('resize', schedule)
    const resize = new ResizeObserver(schedule)
    resize.observe(rail)
    for (const child of Array.from(rail.children)) resize.observe(child)
    const visible = new IntersectionObserver(schedule)
    visible.observe(rail)
    return () => {
      cancelAnimationFrame(frame.current)
      rail.removeEventListener('scroll', schedule)
      rail.removeEventListener('load', schedule, { capture: true })
      window.removeEventListener('resize', schedule)
      resize.disconnect()
      visible.disconnect()
    }
  }, [measure, schedule, children])

  useEffect(() => {
    const rail = ref.current
    const header = slot?.closest<HTMLElement>('.section-header')
    if (!rail || !slot || !header) return
    const on = () => slot.setAttribute('data-hover', 'true')
    const off = () => slot.removeAttribute('data-hover')
    const targets = [rail, header]
    for (const el of targets) {
      el.addEventListener('pointerenter', on)
      el.addEventListener('pointerleave', off)
    }
    return () => {
      for (const el of targets) {
        el.removeEventListener('pointerenter', on)
        el.removeEventListener('pointerleave', off)
      }
      off()
    }
  }, [slot])

  const move = (direction: -1 | 1) => {
    const rail = ref.current
    if (!rail) return
    const first = rail.firstElementChild
    const step = (first?.getBoundingClientRect().width ?? 180) + RAIL_GAP
    const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches
    rail.scrollBy({ left: direction * step * 2, behavior: reduced ? 'auto' : 'smooth' })
  }

  const scrollable = reach.left || reach.right

  return (
    <>
      <div
        ref={ref}
        className={className ? `discover-rail ${className}` : 'discover-rail'}
        data-engine={engine || undefined}
        data-fade-start={reach.left || undefined}
        data-fade-end={reach.right || undefined}
        {...rest}
      >
        {children}
      </div>
      {slot &&
        scrollable &&
        createPortal(
          <>
            <button
              type="button"
              className="rail-arrow"
              aria-label={t`Scroll back`}
              disabled={!reach.left}
              onClick={() => move(-1)}
            >
              <IconChevronLeft size={16} stroke={2} aria-hidden />
            </button>
            <button
              type="button"
              className="rail-arrow"
              aria-label={t`Scroll forward`}
              disabled={!reach.right}
              onClick={() => move(1)}
            >
              <IconChevronRight size={16} stroke={2} aria-hidden />
            </button>
          </>,
          slot,
        )}
    </>
  )
}
