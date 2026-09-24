import { forwardRef, useImperativeHandle, useRef } from 'react'
import {
  animate, motion, useDragControls, useMotionValue, useReducedMotion, useTransform, type PanInfo,
} from 'motion/react'
import { Text } from '@mantine/core'
import { IconStar } from '@tabler/icons-react'
import { Trans, useLingui } from '@lingui/react/macro'
import type { QueueCard, SwipeKind } from '../../../api/discoverQueue'
import { queueWhy } from './queueWhy'

const COMMIT_DISTANCE = 120
const COMMIT_VELOCITY = 500
const EDGE_GUARD = 24
const FLING_DISTANCE = 700

export interface SwipeCardHandle {
  /** Plays the same commit animation a drag release would, for a button-triggered swipe. */
  commit: (kind: SwipeKind) => Promise<void>
}

/**
 * One card in the stack: cover, meta, why line, synopsis, matched tags, and (on the top card) the
 * drag gesture itself. See design 4.4.
 */
export const SwipeCard = forwardRef<SwipeCardHandle, {
  card: QueueCard
  isTop: boolean
  onSwipe: (kind: SwipeKind) => void
  onOpen: () => void
}>(function SwipeCard({ card, isTop, onSwipe, onOpen }, ref) {
  const { t } = useLingui()
  const reducedMotion = useReducedMotion()
  const x = useMotionValue(0)
  const y = useMotionValue(0)
  const opacity = useMotionValue(1)
  const rotate = useTransform(x, (v) => (reducedMotion ? 0 : v / 20))
  const wantOpacity = useTransform(x, [0, COMMIT_DISTANCE], [0, 1])
  const skipOpacity = useTransform(x, [-COMMIT_DISTANCE, 0], [1, 0])
  const notForMeOpacity = useTransform(y, [0, COMMIT_DISTANCE], [0, 1])
  const dragControls = useDragControls()
  const committing = useRef(false)

  const commit = async (kind: SwipeKind) => {
    if (committing.current) return
    committing.current = true
    if (reducedMotion) {
      await animate(opacity, 0, { duration: 0.15 })
    } else {
      const target =
        kind === 'notForMe' ? { x: 0, y: FLING_DISTANCE } : kind === 'want' ? { x: FLING_DISTANCE, y: -60 } : { x: -FLING_DISTANCE, y: -60 }
      await Promise.all([
        animate(x, target.x, { duration: 0.28, ease: 'easeIn' }),
        animate(y, target.y, { duration: 0.28, ease: 'easeIn' }),
      ])
    }
    onSwipe(kind)
    committing.current = false
  }

  useImperativeHandle(ref, () => ({ commit }))

  const cancelDrag = () => {
    if (reducedMotion) {
      x.set(0)
      y.set(0)
    } else {
      animate(x, 0, { type: 'spring', stiffness: 320, damping: 28 })
      animate(y, 0, { type: 'spring', stiffness: 320, damping: 28 })
    }
  }

  // Starting a drag from within the edge guard is left to the browser/OS, so iOS's own back-swipe
  // still wins there: `dragListener={false}` below means motion never starts a drag on its own,
  // only when this starts it, and it only does that outside the guard.
  const handlePointerDown = (e: React.PointerEvent) => {
    if (!isTop) return
    const nearEdge = e.clientX < EDGE_GUARD || e.clientX > window.innerWidth - EDGE_GUARD
    if (nearEdge) return
    dragControls.start(e)
  }

  const handleDragEnd = (_event: PointerEvent | MouseEvent | TouchEvent, info: PanInfo) => {
    const { offset, velocity } = info
    if (Math.abs(offset.y) > Math.abs(offset.x) && (offset.y > COMMIT_DISTANCE || velocity.y > COMMIT_VELOCITY)) {
      void commit('notForMe')
      return
    }
    if (offset.x > COMMIT_DISTANCE || velocity.x > COMMIT_VELOCITY) {
      void commit('want')
      return
    }
    if (offset.x < -COMMIT_DISTANCE || velocity.x < -COMMIT_VELOCITY) {
      void commit('skip')
      return
    }
    cancelDrag()
  }

  const cover = card.coverUrl ?? card.thumbUrlHiDpi ?? card.thumbUrl
  const { Glyph, text } = queueWhy(card)
  const chips = [...card.matchedTags, ...card.matchedGenres].slice(0, 4)

  return (
    <motion.div
      className="queue-card"
      data-top={isTop || undefined}
      style={{ x, y, rotate, opacity, touchAction: isTop ? 'none' : undefined, pointerEvents: isTop ? undefined : 'none' }}
      drag={isTop}
      dragListener={false}
      dragControls={dragControls}
      dragElastic={0.7}
      dragMomentum={false}
      onPointerDown={handlePointerDown}
      onDragEnd={handleDragEnd}
    >
      <button type="button" className="queue-card-action" aria-label={t`View ${card.title}`} onClick={onOpen} />

      {cover ? (
        <img src={cover} alt={card.title} className="queue-card-cover" loading="lazy" decoding="async" />
      ) : (
        <div className="queue-card-placeholder">{card.title}</div>
      )}
      <div className="queue-card-scrim" />

      {isTop && (
        <>
          <motion.div className="queue-overlay queue-overlay-want" style={{ opacity: wantOpacity }}>
            <Trans>WANT</Trans>
          </motion.div>
          <motion.div className="queue-overlay queue-overlay-skip" style={{ opacity: skipOpacity }}>
            <Trans>SKIP</Trans>
          </motion.div>
          <motion.div className="queue-overlay queue-overlay-not-for-me" style={{ opacity: notForMeOpacity }}>
            <Trans>NOT FOR ME</Trans>
          </motion.div>
        </>
      )}

      <div className="queue-card-body">
        <Text className="queue-card-title" fw={700} lineClamp={2}>{card.title}</Text>
        <div className="queue-card-meta">
          {card.year && <span className="tnum">{card.year}</span>}
          <span>· {card.status}</span>
          {card.totalChapters && (
            <span>
              · <Trans>{card.totalChapters} ch</Trans>
            </span>
          )}
          {card.rating != null && (
            <span className="queue-card-rating">
              <IconStar size={12} style={{ color: 'var(--rating)' }} />
              {(card.rating / 10).toFixed(1)}
            </span>
          )}
        </div>
        <div className="queue-card-why">
          <Glyph size={14} />
          <span>{text}</span>
        </div>
        {card.description && (
          <Text className="queue-card-synopsis" size="sm" lineClamp={2}>{card.description}</Text>
        )}
        {chips.length > 0 && (
          <div className="queue-card-chips">
            {chips.map((c) => (
              <span key={c} className="queue-card-chip">{c}</span>
            ))}
          </div>
        )}
      </div>
    </motion.div>
  )
})
