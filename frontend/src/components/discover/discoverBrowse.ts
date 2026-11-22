import type { MessageDescriptor } from '@lingui/core'
import { useLingui } from '@lingui/react/macro'
import type { DiscoverCohortGroup, DiscoverRail, RecommendationItem } from '../../api/hooks'
import { GENRE_LABELS } from '../CatalogueFilters'
import { useLabel } from '../../i18n-context'

/** The theme a side-interest rail is about: the one tag or genre its filter holds. */
export function themeOf(rail: DiscoverRail): string {
  return rail.filters?.tags?.[0] ?? rail.filters?.genres?.[0] ?? rail.title
}

/** A group's picks joined to the rail's items, in rail order, with each one's finished share. */
export interface CohortRow {
  item: RecommendationItem
  share: number
}

export function cohortRows(group: DiscoverCohortGroup, byId: Map<string, RecommendationItem>): CohortRow[] {
  return group.members
    .map((m) => {
      const item = byId.get(m.id)
      return item ? { item, share: m.share } : null
    })
    .filter((r): r is CohortRow => r != null)
}

/**
 * A name for each group from what its picks are: the genres it holds more of than the rail as a
 * whole, at most two. The groups are defined by who reads what rather than by genre, so this is a
 * description of their picks, and a later group never repeats an earlier group's name.
 */
export function cohortGenres(groups: DiscoverCohortGroup[], byId: Map<string, RecommendationItem>): string[][] {
  const all = new Map<string, number>()
  let total = 0
  for (const item of byId.values()) {
    total++
    for (const g of item.matchedGenres) all.set(g, (all.get(g) ?? 0) + 1)
  }
  const used = new Set<string>()
  return groups.map((group) => {
    const rows = cohortRows(group, byId)
    if (rows.length === 0 || total === 0) return []
    const counts = new Map<string, number>()
    for (const { item } of rows) for (const g of item.matchedGenres) counts.set(g, (counts.get(g) ?? 0) + 1)
    const ranked = [...counts.entries()]
      .map(([genre, n]) => {
        const share = n / rows.length
        return { genre, share, score: share * (share / ((all.get(genre) ?? n) / total)) }
      })
      .filter((g) => g.share >= 0.3)
      .sort((a, b) => b.score - a.score || a.genre.localeCompare(b.genre))
    let pick = ranked.slice(0, 2).map((g) => g.genre)
    const key = (names: string[]) => [...names].sort().join('|')
    for (let i = 2; used.has(key(pick)) && i < ranked.length; i++) pick = [ranked[0].genre, ranked[i].genre]
    used.add(key(pick))
    return pick
  })
}

/** "Romance and Comedy readers", in the reader's language. */
export function useCohortName() {
  const { t } = useLingui()
  const renderLabel = useLabel()
  return (genres: string[]) => {
    const [a, b] = genres.map((g) => renderLabel((GENRE_LABELS[g] as MessageDescriptor | undefined) ?? g))
    if (a && b) return t`${a} and ${b} readers`
    if (a) return t`${a} readers`
    return t`Readers like you`
  }
}
