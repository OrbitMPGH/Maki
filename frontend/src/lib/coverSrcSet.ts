const COVER_URL = /^(\/api\/v1\/mediacover\/\d+\/)cover\.jpg(\?.*)?$/

/** The 240 px and 400 px posters of a library series, or undefined for an external URL. */
export function coverSrcSet(url: string): string | undefined {
  const m = COVER_URL.exec(url)
  if (!m) return undefined
  const query = m[2] ?? ''
  return `${m[1]}cover-sm.jpg${query} 240w, ${url} 400w`
}

export const COVER_CARD_SIZES = '180px'
