export interface NumberingClashPair {
  /** Source key that lists sub-chapters (1.1, 1.2, …). */
  sub: string
  /** Source key that lists whole chapters for the same content. */
  whole: string
}

/** `Series.numberingClash` is "sub|whole" pairs joined by ';'. */
export function parseNumberingClash(value: string | null | undefined): NumberingClashPair[] {
  if (!value) return []
  return value
    .split(';')
    .map((pair) => pair.split('|'))
    .filter((parts): parts is [string, string] => parts.length === 2 && !!parts[0] && !!parts[1])
    .map(([sub, whole]) => ({ sub, whole }))
}
