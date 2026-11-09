import { AnimatePresence, motion, useIsPresent, useReducedMotion } from 'motion/react'
import { useLayoutEffect, useRef, useState, type ReactNode, type Ref } from 'react'
import { DUR, EASE, paced } from '../../lib/motion'

const SHIFT = 24

function Pane({
  ref,
  dir,
  still,
  children,
}: {
  ref?: Ref<HTMLDivElement>
  dir: number
  still: boolean
  children: ReactNode
}) {
  // The page sliding out stays mounted for the animation; inert keeps focus and arrow keys off it.
  const present = useIsPresent()
  const slide = { duration: paced(DUR.slide), ease: EASE }
  return (
    <motion.div
      ref={ref}
      data-drill-pane=""
      inert={!present}
      aria-hidden={!present || undefined}
      custom={dir}
      variants={{
        enter: (d: number) => ({ x: `${d * SHIFT}%`, opacity: 0 }),
        // The leaving page fades early so the two never read as one jumble of text.
        center: {
          x: 0,
          opacity: 1,
          transition: still ? { duration: 0 } : { x: slide, opacity: { duration: paced(DUR.medium), delay: paced(0.05) } },
        },
        exit: (d: number) => ({
          x: `${d * -SHIFT}%`,
          opacity: 0,
          transition: still ? { duration: 0 } : { x: slide, opacity: { duration: paced(DUR.fast) } },
        }),
      }}
      initial="enter"
      animate="center"
      exit="exit"
    >
      {children}
    </motion.div>
  )
}

/**
 * The pages of a drill-down menu or popover. A deeper page slides in from the right, a shallower
 * one from the left, and the box eases to the new page's height. `bleed` widens the clip past the
 * content so focus outlines on its edge are not cut off; keep it within the container's padding.
 */
export function DrillPanes({
  page,
  depth,
  bleed = 0,
  children,
}: {
  page: string
  depth: number
  bleed?: number
  children: ReactNode
}) {
  const reduce = useReducedMotion() ?? false
  const [last, setLast] = useState({ page, depth, dir: 1 })
  if (last.page !== page) setLast({ page, depth, dir: depth >= last.depth ? 1 : -1 })

  const innerRef = useRef<HTMLDivElement>(null)
  const [height, setHeight] = useState<number>()
  useLayoutEffect(() => {
    const inner = innerRef.current
    if (!inner) return
    const observer = new ResizeObserver(() => setHeight(inner.offsetHeight))
    observer.observe(inner)
    return () => observer.disconnect()
  }, [])

  return (
    <motion.div
      initial={false}
      animate={{ height: height === undefined ? 'auto' : height + bleed * 2 }}
      transition={{ duration: reduce ? 0 : paced(DUR.slide), ease: EASE }}
      style={{ overflow: 'clip', margin: -bleed, padding: bleed }}
    >
      <div ref={innerRef} style={{ position: 'relative' }}>
        <AnimatePresence mode="popLayout" initial={false} custom={last.dir}>
          <Pane key={page} dir={last.dir} still={reduce}>
            {children}
          </Pane>
        </AnimatePresence>
      </div>
    </motion.div>
  )
}
