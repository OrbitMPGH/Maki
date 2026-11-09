/**
 * Typo-tolerant title matching for the client-side search boxes (Library, the command palette and
 * the pickers that filter the library). The Add page searches the catalogue on the server, which
 * corrects typos against the dump's vocabulary; these boxes filter titles already in memory, so
 * they need their own small matcher.
 *
 * A title matches when the query is a plain substring (score 0), when every query word lands on a
 * title word within an edit budget, or when the query with spaces removed sits inside the title
 * with spaces removed, again within budget ("onepeice" finds "One Piece"). Lower scores are better.
 * Only Latin, Greek and Cyrillic words of four letters or more get any budget: a short word or a
 * CJK title within one edit is a different word, and so is anything with a digit in it.
 */

export interface PreparedTitle {
  raw: string
  spaced: string
  compact: string
  words: string[]
  fuzzy: boolean
}

export interface PreparedQuery {
  raw: string
  spaced: string
  compact: string
  words: string[]
}

const NON_WORD = /[^\p{L}\p{N}]+/gu
const MARKS = /\p{M}+/gu
const FUZZY_SCRIPT = /^[\p{Script=Latin}\p{Script=Greek}\p{Script=Cyrillic}]/u
const FUZZY_WORD = /^[\p{Script=Latin}\p{Script=Greek}\p{Script=Cyrillic}]+$/u
// Below this, matching across word boundaries finds a near-miss in almost every long title.
const MIN_COMPACT_FUZZY = 6

function normalize(text: string): string {
  return text.normalize('NFKD').replace(MARKS, '').toLocaleLowerCase().replace(NON_WORD, ' ').trim()
}

export function prepareTitle(title: string): PreparedTitle {
  const spaced = normalize(title)
  const words = spaced ? spaced.split(' ') : []
  return {
    raw: title.toLocaleLowerCase(),
    spaced,
    compact: words.join(''),
    words,
    fuzzy: words.some((w) => FUZZY_SCRIPT.test(w)),
  }
}

export function prepareQuery(query: string): PreparedQuery {
  const spaced = normalize(query)
  const words = spaced ? spaced.split(' ') : []
  return { raw: query.trim().toLocaleLowerCase(), spaced, compact: words.join(''), words }
}

function budgetFor(word: string): number {
  if (!FUZZY_WORD.test(word)) return 0
  if (word.length < 4) return 0
  return word.length < 8 ? 1 : 2
}

/**
 * Edit distance from `needle` to the closest substring of `hay` (or prefix, with `anchored`),
 * counting an adjacent swap as one edit. Gives up and returns `budget + 1` once every cell in a
 * column is past the budget.
 */
function closestDistance(needle: string, hay: string, budget: number, anchored: boolean): number {
  const m = needle.length
  let prev2: number[] = []
  let prev = Array.from({ length: m + 1 }, (_, i) => i)
  let best = prev[m]
  for (let j = 1; j <= hay.length; j++) {
    const cur = new Array<number>(m + 1)
    cur[0] = anchored ? j : 0
    let columnMin = cur[0]
    for (let i = 1; i <= m; i++) {
      const cost = needle[i - 1] === hay[j - 1] ? 0 : 1
      let value = Math.min(prev[i] + 1, cur[i - 1] + 1, prev[i - 1] + cost)
      if (i > 1 && j > 1 && needle[i - 1] === hay[j - 2] && needle[i - 2] === hay[j - 1]) {
        value = Math.min(value, prev2[i - 2] + 1)
      }
      cur[i] = value
      if (value < columnMin) columnMin = value
    }
    if (cur[m] < best) best = cur[m]
    if (best === 0 || columnMin > budget) break
    prev2 = prev
    prev = cur
  }
  return best
}

function wordScore(word: string, title: PreparedTitle): number | null {
  const budget = title.fuzzy ? budgetFor(word) : 0
  let best: number | null = null
  for (const candidate of title.words) {
    if (candidate.startsWith(word) || (word.length >= 3 && candidate.includes(word))) return 0
    if (budget === 0) continue
    const distance = closestDistance(word, candidate, budget, true)
    if (distance <= budget && (best === null || distance < best)) best = distance
  }
  return best
}

/** 0 for a plain match, a small positive number for a typo-tolerant one, null for no match. */
export function scoreTitle(query: PreparedQuery, title: PreparedTitle): number | null {
  if (!query.raw) return 0
  if (title.raw.includes(query.raw)) return 0
  if (query.spaced && title.spaced.includes(query.spaced)) return 0
  if (query.compact && title.compact.includes(query.compact)) return 0
  if (query.words.length === 0) return null

  let total: number | null = 0
  for (const word of query.words) {
    const score = wordScore(word, title)
    if (score === null) {
      total = null
      break
    }
    total += score
  }
  if (total === 0) return 0

  if (title.fuzzy && query.compact.length >= MIN_COMPACT_FUZZY) {
    const budget = budgetFor(query.compact)
    if (budget > 0) {
      const distance = closestDistance(query.compact, title.compact, budget, false)
      if (distance <= budget && (total === null || distance < total)) total = distance
    }
  }
  return total
}

/** Best score over several titles of one item (canonical, display, original, alt titles). */
export function scoreTitles(query: PreparedQuery, titles: readonly PreparedTitle[]): number | null {
  let best: number | null = null
  for (const title of titles) {
    const score = scoreTitle(query, title)
    if (score === 0) return 0
    if (score !== null && (best === null || score < best)) best = score
  }
  return best
}

/**
 * Filters `items` to those whose titles match `query`, keeping the order they arrived in so a
 * caller's chosen sort still applies. Typo matches are the fallback: they only show when nothing
 * matches plainly, because a four-letter word is one edit away from half of any library.
 * Build `titlesOf` with `titleIndex` so titles are not re-normalised on every keystroke.
 */
export function fuzzyFilter<T>(
  items: readonly T[],
  query: string,
  titlesOf: (item: T) => readonly PreparedTitle[],
): T[] {
  const prepared = prepareQuery(query)
  if (!prepared.raw) return [...items]
  const exact: T[] = []
  const close: { item: T; score: number }[] = []
  for (const item of items) {
    const score = scoreTitles(prepared, titlesOf(item))
    if (score === 0) exact.push(item)
    else if (score !== null) close.push({ item, score })
  }
  if (exact.length > 0) return exact
  const best = Math.min(...close.map((c) => c.score))
  return close.filter((c) => c.score === best).map((c) => c.item)
}

/** Prepares each item's titles once, keyed by the item object, for repeated `fuzzyFilter` calls. */
export function titleIndex<T extends object>(
  items: readonly T[],
  titles: (item: T) => readonly (string | null | undefined)[],
): (item: T) => readonly PreparedTitle[] {
  const cache = new WeakMap<T, PreparedTitle[]>()
  for (const item of items) {
    const seen = new Set<string>()
    const prepared: PreparedTitle[] = []
    for (const text of titles(item)) {
      if (!text || seen.has(text)) continue
      seen.add(text)
      prepared.push(prepareTitle(text))
    }
    cache.set(item, prepared)
  }
  return (item) => cache.get(item) ?? []
}
