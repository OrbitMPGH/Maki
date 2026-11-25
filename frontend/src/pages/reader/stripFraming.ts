export type StripBand = { top: number; bottom: number }

const EDGE_PAD = 7
const MIN_GUTTER_SHARE = 0.025
const MAX_JOIN_GUTTER_SHARE = 0.075
const MIN_STEP_SHARE = 0.25

export function mergeStripBands(bands: StripBand[]): StripBand[] {
  const merged: StripBand[] = []
  for (const band of bands.sort((a, b) => a.top - b.top)) {
    const last = merged.at(-1)
    if (last && band.top <= last.bottom + 1) last.bottom = Math.max(last.bottom, band.bottom)
    else merged.push({ ...band })
  }
  return merged
}

/** Work in reading order so the leading edge of either turn is handled the same way. */
export function frameStripSection(
  from: number,
  height: number,
  max: number,
  direction: 1 | -1,
  coverage: StripBand[],
  gutters: StripBand[],
  stripStart: number | undefined,
  stripEnd: number | undefined,
): number | undefined {
  const orient = (band: StripBand): StripBand =>
    direction > 0 ? band : { top: -band.bottom, bottom: -band.top }
  const start = direction > 0 ? from : -(from + height)
  const edge = start + height
  const range = coverage.map(orient).find((band) => band.top <= edge && edge <= band.bottom)
  if (!range) return

  const firstEdge = direction > 0 ? stripStart : stripEnd === undefined ? undefined : -stripEnd
  const lastEdge = direction > 0 ? stripEnd : stripStart === undefined ? undefined : -stripStart
  const rangeStart = firstEdge !== undefined && firstEdge >= range.top ? firstEdge : undefined
  const rangeEnd = lastEdge !== undefined && lastEdge <= range.bottom ? lastEdge : undefined
  const gaps = gutters.map(orient)
    .filter((gap) => gap.top >= range.top && gap.bottom <= range.bottom &&
      gap.bottom - gap.top >= Math.max(EDGE_PAD * 2, height * MIN_GUTTER_SHARE))
    .sort((a, b) => a.top - b.top)

  const sectionTop = (index: number) => gaps[index]?.bottom ?? rangeStart
  const sectionBottom = (index: number) => gaps[index]?.top ?? rangeEnd
  const usableHeight = height - EDGE_PAD * 2
  const joinLimit = height * MAX_JOIN_GUTTER_SHARE
  const gapSize = (index: number) => {
    const gap = gaps[index]
    return gap ? gap.bottom - gap.top : Infinity
  }

  const frame = (firstGap: number): number | undefined => {
    let beforeIndex = firstGap
    let afterIndex = firstGap + 1
    let top = sectionTop(beforeIndex)
    let bottom = sectionBottom(afterIndex)
    if (bottom === undefined || bottom <= edge + EDGE_PAD || (top !== undefined && top >= bottom)) return
    // Without a gutter behind the edge the art there may just be unsampled.
    // Only a known extent taller than the screen proves the section cannot fit.
    if (top === undefined && bottom - range.top <= usableHeight) return
    // A balloon over a fading panel can have a short band of white between its tail and the art.
    while (top !== undefined) {
      const earlier = sectionTop(beforeIndex - 1)
      const later = sectionBottom(afterIndex + 1)
      const previousGap = gapSize(beforeIndex)
      const nextGap = gapSize(afterIndex)
      const joinsEarlier = earlier !== undefined && previousGap <= joinLimit && bottom - earlier <= usableHeight
      const joinsLater = later !== undefined && nextGap <= joinLimit && later - top <= usableHeight
      if (joinsEarlier && (!joinsLater || previousGap <= nextGap)) {
        top = earlier
        beforeIndex--
      } else if (joinsLater) {
        bottom = later
        afterIndex++
      } else {
        break
      }
    }
    const next = top !== undefined && bottom - top <= usableHeight
      ? (top + bottom - height) / 2
      : bottom + EDGE_PAD - height
    // Rows between the old bottom edge and the new top are never shown, so they must be gutter.
    const before = gaps[beforeIndex]
    if (next > edge && (!before || before.top > edge || next > before.bottom)) return
    return next
  }

  const minStep = height * MIN_STEP_SHARE
  const firstGap = gaps.findLastIndex((gap) => gap.top <= edge + EDGE_PAD)
  let next = frame(firstGap)
  // A section ending just past the edge would barely move, so frame the one after it.
  if (next !== undefined && next - start < minStep) next = gaps[firstGap + 1] ? frame(firstGap + 1) : undefined
  if (next === undefined || next - start < minStep) return

  const target = direction > 0 ? next : -next - height
  return Math.max(0, Math.min(max, target))
}
