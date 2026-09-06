import { useCallback, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { Collapse, Group, Text, Tooltip, UnstyledButton } from '@mantine/core'
import { IconChevronDown } from '@tabler/icons-react'
import {
  animeSpanKind,
  buildAnimeSpans,
  mergeAnimeMarkers,
  type AnimeSpan,
  type AnimeSpanKind,
} from '../lib/animeCoverage'

/** More than the chapter-table stripe can fit, but the bar stacks downwards and has the room. */
const BAR_LANES = 6

/** Horizontal padding inside a pill, matching `.anime-coverage-span` in theme.css. */
const SPAN_PADDING_X = 8

const KIND_LABEL: Record<AnimeSpanKind, string> = {
  season: 'Season',
  film: 'Film / OVA',
  other: 'Adaptation',
}

const rangeText = (span: AnimeSpan) =>
    span.openEnded ? `ch. ${span.from}+` : `ch. ${span.from}–${span.to}`

/**
 * The anime coverage fields drawn as ranges over a chapter axis rather than printed as the two
 * free-text strings MangaBaka sends.
 *
 * The axis is chapter numbers, so a pill's width is the share of the manga that adaptation covers
 * and the gap after the last pill is the part no anime has reached. `readChapter` puts a rule where
 * the reader is, which is what makes the bar answer "the anime ended, where do I start reading".
 *
 * Parsing drops anything without a "Chap N (label)" anchor, so the raw strings stay available
 * behind the details toggle; when nothing parses at all the component renders them plainly instead
 * of an empty axis.
 */
export function AnimeCoverageBar({
  start,
  end,
  totalChapters,
  readChapter,
  tooltipZIndex,
}: {
  start: string | null | undefined
  end: string | null | undefined
  /** Chapter count the axis runs to. Null falls back to the furthest marker, padded. */
  totalChapters: number | null | undefined
  /** Where the reader is, when known. Omitted in Discover, where there is no progress. */
  readChapter?: number | null
  /**
   * Tooltips render in a portal at Mantine's default z-index, which is below a modal. Callers
   * inside one have to lift them: DiscoverDetailModal sits at 1000 and uses 1001 for its own.
   */
  tooltipZIndex?: number
}) {
  const [rawOpen, setRawOpen] = useState(false)

  const model = useMemo(() => {
    const markers = mergeAnimeMarkers(start, end)
    const markerNums = [...markers.keys()]
    if (markerNums.length === 0) return null
    const maxMarker = Math.max(...markerNums)

    // Without a chapter count there is nothing to measure an airing season against, so the axis is
    // stretched past the furthest marker. The padding is also what keeps an open-ended span from
    // collapsing to zero width and being dropped by buildAnimeSpans.
    const lastKnown = totalChapters ?? Math.round(maxMarker * 1.15) + 1
    const spans = buildAnimeSpans(markers, lastKnown, BAR_LANES)
    if (spans.length === 0) return null

    const domainMax = Math.max(lastKnown, maxMarker, ...spans.map((s) => s.to))
    const domainMin = 1
    // A span still airing has no end chapter, so nothing downstream may claim to know where the
    // adaptation stops: `covered` counts only spans that actually ended.
    const hasOpen = spans.some((s) => s.openEnded)
    const ended = spans.filter((s) => !s.openEnded)
    const covered = ended.length > 0 ? Math.max(...ended.map((s) => s.to)) : null

    // Ends that paired with no start keep being the point they were in the chapter table; they get
    // a tick rather than a pill, since a range is exactly what they are missing.
    const pairedEnds = new Set(spans.filter((s) => !s.openEnded).map((s) => s.to))
    const orphanEnds = [...markers.entries()]
        .flatMap(([num, list]) => list.filter((m) => m.kind === 'end').map((m) => ({ num, label: m.label })))
        .filter((e) => !pairedEnds.has(e.num))

    return {
      spans,
      domainMin,
      domainMax,
      covered,
      hasOpen,
      lanes: Math.max(...spans.map((s) => s.lane)) + 1,
      orphanEnds,
    }
  }, [start, end, totalChapters])

  /**
   * Which pills have room for their label. Measured rather than guessed from the pill's share of
   * the axis: "S2" in a 40px pill fits where "Film + OVA" in a 90px one does not, and a percentage
   * threshold cannot tell those apart. Labels stay mounted when they don't fit (hidden via
   * `visibility`) so they can still be measured when the container resizes.
   */
  const [labelFits, setLabelFits] = useState<Record<string, boolean>>({})
  const trackRef = useRef<HTMLDivElement | null>(null)
  const labelRefs = useRef(new Map<string, HTMLSpanElement>())

  const setLabelRef = useCallback((key: string, el: HTMLSpanElement | null) => {
    if (el) labelRefs.current.set(key, el)
    else labelRefs.current.delete(key)
  }, [])

  const measureLabels = useCallback(() => {
    setLabelFits((prev) => {
      const next: Record<string, boolean> = {}
      let changed = false
      for (const [key, el] of labelRefs.current) {
        const pill = el.parentElement
        if (!pill) continue
        // scrollWidth is the label's full text width even while clipped; clientWidth excludes the
        // pill's border but includes its padding, which the text does not get to use.
        const fits = el.scrollWidth <= pill.clientWidth - SPAN_PADDING_X * 2
        next[key] = fits
        if (prev[key] !== fits) changed = true
      }
      if (!changed && Object.keys(prev).length === Object.keys(next).length) return prev
      return next
    })
  }, [])

  useLayoutEffect(() => {
    const track = trackRef.current
    if (!track) return
    measureLabels()
    const observer = new ResizeObserver(measureLabels)
    observer.observe(track)
    return () => observer.disconnect()
  })

  const raw = (
      <>
        {start && (
            <Text size="xs" c="var(--ink-4)" className="tnum" style={{ lineHeight: 1.5 }}>
              From: {start}
            </Text>
        )}
        {end && (
            <Text size="xs" c="var(--ink-4)" className="tnum" style={{ lineHeight: 1.5 }}>
              Until: {end}
            </Text>
        )}
      </>
  )

  if (!model) return <div className="anime-coverage-raw">{raw}</div>

  const { spans, domainMin, domainMax, covered, hasOpen, lanes, orphanEnds } = model
  const pct = (n: number) => {
    const span = domainMax - domainMin
    if (span <= 0) return 0
    return Math.min(100, Math.max(0, ((n - domainMin) / span) * 100))
  }

  const kindsPresent = [...new Set(spans.map((s) => animeSpanKind(s.label)))]
  const readPct = readChapter != null && readChapter > 0 ? pct(readChapter) : null
  // Only worth saying when the reader is inside or behind the adapted part; past it, the bar
  // already shows the marker sitting in open ground.
  const startReadingAt =
      covered != null && !hasOpen && readChapter != null && readChapter < covered ? covered + 1 : null

  return (
      <div className="anime-coverage">
        <Group justify="space-between" align="baseline" gap="sm" wrap="nowrap" mb={10}>
          <Text size="xs" c="var(--ink-4)">
            {spans.length === 1 ? '1 adaptation' : `${spans.length} adaptations`}
          </Text>
          <Text size="xs" c="var(--ink-4)" className="tnum">
            {hasOpen || covered === null
                ? `ch. ${spans[0].from}+`
                : `ch. ${spans[0].from}–${covered}${totalChapters != null ? ` of ${totalChapters}` : ''}`}
          </Text>
        </Group>

        <div
            ref={trackRef}
            className="anime-coverage-track"
            style={{ height: lanes * 24 + (lanes - 1) * 4 }}
        >
          {spans.map((span) => {
            const left = pct(span.from)
            const width = Math.max(1.5, pct(span.to) - left)
            const kind = animeSpanKind(span.label)
            return (
                <Tooltip
                    key={span.key}
                    label={`${span.label} · ${rangeText(span)}`}
                    withArrow
                    zIndex={tooltipZIndex}
                >
                  <div
                      className="anime-coverage-span"
                      data-kind={kind}
                      data-open-ended={span.openEnded || undefined}
                      style={{
                        left: `${left}%`,
                        width: `${width}%`,
                        top: span.lane * 28,
                      }}
                  >
                    <span
                        ref={(el) => setLabelRef(span.key, el)}
                        className="anime-coverage-span-label"
                        data-fits={labelFits[span.key] === false ? 'false' : undefined}
                    >
                      {span.label}
                    </span>
                  </div>
                </Tooltip>
            )
          })}

          {orphanEnds.map((e) => (
              <Tooltip
                  key={`end-${e.num}`}
                  label={`${e.label} · ends at ch. ${e.num}`}
                  withArrow
                  zIndex={tooltipZIndex}
              >
                <div className="anime-coverage-tick" style={{ left: `${pct(e.num)}%` }} />
              </Tooltip>
          ))}

          {readPct !== null && (
              <Tooltip label={`You have read to ch. ${readChapter}`} withArrow zIndex={tooltipZIndex}>
                <div className="anime-coverage-progress" style={{ left: `${readPct}%` }} />
              </Tooltip>
          )}
        </div>

        <div className="anime-coverage-axis tnum">
          <span>{domainMin}</span>
          {readPct !== null && readPct > 8 && readPct < 92 && (
              <span className="anime-coverage-axis-you" style={{ left: `${readPct}%` }}>
                you · {readChapter}
              </span>
          )}
          <span>{domainMax}</span>
        </div>

        <Group gap={14} mt={10} wrap="wrap">
          {kindsPresent.map((k) => (
              <Group key={k} gap={6} wrap="nowrap">
                <span className="anime-coverage-swatch" data-kind={k} />
                <Text size="xs" c="var(--ink-4)">
                  {KIND_LABEL[k]}
                </Text>
              </Group>
          ))}
          {!hasOpen && covered !== null && domainMax > covered && (
              <Text size="xs" c="var(--ink-4)" className="tnum">
                ch. {covered + 1} onward not adapted
              </Text>
          )}
        </Group>

        {startReadingAt !== null && (
            <Text size="xs" c="var(--ink-3)" mt={8} className="tnum">
              The anime covers through ch. {covered}. Start reading at ch. {startReadingAt}.
            </Text>
        )}

        {(start || end) && (
            <>
              <UnstyledButton
                  className="anime-coverage-raw-toggle"
                  onClick={() => setRawOpen((o) => !o)}
                  aria-expanded={rawOpen}
              >
                <Text size="xs" c="var(--ink-4)">
                  {rawOpen ? 'Hide' : 'Show'} source text
                </Text>
                <IconChevronDown
                    size={13}
                    style={{ transform: rawOpen ? 'rotate(180deg)' : undefined, transition: 'transform 140ms ease' }}
                />
              </UnstyledButton>
              <Collapse expanded={rawOpen}>
                <div className="anime-coverage-raw">{raw}</div>
              </Collapse>
            </>
        )}
      </div>
  )
}
