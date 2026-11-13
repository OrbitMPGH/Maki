import { useCallback, useEffect, useMemo, useState } from 'react'

/**
 * Page aspect ratios, measured in the browser as images load.
 *
 * Deliberately client-side: the manifest carries no dimensions, because probing every page
 * server-side means decoding the whole archive just to open a chapter. The reader needs the
 * ratio only to decide whether a page is a two-page spread, and by then the image is loading
 * anyway.
 */
export function usePageAspects(urls: string[]) {
  const [wide, setWide] = useState<Record<number, boolean>>({})

  useEffect(() => {
    setWide({})
  }, [urls])

  const measure = useCallback((index: number, image: HTMLImageElement) => {
    if (!image.naturalWidth || !image.naturalHeight) return
    const isWide = image.naturalWidth > image.naturalHeight
    setWide((current) => (current[index] === isWide ? current : { ...current, [index]: isWide }))
  }, [])

  return { wide, measure }
}

export type Spread = number[]

/**
 * Groups pages into what is shown at once in double-page mode.
 *
 * The first page stands alone (covers are single, and pairing from index 0 puts every
 * subsequent spread out of phase). A page measured as wider than it is tall is a printed
 * two-page spread and also stands alone. Pages not measured yet are assumed portrait, so the
 * layout settles as images arrive rather than blocking on them.
 *
 * Only pages `first` to `last` are shown, when credit pages are left out at either end. The pairing
 * keeps the whole chapter's phase, so leaving out a credit page never re-pairs the printed spreads;
 * a pair cut by the window's edge shows its one remaining page alone.
 */
export function useSpreads(
  pageCount: number,
  wide: Record<number, boolean>,
  enabled: boolean,
  first = 0,
  last = pageCount - 1,
): Spread[] {
  return useMemo(() => {
    const spreads: Spread[] = []
    let i = 0
    while (i < pageCount) {
      if (!enabled || i === 0 || wide[i]) {
        spreads.push([i])
        i += 1
        continue
      }
      if (i + 1 < pageCount && !wide[i + 1]) {
        spreads.push([i, i + 1])
        i += 2
        continue
      }
      spreads.push([i])
      i += 1
    }
    if (first <= 0 && last >= pageCount - 1) return spreads
    return spreads.map((spread) => spread.filter((index) => index >= first && index <= last)).filter((spread) => spread.length > 0)
  }, [pageCount, wide, enabled, first, last])
}

/** The spread containing a page, and the first page of a spread. */
export function spreadIndexOf(spreads: Spread[], page: number): number {
  const at = spreads.findIndex((s) => s.includes(page))
  return at < 0 ? 0 : at
}
