import { useLayoutEffect, useRef } from 'react'
import { useLingui } from '@lingui/react/macro'
import type { ReaderDirection, ReaderFit } from './prefs'
import type { Spread } from './useSpreads'

const FIT_CLASS: Record<ReaderFit, string> = {
  width: 'reader-fit-width',
  height: 'reader-fit-height',
  screen: 'reader-fit-screen',
  original: 'reader-fit-original',
}

/**
 * One spread at a time: a single page, or two side by side in double mode.
 * Page turns are instant because neighbours are already preloaded.
 */
export default function PagedView({
  urls,
  spread,
  fit,
  direction,
  zoom,
  scale,
  label,
  onMeasure,
}: {
  urls: string[]
  spread: Spread
  fit: ReaderFit
  direction: ReaderDirection
  zoom: number
  /** Percent scale on top of the '1:1' fit; ignored for the other fits. */
  scale: number
  label: string
  onMeasure: (index: number, image: HTMLImageElement) => void
}) {
  const { t } = useLingui()
  // In right-to-left reading the lower page number belongs on the right.
  const ordered = direction === 'rtl' ? [...spread].reverse() : spread
  const root = useRef<HTMLDivElement>(null)

  // The scroller outlives the page, so a page read to its bottom would otherwise hand the next one
  // the same offset.
  const leadSrc = urls[spread[0]]
  useLayoutEffect(() => {
    const scroller = root.current?.parentElement
    if (scroller) scroller.scrollTop = 0
  }, [leadSrc])

  return (
    <div
      ref={root}
      className="reader-paged"
      data-double={spread.length > 1}
      style={zoom === 1 ? undefined : { transform: `scale(${zoom})`, transformOrigin: 'center top' }}
    >
      {ordered.map((page) => {
        const src = urls[page]
        if (!src) return null
        const pageNumber = page + 1
        return (
          <img
            key={src}
            src={src}
            alt={t`${label} - page ${pageNumber}`}
            className={`reader-page ${FIT_CLASS[fit]}`}
            style={fit === 'original' && scale !== 100 ? { zoom: scale / 100 } : undefined}
            decoding="async"
            draggable={false}
            onLoad={(event) => onMeasure(page, event.currentTarget)}
          />
        )
      })}
    </div>
  )
}
