import { useEffect, useRef, useState } from 'react'
import { VisuallyHidden } from '@mantine/core'
import { useLingui } from '@lingui/react/macro'
import { useLabel } from '../../i18n-context'
import { enhanceMethodInfo } from './enhanceMethods'
import type { EnhanceMethod, EnhanceProgress } from './prefs'
import type { ServerEnhance } from './useServerEnhance'

export type EnhancePhase = 'queued' | 'working' | 'loading' | 'done' | 'failed'

const SHOW_AFTER_MS = 400
const DONE_MS = 1200
const FAILED_MS = 2500

const PENDING: ReadonlySet<EnhancePhase | null> = new Set<EnhancePhase>(['queued', 'working', 'loading'])

export function isPendingPhase(phase: EnhancePhase | null): boolean {
  return PENDING.has(phase)
}

/** Where the pages on screen stand with the server, or null when it was not asked about any of them. */
function phaseOf(server: ServerEnhance | null, pages: number[]): EnhancePhase | null {
  if (!server) return null
  const asked = pages.filter((page) => server.states.has(page))
  if (asked.length === 0) return null
  const states = asked.map((page) => server.states.get(page))
  if (states.includes('working')) return 'working'
  if (states.includes('queued')) return 'queued'
  const ready = asked.filter((page) => server.states.get(page) === 'ready')
  if (ready.some((page) => !server.shown.has(page))) return 'loading'
  return ready.length > 0 ? 'done' : 'failed'
}

/**
 * The server upscale phase worth showing for the pages on screen. A wait only shows once it outlasts
 * a short delay, so a cached page never flashes it; `done` shows briefly after a wait that was shown,
 * and `failed` briefly whenever the server gave up.
 */
export function useEnhancePhase(server: ServerEnhance | null, pages: number[]): EnhancePhase | null {
  const phase = phaseOf(server, pages)
  const [visible, setVisible] = useState<EnhancePhase | null>(null)
  const visibleRef = useRef(visible)
  visibleRef.current = visible
  const pagesKey = pages.join(',')

  useEffect(() => {
    const showing = visibleRef.current !== null
    let timer: ReturnType<typeof setTimeout> | undefined
    if (PENDING.has(phase)) {
      if (showing) setVisible(phase)
      else timer = setTimeout(() => setVisible(phase), SHOW_AFTER_MS)
    } else if (phase === 'done' && showing) {
      setVisible('done')
      timer = setTimeout(() => setVisible(null), DONE_MS)
    } else if (phase === 'failed') {
      setVisible('failed')
      timer = setTimeout(() => setVisible(null), FAILED_MS)
    } else {
      setVisible(null)
    }
    return () => clearTimeout(timer)
  }, [phase, pagesKey])

  return visible
}

export function useEnhancePhaseLabel() {
  const { t } = useLingui()
  return (phase: EnhancePhase) =>
    ({
      queued: t`Waiting for the server`,
      working: t`Upscaling this page`,
      loading: t`Loading the upscaled page`,
      done: t`Upscaled page shown`,
      failed: t`Could not upscale this page`,
    })[phase]
}

/**
 * The server upscale's progress over the page while the bars are hidden: a thin line on the
 * screen's bottom edge, the labelled pill, or nothing, as `style` says. With the bars up the toolbar
 * shows it instead. Screen readers hear the phase in every style.
 */
export default function ServerEnhanceStatus({
  phase,
  method,
  style,
  chrome,
  rtl,
}: {
  phase: EnhancePhase | null
  method: EnhanceMethod
  style: EnhanceProgress
  chrome: boolean
  rtl: boolean
}) {
  const renderLabel = useLabel()
  const labelOf = useEnhancePhaseLabel()
  const label = phase ? labelOf(phase) : null
  const info = enhanceMethodInfo(method)

  return (
    <>
      <VisuallyHidden role="status">{label}</VisuallyHidden>
      {phase && !chrome && style === 'line' && (
        <div className="reader-enhance-line" data-phase={phase} data-rtl={rtl || undefined} aria-hidden>
          <div className="reader-enhance-line-fill" />
        </div>
      )}
      {phase && !chrome && style === 'detailed' && (
        <div className="reader-enhance-status" data-phase={phase} aria-hidden>
          <span>
            {info && <span className="reader-enhance-status-method">{renderLabel(info.label)}</span>}
            {label}
          </span>
          <div className="reader-enhance-status-bar">
            {phase !== 'failed' && <div className="reader-enhance-status-fill" />}
          </div>
        </div>
      )}
    </>
  )
}
