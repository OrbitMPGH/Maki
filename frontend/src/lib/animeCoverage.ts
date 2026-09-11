/**
 * Parsing for MangaBaka's free-text anime coverage fields, shared by the chapter-table stripe on
 * SeriesDetailPage and the coverage bar shown in both SeriesDetailPage and DiscoverDetailModal.
 */

export type AnimeMarker = { label: string; kind: 'start' | 'end' }

/**
 * AnimeStart/AnimeEnd are free-text from MangaBaka, e.g.
 * "Vol 1, Chap 1 (S1) / Vol 31, Chap 270 (Film + OVA) / Vol 35, Chap 315 (S2)". Matches every
 * "Chap N ... (label)" run anywhere in the string rather than splitting on " / " first, so it
 * also survives entries with no chapter anchor at all ("Alternate Setting with an original
 * ending") and trailing notes glued onto the last segment ("... (Shippuden) Chap 239-244 adapted
 * in EP 119-120"): neither of those has a "Chap N (" to match, so they're silently skipped.
 */
export function parseAnimeMarkers(
    text: string | null | undefined,
    kind: 'start' | 'end',
): Map<number, AnimeMarker[]> {
  const map = new Map<number, AnimeMarker[]>()
  if (!text) return map
  const re = /Chap\s*(\d+(?:\.\d+)?)[^()/]*\(([^)]+)\)/gi
  const reOnce = /Chap\s*(\d+(?:\.\d+)?)[^()/]*/gi
  let match: RegExpExecArray | null
  while ((match = re.exec(text))) {
    const chapterNum = parseFloat(match[1])
    const label = match[2].trim()
    const list = map.get(chapterNum) ?? []
    list.push({ label, kind })
    map.set(chapterNum, list)
  }
  // If no "(label)" was found, fall back to the first chapter number found and give it a default label.
  if (map.size === 0 && (match = reOnce.exec(text))) {
    const chapterNum = parseFloat(match[1])
    const label = 'S1'
    const list = map.get(chapterNum) ?? []
    list.push({ label, kind })
    map.set(chapterNum, list)
  }
  return map
}

export function mergeAnimeMarkers(
    start: string | null | undefined,
    end: string | null | undefined,
): Map<number, AnimeMarker[]> {
  const combined = new Map<number, AnimeMarker[]>()
  for (const source of [parseAnimeMarkers(start, 'start'), parseAnimeMarkers(end, 'end')]) {
    for (const [num, list] of source) {
      combined.set(num, [...(combined.get(num) ?? []), ...list])
    }
  }
  return combined
}

/** Widest the stacked stripe in the Chapter cell gets. Beyond this a lane draws no line. */
export const MAX_SPAN_LANES = 3

export type AnimeSpan = {
  key: string
  label: string
  from: number
  /** Inclusive. A season still airing has no end marker and runs to the last known chapter. */
  to: number
  /** True when `to` was inferred rather than read off an end marker. */
  openEnded: boolean
  lane: number
}

/**
 * Pairs the point markers into ranges, so a season reads as a run of chapters rather than as two
 * badges 270 rows apart.
 *
 * MangaBaka writes the same label on both sides in practice ("Chap 1 (S1)" in AnimeStart, "Chap
 * 270 (S1)" in AnimeEnd), so labels are matched first; a start whose label matches nothing falls
 * back to the next unconsumed end after it, which is what an entry with mismatched or missing
 * labels degrades to. A start with no end at all is a currently-airing season and runs to the last
 * chapter known. An end with no start is left alone — it stays the point badge it already was.
 *
 * `maxLanes` caps how many overlapping spans get laid out; the rest are dropped. The chapter-table
 * stripe has a fixed-width slot to draw in and passes the default, the coverage bar has room to
 * stack and passes a larger number.
 */
export function buildAnimeSpans(
    markers: Map<number, AnimeMarker[]>,
    lastChapterNumber: number,
    maxLanes: number = MAX_SPAN_LANES,
): AnimeSpan[] {
  const starts: { num: number; label: string }[] = []
  const ends: { num: number; label: string; used: boolean }[] = []
  for (const [num, list] of markers) {
    for (const m of list) {
      if (m.kind === 'start') starts.push({ num, label: m.label })
      else ends.push({ num, label: m.label, used: false })
    }
  }
  starts.sort((a, b) => a.num - b.num)
  ends.sort((a, b) => a.num - b.num)

  const spans: AnimeSpan[] = []
  for (const start of starts) {
    const norm = start.label.trim().toLowerCase()
    const end =
        ends.find((e) => !e.used && e.num >= start.num && e.label.trim().toLowerCase() === norm) ??
        ends.find((e) => !e.used && e.num >= start.num)
    if (end) end.used = true

    const to = end?.num ?? lastChapterNumber
    // A start past the last known chapter, or an end that lands on the start, spans nothing worth
    // drawing a line for.
    if (to <= start.num) continue

    spans.push({
      key: `${start.label}:${start.num}-${to}`,
      label: start.label,
      from: start.num,
      to,
      openEnded: end === undefined,
      lane: 0,
    })
  }

  // Lanes: an enclosing span takes the outer one, so a film sitting inside a season's range draws
  // beside it rather than on top of it. Anything past the cap keeps its point badges and no line.
  spans.sort((a, b) => a.from - b.from || b.to - a.to)
  const laneEnds: number[] = []
  const laid: AnimeSpan[] = []
  for (const span of spans) {
    let lane = laneEnds.findIndex((end) => end < span.from)
    if (lane === -1) {
      if (laneEnds.length >= maxLanes) continue
      lane = laneEnds.length
    }
    laneEnds[lane] = span.to
    laid.push({ ...span, lane })
  }
  return laid
}

/**
 * Which of the three visual kinds a MangaBaka label reads as. Keyed off the label text rather than
 * the span's position, so the same season is the same colour on every series.
 */
export type AnimeSpanKind = 'season' | 'film' | 'other'

export function animeSpanKind(label: string): AnimeSpanKind {
  const s = label.toLowerCase()
  if (/\b(film|movie|ova|ona|special|specials)\b/.test(s)) return 'film'
  if (/(^|\b)(s\d|season|shippuden|part\s*\d|cour)/.test(s)) return 'season'
  return 'other'
}
