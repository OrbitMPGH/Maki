import { useCallback, useEffect, useMemo, useState } from 'react'
import { createPortal } from 'react-dom'
import { ActionIcon } from '@mantine/core'
import { IconX } from '@tabler/icons-react'
import { AnimatePresence, motion, useReducedMotion } from 'motion/react'
import type { RewindStats } from '../../api/hooks'
import { buildSlides } from './slides'

const SLIDE_MS = 6000

/**
 * Fullscreen story-style overlay (Spotify-Wrapped feel): segmented progress bar,
 * auto-advance, tap/arrow navigation, Esc or the close button to leave. Rendered
 * through a portal so it sits above the AppShell.
 */
export function RewindIntro({
  stats,
  label,
  onClose,
}: {
  stats: RewindStats
  label: string
  onClose: () => void
}) {
  const reduced = useReducedMotion()
  const slides = useMemo(() => buildSlides(stats, label), [stats, label])
  const [index, setIndex] = useState(0)
  const isLast = index === slides.length - 1

  const next = useCallback(() => {
    setIndex((i) => Math.min(i + 1, slides.length - 1))
  }, [slides.length])
  const prev = useCallback(() => setIndex((i) => Math.max(i - 1, 0)), [])

  // Auto-advance; the last slide stays until dismissed.
  useEffect(() => {
    if (isLast) return
    const timer = setTimeout(next, SLIDE_MS)
    return () => clearTimeout(timer)
  }, [index, isLast, next])

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose()
      else if (e.key === 'ArrowRight' || e.key === ' ') next()
      else if (e.key === 'ArrowLeft') prev()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose, next, prev])

  // Lock page scroll while the overlay is up.
  useEffect(() => {
    const previous = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    return () => {
      document.body.style.overflow = previous
    }
  }, [])

  const onTap = (e: React.MouseEvent<HTMLDivElement>) => {
    const { left, width } = e.currentTarget.getBoundingClientRect()
    const inLeftThird = (e.clientX - left) / width < 1 / 3
    if (inLeftThird) prev()
    else if (isLast) onClose()
    else next()
  }

  return createPortal(
    <div className="rewind-overlay" role="dialog" aria-label={`${label} Rewind`} onClick={onTap}>
      <div className="rewind-progress" aria-hidden>
        {slides.map((s, i) => (
          <div key={s.key} className="rewind-progress-track">
            {i < index && <div className="rewind-progress-fill" style={{ width: '100%' }} />}
            {i === index &&
              (reduced || isLast ? (
                <div className="rewind-progress-fill" style={{ width: '100%' }} />
              ) : (
                <motion.div
                  key={index}
                  className="rewind-progress-fill"
                  initial={{ width: '0%' }}
                  animate={{ width: '100%' }}
                  transition={{ duration: SLIDE_MS / 1000, ease: 'linear' }}
                />
              ))}
          </div>
        ))}
      </div>

      <ActionIcon
        className="rewind-close"
        variant="subtle"
        color="gray.0"
        size="lg"
        aria-label="Close Rewind"
        onClick={(e) => {
          e.stopPropagation()
          onClose()
        }}
      >
        <IconX size={22} />
      </ActionIcon>

      <AnimatePresence mode="wait">
        <motion.div
          key={slides[index].key}
          className="rewind-slide"
          initial={reduced ? false : { opacity: 0, y: 40 }}
          animate={{ opacity: 1, y: 0 }}
          exit={reduced ? undefined : { opacity: 0, y: -40 }}
          transition={{ duration: 0.4, ease: 'easeOut' }}
        >
          {slides[index].node}
        </motion.div>
      </AnimatePresence>
    </div>,
    document.body,
  )
}
