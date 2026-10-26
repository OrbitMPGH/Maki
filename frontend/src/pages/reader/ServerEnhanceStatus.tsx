import { useEffect, useRef, useState } from 'react'
import { useLingui } from '@lingui/react/macro'
import { useLabel } from '../../i18n-context'
import { enhanceMethodInfo } from './enhanceMethods'
import type { EnhanceMethod } from './prefs'
import type { ServerEnhance } from './useServerEnhance'

type Phase = 'queued' | 'working' | 'loading' | 'done' | 'failed'

const SHOW_AFTER_MS = 400
const DONE_MS = 1200
const FAILED_MS = 2500

const PENDING: ReadonlySet<Phase | null> = new Set<Phase>(['queued', 'working', 'loading'])

/** Where the pages on screen stand with the server, or null when it was not asked about any of them. */
function phaseOf(server: ServerEnhance | null, pages: number[]): Phase | null {
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
 * A pill over the reader while the page on screen waits for its server upscale: queued, running,
 * then loading. It appears only when the wait outlasts a short delay, so a cached page never
 * flashes it, says so briefly once the upscale is drawn, and says so too when the server gave up.
 */
export default function ServerEnhanceStatus({
  server,
  pages,
  method,
}: {
  server: ServerEnhance | null
  pages: number[]
  method: EnhanceMethod
}) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const phase = phaseOf(server, pages)
  const [visible, setVisible] = useState<Phase | null>(null)
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

  if (!visible) return null
  const info = enhanceMethodInfo(method)
  const label = {
    queued: t`Waiting for the server`,
    working: t`Upscaling this page`,
    loading: t`Loading the upscaled page`,
    done: t`Upscaled page shown`,
    failed: t`Could not upscale this page`,
  }[visible]

  return (
    <div className="reader-enhance-status" data-phase={visible} role="status">
      <span>
        {info && <span className="reader-enhance-status-method">{renderLabel(info.label)}</span>}
        {label}
      </span>
      <div className="reader-next-chapter-bar">
        {visible !== 'failed' && <div className="reader-next-chapter-fill reader-enhance-status-fill" />}
      </div>
    </div>
  )
}
