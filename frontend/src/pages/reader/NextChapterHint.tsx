import { Trans } from '@lingui/react/macro'
import { IconArrowDown, IconArrowUp, IconCheck } from '../../lib/icons'

const RING_RADIUS = 15.5
const RING_LENGTH = 2 * Math.PI * RING_RADIUS

/**
 * The bottom-of-strip prompt in the vertical reader. A ring fills as the reader scrolls or pulls
 * past the end; on touch a full ring turns solid and waits for the finger to lift.
 */
export default function NextChapterHint({
  progress,
  pulling,
  opening = false,
  leaving = false,
  atEnd,
  nextLabel,
  fallbackLabel,
}: {
  progress: number
  pulling: boolean
  /** The meter filled and the next chapter is loading. */
  opening?: boolean
  /** The meter has emptied and the card is fading out. */
  leaving?: boolean
  /** Last chapter: the same card, but the ring never fills. */
  atEnd: boolean
  nextLabel?: string | null
  /** Replaces the generic wording when there's no next chapter label, as in the Discover preview. */
  fallbackLabel?: React.ReactNode
}) {
  const ready = !atEnd && (opening || (pulling && progress >= 1))
  const state = atEnd ? 'end' : ready ? 'ready' : 'filling'

  let eyebrow: React.ReactNode = null
  let title: React.ReactNode
  if (atEnd) {
    title = <Trans>No more chapters</Trans>
  } else if (opening) {
    eyebrow = nextLabel ? <Trans context="next chapter gesture">Opening</Trans> : null
    title = nextLabel || <Trans>Opening next chapter</Trans>
  } else if (nextLabel) {
    eyebrow = !pulling ? (
      <Trans context="next chapter gesture">Scroll</Trans>
    ) : ready ? (
      <Trans context="next chapter gesture">Release</Trans>
    ) : (
      <Trans context="next chapter gesture">Pull up</Trans>
    )
    title = nextLabel
  } else {
    title =
      fallbackLabel ??
      (!pulling ? (
        <Trans>Scroll for next chapter</Trans>
      ) : ready ? (
        <Trans>Release for next chapter</Trans>
      ) : (
        <Trans>Pull up for next chapter</Trans>
      ))
  }

  const Glyph = ready ? IconCheck : pulling || atEnd ? IconArrowUp : IconArrowDown
  const fill = atEnd ? 0 : opening ? 1 : Math.min(1, progress)

  return (
    <div className="reader-card reader-next-chapter-hint" data-state={state} data-leaving={leaving || undefined}>
      <span className="reader-ring" aria-hidden>
        <svg className="reader-ring-svg" viewBox="0 0 36 36">
          <circle className="reader-ring-track" cx="18" cy="18" r={RING_RADIUS} />
          <circle
            className="reader-ring-fill"
            cx="18"
            cy="18"
            r={RING_RADIUS}
            strokeDasharray={RING_LENGTH}
            strokeDashoffset={RING_LENGTH * (1 - fill)}
          />
        </svg>
        <Glyph size={16} className="reader-ring-glyph" />
      </span>
      <div className="reader-card-text">
        {eyebrow && <span className="reader-card-eyebrow">{eyebrow}</span>}
        <span className="reader-card-title">{title}</span>
      </div>
    </div>
  )
}
