import { forwardRef, useEffect, useImperativeHandle, useRef } from 'react'
import { AnimatePresence } from 'motion/react'
import type { QueueCard, SwipeKind } from '../../../api/discoverQueue'
import { SwipeCard, type SwipeCardHandle } from './SwipeCard'

const VISIBLE = 3
const PRELOAD = 2

export interface CardStackHandle {
  /** Plays the top card's commit animation, for the action bar's buttons. */
  commitTop: (kind: SwipeKind) => Promise<void>
}

/**
 * The top three cards of the deck, stacked. Only the front one is draggable; the rest sit inert
 * behind it (`pointer-events: none`, set by `SwipeCard` itself).
 */
export const CardStack = forwardRef<CardStackHandle, {
  cards: QueueCard[]
  onSwipe: (card: QueueCard, kind: SwipeKind) => void
  onOpen: (card: QueueCard) => void
}>(function CardStack({ cards, onSwipe, onOpen }, ref) {
  const visible = cards.slice(0, VISIBLE)
  const topHandleRef = useRef<SwipeCardHandle | null>(null)

  useImperativeHandle(ref, () => ({
    commitTop: async (kind) => {
      await topHandleRef.current?.commit(kind)
    },
  }), [])

  // Preloads the covers of the next two cards so a swipe never reveals a blank card behind it.
  useEffect(() => {
    const upcoming = cards.slice(1, 1 + PRELOAD)
    const images = upcoming.map((c) => {
      const src = c.coverUrl ?? c.thumbUrlHiDpi ?? c.thumbUrl
      if (!src) return null
      const img = new Image()
      img.src = src
      return img
    })
    return () => { for (const img of images) if (img) img.src = '' }
  }, [cards])

  return (
    <div className="queue-stack">
      <AnimatePresence>
        {visible.map((card, i) => (
          <SwipeCard
            key={card.providerId}
            ref={i === 0 ? topHandleRef : undefined}
            card={card}
            isTop={i === 0}
            onSwipe={(kind) => onSwipe(card, kind)}
            onOpen={() => onOpen(card)}
          />
        ))}
      </AnimatePresence>
    </div>
  )
})
