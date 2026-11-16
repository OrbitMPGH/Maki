import { useState } from 'react'
import { Trans } from '@lingui/react/macro'
import { IconCheck, IconPlayerTrackNext } from '../../lib/icons'
import { useLinger } from './useLinger'

/** The fade out, kept in step with `--dur-medium` on `.reader-recap-skip[data-leaving]`. */
const LEAVE_MS = 250

export type RecapPhase = 'offer' | 'skipped'

/**
 * The bottom-of-strip card at a chapter's start when it opens with a recap of the previous
 * chapter: a "Skip recap" button, or after an automatic skip, a "Recap skipped" note that the
 * reader clears after a moment. Null fades the card out.
 */
export default function RecapSkip({ phase, onSkip }: { phase: RecapPhase | null; onSkip: () => void }) {
  const [last, setLast] = useState<RecapPhase>(phase ?? 'offer')
  if (phase && phase !== last) setLast(phase)
  const mounted = useLinger(phase !== null, LEAVE_MS)
  if (!mounted) return null

  const shown = phase ?? last
  const leaving = phase === null || undefined

  if (shown === 'skipped') {
    return (
      <div className="reader-card reader-recap-skip" data-state="skipped" data-leaving={leaving} role="status">
        <span className="reader-ring" aria-hidden>
          <IconCheck size={16} className="reader-ring-glyph" />
        </span>
        <span className="reader-card-title">
          <Trans>Recap skipped</Trans>
        </span>
      </div>
    )
  }

  return (
    <button
      type="button"
      className="reader-card reader-recap-skip"
      data-state="offer"
      data-leaving={leaving}
      disabled={phase === null}
      onClick={(event) => {
        event.stopPropagation()
        onSkip()
      }}
    >
      <span className="reader-ring" aria-hidden>
        <IconPlayerTrackNext size={16} className="reader-ring-glyph" />
      </span>
      <span className="reader-card-title">
        <Trans>Skip recap</Trans>
      </span>
    </button>
  )
}
