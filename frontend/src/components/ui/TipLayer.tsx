import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'

/** Gap between the target and the tooltip, and the room needed above before it flips under. */
const GAP = 8
const FLIP_THRESHOLD = 44
/** Keep the bubble this far off the viewport edges. */
const EDGE = 8

interface TipState {
  text: string
  /** Viewport x of the target's centre, where the arrow wants to point. */
  anchorX: number
  y: number
  below: boolean
}

/**
 * One delegated tooltip for the whole app: any element carrying `data-tip` gets it on hover or
 * keyboard focus, styled like Mantine's.
 *
 * Mounted once, in App. That's the entire point: a library grid renders several hundred cards,
 * and giving each one its own `<Tooltip>` meant hundreds of floating-ui instances mounted just to
 * sit idle, which is a large part of what made the grid slow. Delegating costs one listener and
 * one node no matter how many targets are on screen.
 */
export function TipLayer() {
  const [tip, setTip] = useState<TipState | null>(null)
  // Kept apart from `tip` so the bubble can fade *out* still showing its old text in its old
  // place. Clearing `tip` on hide would blank the thing mid-fade.
  const [open, setOpen] = useState(false)
  const [pos, setPos] = useState<{ left: number; arrow: number } | null>(null)
  const target = useRef<Element | null>(null)
  const bubble = useRef<HTMLDivElement>(null)

  useEffect(() => {
    const show = (el: Element) => {
      const text = el.getAttribute('data-tip')
      if (!text) return
      target.current = el
      const r = el.getBoundingClientRect()
      const below = r.top < FLIP_THRESHOLD
      setPos(null)
      setTip({
        text,
        anchorX: r.left + r.width / 2,
        y: below ? r.bottom + GAP : r.top - GAP,
        below,
      })
      setOpen(true)
    }

    const hide = () => {
      if (!target.current) return
      target.current = null
      setOpen(false)
    }

    const onOver = (e: Event) => {
      const el = (e.target as HTMLElement | null)?.closest?.('[data-tip]')
      if (!el) hide()
      else if (el !== target.current) show(el)
    }
    const onFocus = (e: Event) => {
      const el = (e.target as HTMLElement | null)?.closest?.('[data-tip]')
      if (el) show(el)
      else hide()
    }

    document.addEventListener('pointerover', onOver)
    document.addEventListener('pointerdown', hide)
    document.addEventListener('focusin', onFocus)
    document.addEventListener('focusout', hide)
    // Capture, so a scroll inside any nested scroller dismisses too rather than leaving the
    // bubble stranded next to where the target used to be.
    window.addEventListener('scroll', hide, true)
    window.addEventListener('blur', hide)

    return () => {
      document.removeEventListener('pointerover', onOver)
      document.removeEventListener('pointerdown', hide)
      document.removeEventListener('focusin', onFocus)
      document.removeEventListener('focusout', hide)
      window.removeEventListener('scroll', hide, true)
      window.removeEventListener('blur', hide)
    }
  }, [])

  // Clamp to the viewport once the bubble's real width is known: a card on the left edge of the
  // grid would otherwise centre its tooltip off-screen. The arrow keeps pointing at the target.
  useLayoutEffect(() => {
    if (!tip || !bubble.current) return
    const w = bubble.current.offsetWidth
    const left = Math.min(Math.max(tip.anchorX - w / 2, EDGE), window.innerWidth - w - EDGE)
    setPos({ left, arrow: tip.anchorX - left })
  }, [tip])

  // Rendered unconditionally rather than mounted on demand: a node that mounts already open has
  // no previous opacity to animate from, so the first tooltip of the session would pop in while
  // every later one faded. Idle it costs one empty, transparent, non-interactive div.
  return createPortal(
    <div
      ref={bubble}
      className="tip"
      role="tooltip"
      aria-hidden={!open}
      data-open={open || undefined}
      data-below={tip?.below || undefined}
      style={{
        top: tip?.y ?? -9999,
        // Rendered off-screen for the first frame so its width can be measured without a flash
        // of the bubble sitting in the wrong place.
        left: pos ? pos.left : -9999,
        transform: tip?.below ? undefined : 'translateY(-100%)',
        ...(pos ? { '--tip-arrow': `${pos.arrow}px` } : {}),
      } as React.CSSProperties}
    >
      {tip?.text}
    </div>,
    document.body,
  )
}
