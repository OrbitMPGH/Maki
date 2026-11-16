import { useRef, useState } from 'react'
import { useLingui } from '@lingui/react/macro'
import { useStripPlace, type StripTracker } from './stripPosition'

export interface ProgressRailProps {
  tracker: StripTracker
  /** Pages in the strip, from `first` on. */
  count: number
  /** The chapter index of the strip's first page, past any credit pages left out. */
  first?: number
  /** The reader's bars are up: the rail widens, shows its handle and takes input everywhere. */
  active: boolean
  /** The thumbnail strip is open above the bottom bar, so the rail ends above it. */
  strip?: boolean
  /** Pressed or hovered: keeps the bars from hiding mid-drag. */
  onHold?: (held: boolean) => void
}

function clamp01(value: number): number {
  return Math.min(1, Math.max(0, value))
}

/**
 * The continuous strip's progress as a rail down the right edge, each page an equal share of its
 * length, so a chapter of a hundred slices and one of five tall ones read the same. With the bars
 * hidden it is a hairline that takes no touches (a mouse can still hover it open); with them up it
 * can be dragged, and the strip follows the drag.
 */
export default function ProgressRail({ tracker, count, first = 0, active, strip, onHold }: ProgressRailProps) {
  const { t } = useLingui()
  const place = useStripPlace(tracker)
  const track = useRef<HTMLDivElement>(null)
  const [drag, setDrag] = useState<number | null>(null)
  const [hover, setHover] = useState(false)

  const progress = drag ?? place?.progress ?? 0
  const page = place ? place.page - first : 0
  const shownPage = drag !== null ? Math.min(count - 1, Math.floor(drag * count)) : page

  const at = (clientY: number) => {
    const rect = track.current?.getBoundingClientRect()
    if (!rect || rect.height === 0) return progress
    return clamp01((clientY - rect.top) / rect.height)
  }
  const seek = (value: number) => tracker.seek?.(clamp01(value))

  const onKeyDown = (event: React.KeyboardEvent) => {
    const step = count > 0 ? 1 / count : 0
    const target =
      event.key === 'ArrowDown' || event.key === 'PageDown' ? progress + step
      : event.key === 'ArrowUp' || event.key === 'PageUp' ? progress - step
      : event.key === 'Home' ? 0
      : event.key === 'End' ? 1
      : null
    if (target === null) return
    // The reader's own key handler skips anything already handled, so the strip moves once.
    event.preventDefault()
    seek(target)
  }

  const release = () => {
    setDrag(null)
    if (!hover) onHold?.(false)
  }

  return (
    <div
      className="reader-rail"
      data-active={active || undefined}
      data-dragging={drag !== null || undefined}
      data-strip={strip || undefined}
      style={{ '--rail-progress': `${progress * 100}%` } as React.CSSProperties}
      role="slider"
      tabIndex={active ? 0 : -1}
      aria-label={t`Chapter progress`}
      aria-orientation="vertical"
      aria-valuemin={1}
      aria-valuemax={Math.max(1, count)}
      aria-valuenow={shownPage + 1}
      aria-valuetext={t`Page ${shownPage + 1} of ${count}`}
      onKeyDown={onKeyDown}
      onClick={(event) => event.stopPropagation()}
      onPointerEnter={(event) => {
        if (event.pointerType !== 'mouse') return
        setHover(true)
        onHold?.(true)
      }}
      onPointerLeave={(event) => {
        if (event.pointerType !== 'mouse') return
        setHover(false)
        if (drag === null) onHold?.(false)
      }}
      onPointerDown={(event) => {
        if (event.button !== 0 || count === 0) return
        event.stopPropagation()
        event.currentTarget.setPointerCapture(event.pointerId)
        onHold?.(true)
        const value = at(event.clientY)
        setDrag(value)
        seek(value)
      }}
      onPointerMove={(event) => {
        if (drag === null) return
        const value = at(event.clientY)
        setDrag(value)
        seek(value)
      }}
      onPointerUp={(event) => {
        if (drag === null) return
        seek(at(event.clientY))
        release()
      }}
      onPointerCancel={() => {
        if (drag !== null) release()
      }}
    >
      <div ref={track} className="reader-rail-track">
        <div className="reader-rail-fill" />
        <div className="reader-rail-thumb" />
        {(drag !== null || hover) && count > 0 && (
          <div className="reader-rail-bubble" aria-hidden>
            {shownPage + 1} / {count}
          </div>
        )}
      </div>
    </div>
  )
}
