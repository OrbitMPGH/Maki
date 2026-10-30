/**
 * Device pixels the engine keeps enhanced results for: eight screens, four on phones and low-memory
 * devices. Kept out of the engine module so the reader can size its window without loading it.
 */
export function enhanceBudget(): number {
  const memory = (navigator as { deviceMemory?: number }).deviceMemory
  const coarse = typeof matchMedia === 'function' && matchMedia('(pointer: coarse)').matches
  const pages = coarse || (memory !== undefined && memory <= 4) ? 4 : 8
  const dpr = globalThis.devicePixelRatio || 1
  return pages * Math.round(screen.width * dpr) * Math.round(screen.height * dpr)
}
