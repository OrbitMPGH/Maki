import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { ActionIcon } from '@mantine/core'
import { IconPlayerPauseFilled, IconPlayerPlayFilled, IconX } from '@tabler/icons-react'
import { AnimatePresence, motion, useReducedMotion } from 'motion/react'
import { useLingui } from '@lingui/react/macro'
import type { ActivityStats } from '../../api/hooks'
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
  stats: ActivityStats
  label: string
  onClose: () => void
}) {
  const { t, i18n } = useLingui()
  const reduced = useReducedMotion()
  // `i18n.locale` is not read directly here, but `buildSlides` renders translated markup, so a
  // language switch would otherwise leave the deck frozen in whatever language built it.
  const slides = useMemo(() => buildSlides(stats, label), [stats, label, i18n.locale])
  const [index, setIndex] = useState(0)
  const [paused, setPaused] = useState(false)
  // Time already spent on the current slide, so a resume finishes the slide instead of restarting it.
  const elapsed = useRef(0)
  const overlayRef = useRef<HTMLDivElement>(null)
  const pauseRef = useRef<HTMLButtonElement>(null)
  const closeRef = useRef<HTMLButtonElement>(null)
  const isLast = index === slides.length - 1

  const next = useCallback(() => {
    setIndex((i) => Math.min(i + 1, slides.length - 1))
  }, [slides.length])
  const prev = useCallback(() => setIndex((i) => Math.max(i - 1, 0)), [])

  // Auto-advance; the last slide stays until dismissed. Declared before the timer so a new slide
  // starts from zero after the old slide's cleanup has added its time.
  useEffect(() => {
    elapsed.current = 0
  }, [index])
  useEffect(() => {
    if (isLast || paused) return
    const started = performance.now()
    const timer = setTimeout(next, Math.max(0, SLIDE_MS - elapsed.current))
    return () => {
      clearTimeout(timer)
      elapsed.current += performance.now() - started
    }
  }, [index, isLast, next, paused])

  // Focus moves into the overlay while it is open and goes back to the Play button afterwards.
  useEffect(() => {
    const opener = document.activeElement instanceof HTMLElement ? document.activeElement : null
    overlayRef.current?.focus()
    return () => opener?.focus()
  }, [])

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        onClose()
      } else if (e.key === 'Tab') {
        // Nothing behind the overlay is reachable: Tab cycles the overlay and its two buttons.
        e.preventDefault()
        const stops = [overlayRef.current, pauseRef.current, closeRef.current].filter((s) => s !== null)
        const at = stops.indexOf(document.activeElement as HTMLDivElement | HTMLButtonElement)
        stops[(at + (e.shiftKey ? stops.length - 1 : 1)) % stops.length]?.focus()
      } else if (e.key === ' ' && e.target instanceof HTMLButtonElement) {
        // Space on the focused close button closes; it should not also advance the slide.
      } else if (e.key === 'ArrowRight' || e.key === ' ') {
        e.preventDefault()
        next()
      } else if (e.key === 'ArrowLeft') {
        e.preventDefault()
        prev()
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose, next, prev])

  // Lock page scroll while the overlay is up. In the installed iOS app the shell's <main> is the
  // scroller (lib/pageScroll.ts), so it is locked too.
  useEffect(() => {
    const previous = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    const inner =
      document.documentElement.dataset.scroll === 'inner' ? document.getElementById('main') : null
    const previousInner = inner?.style.overflow ?? ''
    if (inner) inner.style.overflow = 'hidden'
    return () => {
      document.body.style.overflow = previous
      if (inner) inner.style.overflow = previousInner
    }
  }, [])

  const onTap = (e: React.MouseEvent<HTMLDivElement>) => {
    const { left, width } = e.currentTarget.getBoundingClientRect()
    const inLeftThird = (e.clientX - left) / width < 1 / 3
    if (inLeftThird) prev()
    else if (isLast) onClose()
    else next()
  }

  const slide = slides[index]
  // Keyed by what is drawn rather than by slide, so two slides on the same cover hold it instead of
  // flashing it out and back in.
  const backdropKey = slide.mosaic ? 'mosaic' : (slide.backdrop ?? 'none')

  return createPortal(
    <div
      ref={overlayRef}
      className="rewind-overlay"
      role="dialog"
      aria-modal="true"
      aria-label={t`${label} Rewind`}
      tabIndex={-1}
      style={{ outline: 'none' }}
      onClick={onTap}
    >
      <AnimatePresence>
        {backdropKey !== 'none' && (
          <motion.div
            key={backdropKey}
            className="rewind-stage"
            aria-hidden
            initial={{ opacity: 0 }}
            animate={{ opacity: 1 }}
            exit={{ opacity: 0 }}
            transition={{ duration: reduced ? 0 : 0.9, ease: 'easeOut' }}
          >
            {slide.mosaic ? (
              <motion.div
                className="rewind-mosaic"
                initial={reduced ? false : { y: 0 }}
                animate={reduced ? undefined : { y: -120 }}
                transition={{ duration: SLIDE_MS / 1000 + 2, ease: 'linear' }}
              >
                {Array.from({ length: 54 }, (_, i) => (
                  <img key={i} src={slide.mosaic![i % slide.mosaic!.length]} alt="" />
                ))}
              </motion.div>
            ) : (
              <motion.div
                className="rewind-backdrop"
                style={{ backgroundImage: `url("${slide.backdrop}")` }}
                initial={reduced ? false : { scale: 1.12 }}
                animate={{ scale: 1 }}
                transition={{ duration: SLIDE_MS / 1000 + 1, ease: 'easeOut' }}
              />
            )}
            <div className="rewind-scrim" />
          </motion.div>
        )}
      </AnimatePresence>

      <div className="rewind-progress" aria-hidden>
        {slides.map((s, i) => (
          <div key={s.key} className="rewind-progress-track">
            {i < index && <div className="rewind-progress-fill" style={{ width: '100%' }} />}
            {i === index &&
              (reduced || isLast ? (
                <div className="rewind-progress-fill" style={{ width: '100%' }} />
              ) : (
                <div
                  key={index}
                  className="rewind-progress-fill rewind-progress-timed"
                  style={{ animationDuration: `${SLIDE_MS}ms`, animationPlayState: paused ? 'paused' : 'running' }}
                />
              ))}
          </div>
        ))}
      </div>

      <div className="rewind-controls">
        {!isLast && (
          <ActionIcon
            ref={pauseRef}
            variant="subtle"
            color="gray.0"
            size="lg"
            aria-label={paused ? t`Resume Rewind` : t`Pause Rewind`}
            aria-pressed={paused}
            onClick={(e) => {
              e.stopPropagation()
              setPaused((p) => !p)
            }}
          >
            {paused ? <IconPlayerPlayFilled size={18} /> : <IconPlayerPauseFilled size={18} />}
          </ActionIcon>
        )}
        <ActionIcon
          ref={closeRef}
          variant="subtle"
          color="gray.0"
          size="lg"
          aria-label={t`Close Rewind`}
          onClick={(e) => {
            e.stopPropagation()
            onClose()
          }}
        >
          <IconX size={22} />
        </ActionIcon>
      </div>

      <AnimatePresence mode="wait">
        <motion.div
          key={slide.key}
          className="rewind-slide"
          initial={reduced ? false : { opacity: 0, y: 40 }}
          animate={{ opacity: 1, y: 0 }}
          exit={reduced ? undefined : { opacity: 0, y: -40 }}
          transition={{ duration: 0.4, ease: 'easeOut' }}
        >
          {slide.node}
        </motion.div>
      </AnimatePresence>
    </div>,
    document.body,
  )
}
