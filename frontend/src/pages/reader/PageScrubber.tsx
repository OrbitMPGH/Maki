import { useRef, useState } from 'react'
import { useElementSize } from '@mantine/hooks'
import { useLingui } from '@lingui/react/macro'

/** Below this many pixels per page the segments touch instead of being split by gaps. */
const MIN_SEGMENT_WITH_GAP = 6
const PREVIEW_HALF_WIDTH = 64

export interface PageScrubberProps {
  count: number
  /** The first page on screen. */
  page: number
  /** The last page on screen; every segment up to it is lit. */
  shownTo: number
  rtl: boolean
  onSeek: (page: number) => void
  /** Pages the server has upscaled; the ones ahead are marked like a video's buffer. */
  enhanced?: ReadonlySet<number>
  /** Thumbnail URL per page, shown above the segment under the pointer. */
  thumbs: string[]
}

/**
 * The chapter as a row of segments, one per page, lit up to the page on screen. Hovering shows a
 * page's thumbnail; pressing and dragging scrubs, and the page changes on release. In right-to-left
 * reading page 1 is the rightmost segment.
 */
export default function PageScrubber({ count, page, shownTo, rtl, onSeek, enhanced, thumbs }: PageScrubberProps) {
  const { t } = useLingui()
  const { ref, width } = useElementSize()
  const track = useRef<HTMLDivElement>(null)
  const [hover, setHover] = useState<number | null>(null)
  const [drag, setDrag] = useState<number | null>(null)

  const pageAt = (clientX: number) => {
    const rect = track.current?.getBoundingClientRect()
    if (!rect || rect.width === 0) return page
    const slot = Math.min(count - 1, Math.max(0, Math.floor(((clientX - rect.left) / rect.width) * count)))
    return rtl ? count - 1 - slot : slot
  }
  const seek = (target: number) => onSeek(Math.min(count - 1, Math.max(0, target)))

  const onKeyDown = (event: React.KeyboardEvent) => {
    const forward = rtl ? 'ArrowLeft' : 'ArrowRight'
    const back = rtl ? 'ArrowRight' : 'ArrowLeft'
    const target =
      event.key === forward ? shownTo + 1
      : event.key === back ? page - 1
      : event.key === 'Home' ? 0
      : event.key === 'End' ? count - 1
      : null
    if (target === null) return
    // The reader's own key handler skips anything already handled, so the page turns once.
    event.preventDefault()
    seek(target)
  }

  const from = drag ?? page
  const to = drag ?? shownTo
  const shown = hover ?? drag
  const rect = shown !== null ? track.current?.getBoundingClientRect() : undefined
  const previewX = rect && shown !== null ? rect.left + (((rtl ? count - 1 - shown : shown) + 0.5) / count) * rect.width : 0

  return (
    <div
      ref={ref}
      className="reader-scrub"
      data-gaps={count > 0 && width / count >= MIN_SEGMENT_WITH_GAP ? true : undefined}
      role="slider"
      tabIndex={0}
      aria-label={t`Page`}
      aria-orientation="horizontal"
      aria-valuemin={1}
      aria-valuemax={Math.max(1, count)}
      aria-valuenow={page + 1}
      aria-valuetext={t`Page ${page + 1} of ${count}`}
      onKeyDown={onKeyDown}
      onPointerDown={(event) => {
        if (event.button !== 0 || count === 0) return
        event.currentTarget.setPointerCapture(event.pointerId)
        setDrag(pageAt(event.clientX))
      }}
      onPointerMove={(event) => {
        const at = pageAt(event.clientX)
        if (drag !== null) setDrag(at)
        if (drag !== null || event.pointerType === 'mouse') setHover(at)
      }}
      onPointerUp={(event) => {
        if (drag !== null) seek(pageAt(event.clientX))
        setDrag(null)
        if (event.pointerType !== 'mouse') setHover(null)
      }}
      onPointerCancel={() => {
        setDrag(null)
        setHover(null)
      }}
      onPointerLeave={() => {
        if (drag === null) setHover(null)
      }}
    >
      <div ref={track} className="reader-scrub-track" data-rtl={rtl || undefined}>
        {Array.from({ length: count }, (_, index) => (
          <span
            key={index}
            className="reader-scrub-segment"
            data-lit={index <= to || undefined}
            data-current={(index >= from && index <= to) || undefined}
            data-ready={(index > to && enhanced?.has(index)) || undefined}
            data-hover={index === hover || undefined}
          />
        ))}
      </div>
      {shown !== null && rect && (
        <div
          className="reader-scrub-preview"
          style={{
            left: Math.min(Math.max(previewX, PREVIEW_HALF_WIDTH + 8), window.innerWidth - PREVIEW_HALF_WIDTH - 8),
            top: rect.top,
          }}
          aria-hidden
        >
          {thumbs[shown] && <img src={thumbs[shown]} alt="" decoding="async" draggable={false} />}
          <span>
            {shown + 1} / {count}
          </span>
        </div>
      )}
    </div>
  )
}
