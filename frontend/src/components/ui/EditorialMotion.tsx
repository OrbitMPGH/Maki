import { useGSAP } from '@gsap/react'
import { useReducedMotion } from '@mantine/hooks'
import type { ReactNode } from 'react'
import { useRef } from 'react'
import { gsap } from 'gsap'
import { ScrollTrigger } from 'gsap/ScrollTrigger'

gsap.registerPlugin(ScrollTrigger, useGSAP)

export type EditorialMotionMode = 'image-scale' | 'text-scrub'

type EditorialMotionProps = {
  mode: EditorialMotionMode
  children?: ReactNode
  text?: string
  className?: string
}

/**
 * Small, opt-in GSAP surface for meaningful editorial moments. It stays inert
 * when the user prefers reduced motion, so route components do not need to
 * repeat that accessibility guard.
 */
export function EditorialMotion({ mode, children, text, className }: EditorialMotionProps) {
  const root = useRef<HTMLDivElement>(null)
  const reducedMotion = useReducedMotion()

  useGSAP(
    () => {
      if (reducedMotion || !root.current) return

      if (mode === 'image-scale') {
        const target =
          root.current.querySelector<HTMLElement>('[data-editorial-motion-target]') ??
          root.current.querySelector<HTMLElement>('img') ??
          root.current.firstElementChild

        if (!target) return

        const timeline = gsap.timeline({
          scrollTrigger: {
            trigger: root.current,
            start: 'top 92%',
            end: 'bottom 10%',
            scrub: true,
          },
        })

        timeline
          .fromTo(
            target,
            { scale: 0.8, opacity: 0.2, filter: 'brightness(0.72)' },
            { scale: 1, opacity: 1, filter: 'brightness(1)', duration: 0.45, ease: 'none' },
          )
          .to(target, { scale: 0.86, opacity: 0.2, filter: 'brightness(0.72)', duration: 0.55, ease: 'none' })
      }

      if (mode === 'text-scrub') {
        const words = root.current.querySelectorAll<HTMLElement>('[data-editorial-word]')
        if (!words.length) return

        gsap.fromTo(
          words,
          { opacity: 0.12 },
          {
            opacity: 1,
            duration: 1,
            stagger: 0.08,
            ease: 'none',
            scrollTrigger: {
              trigger: root.current,
              start: 'top 82%',
              end: 'bottom 35%',
              scrub: true,
            },
          },
        )
      }
    },
    { dependencies: [mode, reducedMotion, text], scope: root },
  )

  const classes = ['editorial-motion', `editorial-motion--${mode}`, className].filter(Boolean).join(' ')

  if (mode === 'text-scrub') {
    const words = (text ?? '').trim().split(/\s+/).filter(Boolean)

    return (
      <div ref={root} className={classes} data-editorial-motion={mode} data-reduced-motion={reducedMotion}>
        {words.map((word, index) => (
          <span className="editorial-motion__word" data-editorial-word key={`${word}-${index}`}>
            {word}
            {index < words.length - 1 ? ' ' : ''}
          </span>
        ))}
      </div>
    )
  }

  return (
    <div ref={root} className={classes} data-editorial-motion={mode} data-reduced-motion={reducedMotion}>
      {children}
    </div>
  )
}
