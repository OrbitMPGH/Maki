import { useEffect, useRef, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { UnstyledButton } from '@mantine/core'
import { useLingui } from '@lingui/react/macro'
import { useLabel } from '../../i18n-context'
import {
  STATS_SECTIONS,
  isStatsSectionKey,
  sectionElementId,
  type StatsSectionKey,
} from './sectionList'

/**
 * The strip of section links pinned under the app header. Highlights whichever section is in the
 * upper part of the viewport. A click writes `?section=` so the link can be shared; scrolling
 * never does, or the back button would step through sections instead of leaving the page.
 */
export function StatsRail() {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const [searchParams, setSearchParams] = useSearchParams()
  const [active, setActive] = useState<StatsSectionKey>(() => {
    const requested = searchParams.get('section')
    return isStatsSectionKey(requested) ? requested : STATS_SECTIONS[0].key
  })
  const railRef = useRef<HTMLElement>(null)

  // Deep link: jump once on mount. Panels above are still loading and grow as they fill in, so
  // it jumps again shortly after, unless the reader has started scrolling on their own by then.
  useEffect(() => {
    const requested = searchParams.get('section')
    if (!isStatsSectionKey(requested)) return
    const jump = () => document.getElementById(sectionElementId(requested))?.scrollIntoView({ block: 'start' })
    jump()
    let userMoved = false
    const stop = () => {
      userMoved = true
    }
    const opts = { passive: true, once: true } as const
    window.addEventListener('wheel', stop, opts)
    window.addEventListener('touchstart', stop, opts)
    window.addEventListener('keydown', stop, opts)
    const timer = window.setTimeout(() => {
      if (!userMoved) jump()
    }, 700)
    return () => {
      window.clearTimeout(timer)
      window.removeEventListener('wheel', stop)
      window.removeEventListener('touchstart', stop)
      window.removeEventListener('keydown', stop)
    }
    // Mount only: later changes to the query string come from this rail's own clicks.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  useEffect(() => {
    const elements = STATS_SECTIONS.map((s) => document.getElementById(sectionElementId(s.key))).filter(
      (el): el is HTMLElement => el !== null,
    )
    const visible = new Set<string>()
    const observer = new IntersectionObserver(
      (records) => {
        for (const r of records) {
          const key = r.target.id.slice('stats-'.length)
          if (r.isIntersecting) visible.add(key)
          else visible.delete(key)
        }
        const first = STATS_SECTIONS.find((s) => visible.has(s.key))
        if (first) setActive(first.key)
      },
      // A band across the upper part of the screen, below the app header and this rail.
      { rootMargin: '-120px 0px -55% 0px' },
    )
    elements.forEach((el) => observer.observe(el))
    return () => observer.disconnect()
  }, [])

  // Keep the active link in view on a phone, where the strip scrolls sideways.
  useEffect(() => {
    const rail = railRef.current
    const link = rail?.querySelector<HTMLElement>(`[data-section="${active}"]`)
    if (!rail || !link) return
    // The rail is sticky, so it is the link's offset parent.
    const left = link.offsetLeft
    if (left < rail.scrollLeft || left + link.offsetWidth > rail.scrollLeft + rail.clientWidth) {
      rail.scrollTo({ left: Math.max(0, left - 16) })
    }
  }, [active])

  return (
    <nav ref={railRef} className="stats-rail" aria-label={t`Stats sections`}>
      {STATS_SECTIONS.map((s) => {
        const SectionIcon = s.icon
        const isActive = active === s.key
        return (
          <UnstyledButton
            key={s.key}
            data-section={s.key}
            className={isActive ? 'stats-rail-link is-active' : 'stats-rail-link'}
            aria-current={isActive ? 'location' : undefined}
            onClick={() => {
              document
                .getElementById(sectionElementId(s.key))
                ?.scrollIntoView({ block: 'start', behavior: 'smooth' })
              setActive(s.key)
              setSearchParams(
                (prev) => {
                  const next = new URLSearchParams(prev)
                  next.set('section', s.key)
                  return next
                },
                { replace: true },
              )
            }}
          >
            <SectionIcon size={15} stroke={1.8} aria-hidden />
            {renderLabel(s.label)}
            {s.railChip && <span className="stats-rail-chip">{renderLabel(s.railChip)}</span>}
          </UnstyledButton>
        )
      })}
    </nav>
  )
}
