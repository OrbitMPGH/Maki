import { useCallback, useLayoutEffect, useRef, useState } from 'react'
import { Button, Stack, Text } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import type { ReaderDirection, ReaderFit } from './prefs'
import type { Spread } from './useSpreads'

const FIT_CLASS: Record<ReaderFit, string> = {
  width: 'reader-fit-width',
  height: 'reader-fit-height',
  screen: 'reader-fit-screen',
  original: 'reader-fit-original',
}

type PageState = 'loading' | 'ready' | 'failed'

/**
 * One page with its own loading and failure state: the page's outline while it loads, and a retry
 * where a failed one would otherwise show the browser's broken-image icon. The image stays mounted
 * (hidden until it is ready) so a retry just re-requests it with a cache-busting suffix.
 */
function PagedPage({
  src,
  alt,
  className,
  style,
  onLoaded,
}: {
  src: string
  alt: string
  className: string
  style: React.CSSProperties | undefined
  onLoaded: (image: HTMLImageElement) => void
}) {
  const [state, setState] = useState<PageState>('loading')
  const [attempt, setAttempt] = useState(0)
  const url = attempt === 0 ? src : `${src}${src.includes('?') ? '&' : '?'}retry=${attempt}`

  const loaded = useRef(onLoaded)
  loaded.current = onLoaded
  const markReady = useCallback((image: HTMLImageElement) => {
    setState('ready')
    loaded.current(image)
  }, [])
  // A page already in the browser cache is complete before React attaches its load listener.
  const setRef = useCallback(
    (image: HTMLImageElement | null) => {
      if (image?.complete && image.naturalWidth > 0) markReady(image)
    },
    [markReady],
  )

  return (
    <>
      {state === 'loading' && <div className="reader-page-skeleton" aria-hidden />}
      {state === 'failed' && (
        <Stack align="center" gap="xs" onClick={(event) => event.stopPropagation()}>
          <Text c="var(--ink-3)" size="sm">
            <Trans>This page failed to load.</Trans>
          </Text>
          <Button
            variant="light"
            size="xs"
            onClick={() => {
              setState('loading')
              setAttempt((n) => n + 1)
            }}
          >
            <Trans>Try again</Trans>
          </Button>
        </Stack>
      )}
      <img
        ref={setRef}
        src={url}
        alt={alt}
        className={className}
        style={state === 'ready' ? style : { ...style, display: 'none' }}
        decoding="async"
        draggable={false}
        onLoad={(event) => markReady(event.currentTarget)}
        onError={() => setState('failed')}
      />
    </>
  )
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

  // Scrolling can only reach the right and bottom of a transformed box, so the zoom grows from the
  // top-left corner and the scroll position is moved to keep the viewport centre where it was.
  const lastZoom = useRef(zoom)
  useLayoutEffect(() => {
    const scroller = root.current?.parentElement
    const previous = lastZoom.current
    lastZoom.current = zoom
    if (!scroller || previous === zoom) return
    const centre = scroller.clientWidth / 2
    scroller.scrollLeft = (scroller.scrollLeft + centre) * (zoom / previous) - centre
  }, [zoom])

  return (
    <div
      ref={root}
      className="reader-paged"
      data-double={spread.length > 1}
      style={zoom === 1 ? undefined : { transform: `scale(${zoom})`, transformOrigin: 'left top' }}
    >
      {ordered.map((page) => {
        const src = urls[page]
        if (!src) return null
        const pageNumber = page + 1
        return (
          <PagedPage
            key={src}
            src={src}
            alt={t`${label} - page ${pageNumber}`}
            className={`reader-page ${FIT_CLASS[fit]}`}
            style={fit === 'original' && scale !== 100 ? { zoom: scale / 100 } : undefined}
            onLoaded={(image) => onMeasure(page, image)}
          />
        )
      })}
    </div>
  )
}
