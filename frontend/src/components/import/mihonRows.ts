import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import type { MihonImportItem, MihonMatchedBy, MihonParsedManga, MihonResolveResult } from '../../api/mihonImport'
import type { MetadataSearchResult } from '../../api/types'

export const MATCHED_BY_LABELS: Record<MihonMatchedBy, MessageDescriptor> = {
  mangabaka: msg`MangaBaka id`,
  anilist: msg`AniList`,
  mal: msg`MyAnimeList`,
  kitsu: msg`Kitsu`,
  title: msg`Title`,
}

export interface MihonRow {
  manga: MihonParsedManga
  result: MihonResolveResult
}

/** What the user did to one row on the review screen. */
export interface MihonRowChoice {
  /** The candidate picked from a needs-review row. */
  picked?: string
  /** A match found by hand; it makes the row matched. */
  manual?: MetadataSearchResult
  /** Overrides the default tick. */
  checked?: boolean
}

export type MihonFilter = 'all' | 'matched' | 'needsReview' | 'notFound' | 'inLibrary'

export interface EffectiveMatch {
  match: MetadataSearchResult | null
  matchedBy: MihonMatchedBy | null
  library: MihonResolveResult['library']
}

export function effectiveMatch(row: MihonRow, choice: MihonRowChoice | undefined): EffectiveMatch {
  const { result } = row
  if (choice?.manual) return { match: choice.manual, matchedBy: null, library: 'none' }
  if (result.status === 'matched') {
    return { match: result.match, matchedBy: result.matchedBy, library: result.library }
  }
  if (result.status === 'needsReview' && choice?.picked) {
    const match = result.candidates.find((c) => c.providerId === choice.picked) ?? null
    return { match, matchedBy: result.matchedBy, library: 'none' }
  }
  return { match: null, matchedBy: null, library: 'none' }
}

export function rowFilter(row: MihonRow, choice: MihonRowChoice | undefined): Exclude<MihonFilter, 'all'> {
  const { match, library } = effectiveMatch(row, choice)
  if (choice?.manual) return 'matched'
  if (row.result.status === 'matched' && match && library !== 'none') return 'inLibrary'
  if (row.result.status === 'matched') return 'matched'
  return row.result.status
}

export function isSelectable(row: MihonRow, choice: MihonRowChoice | undefined): boolean {
  const { match, library } = effectiveMatch(row, choice)
  return match !== null && library !== 'elsewhere'
}

export function isChecked(row: MihonRow, choice: MihonRowChoice | undefined): boolean {
  if (!isSelectable(row, choice)) return false
  if (choice?.checked !== undefined) return choice.checked
  // Needs-review rows are ticked once a candidate is chosen, which sets `checked` itself.
  return row.result.status === 'matched' || choice?.manual !== undefined
}

/** One series to create or update, merged from every ticked row that resolved to it. */
export interface MihonGroup extends MihonImportItem {
  title: string
}

export function buildGroups(rows: MihonRow[], choices: Record<number, MihonRowChoice>): MihonGroup[] {
  const groups = new Map<string, { title: string; categories: Set<string>; read: Set<number>; last: number | null }>()
  for (const row of rows) {
    const choice = choices[row.manga.index]
    if (!isChecked(row, choice)) continue
    const { match } = effectiveMatch(row, choice)
    if (!match) continue
    let group = groups.get(match.providerId)
    if (!group) {
      group = { title: match.title, categories: new Set(), read: new Set(), last: null }
      groups.set(match.providerId, group)
    }
    for (const c of row.manga.categories) group.categories.add(c)
    for (const n of row.manga.readNumbers) group.read.add(n)
    const last = row.manga.trackerLastRead
    if (last !== null && (group.last === null || last > group.last)) group.last = last
  }
  return Array.from(groups, ([providerId, g]) => ({
    providerId,
    title: g.title,
    categories: Array.from(g.categories),
    readNumbers: Array.from(g.read).sort((a, b) => a - b),
    trackerLastRead: g.last,
  }))
}
