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
  label,
  onMeasure,
}: {
  urls: string[]
  spread: Spread
  fit: ReaderFit
  direction: ReaderDirection
  zoom: number
  label: string
  onMeasure: (index: number, image: HTMLImageElement) => void
}) {
  // In right-to-left reading the lower page number belongs on the right.
  const ordered = direction === 'rtl' ? [...spread].reverse() : spread

  return (
    <div
      className="reader-paged"
      data-double={spread.length > 1}
      style={zoom === 1 ? undefined : { transform: `scale(${zoom})`, transformOrigin: 'center top' }}
    >
      {ordered.map((page) => {
        const src = urls[page]
        if (!src) return null
        return (
          <img
            key={src}
            src={src}
            alt={`${label} - page ${page + 1}`}
            className={`reader-page ${FIT_CLASS[fit]}`}
            decoding="async"
            draggable={false}
            onLoad={(event) => onMeasure(page, event.currentTarget)}
          />
        )
      })}
    </div>
  )
}
