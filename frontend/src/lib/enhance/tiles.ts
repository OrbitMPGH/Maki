export const TILE_OVERLAP = 16

/** Largest source extent processed in one piece; the 2x chain output must still fit a texture. */
export function tileLimit(maxTextureSize: number): number {
  return Math.floor(Math.min(maxTextureSize, 4096) / 2)
}

export interface Span {
  /** Source pixels the tile reads, interior plus overlap. */
  start: number
  end: number
  /** Source pixels the tile is responsible for drawing. Interiors tile the axis exactly. */
  inStart: number
  inEnd: number
}

export function spans(length: number, limit: number): Span[] {
  if (length <= limit) return [{ start: 0, end: length, inStart: 0, inEnd: length }]
  const step = limit - 2 * TILE_OVERLAP - 1
  const n = Math.ceil(length / step)
  const out: Span[] = []
  for (let k = 0; k < n; k++) {
    const inStart = Math.round((k * length) / n)
    const inEnd = Math.round(((k + 1) * length) / n)
    out.push({ inStart, inEnd, start: Math.max(0, inStart - TILE_OVERLAP), end: Math.min(length, inEnd + TILE_OVERLAP) })
  }
  return out
}
