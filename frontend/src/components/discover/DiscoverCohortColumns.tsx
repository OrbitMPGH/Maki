import { useMemo } from 'react'
import { Trans, useLingui } from '@lingui/react/macro'
import type { MessageDescriptor } from '@lingui/core'
import type { DiscoverCohortGroup, DiscoverRail, RecommendationItem } from '../../api/hooks'
import { cohortGenres, cohortRows, useCohortName, type CohortRow } from './discoverBrowse'
import { sectionSource } from '../../api/shortlist'
import { useLibraryCoverLookup } from '../../api/seriesLookup'
import { GENRE_LABELS } from '../CatalogueFilters'
import { useLabel } from '../../i18n-context'
import { IconChevronRight } from '../../lib/icons'
import { ScoreInline } from '../ui/ScoreInline'
import { MetaList } from '../ui/MetaList'
import type { OpenItem } from '../ui/DiscoverRail'
import { formatNumber } from '../../format'

/** Rows per column on the page. */
const ROWS = 3
/** Columns on the page; a reader placed in more groups sees the strongest. */
const COLUMNS = 3

/**
 * "Readers like you also finished" as columns: one per group of readers the reader was placed
 * with, each showing what that group finished that the reader has not, and how many of them
 * finished it. A reader placed in one group alone gets the same picks sorted three ways instead.
 */
export function DiscoverCohortColumns({
  rail,
  seriesIdFor,
  onOpen,
  onSeeAll,
}: {
  rail: DiscoverRail
  seriesIdFor: (item: RecommendationItem) => number | null
  onOpen: OpenItem
  /** Opens the expanded view on one group, or on all of them. */
  onSeeAll: (groupKey: number | null) => void
}) {
  const byId = useMemo(() => new Map(rail.items.map((i) => [i.providerId, i])), [rail.items])
  const groups = useMemo(() => (rail.groups ?? []).filter((g) => cohortRows(g, byId).length > 0), [rail.groups, byId])
  const names = useMemo(() => cohortGenres(groups, byId), [groups, byId])
  // One scale for every bar in the section, so a longer bar always means a larger share.
  const top = useMemo(
    () => Math.max(0.01, ...groups.flatMap((g) => g.members.map((m) => m.share))),
    [groups],
  )

  if (groups.length >= 2) {
    return (
      <div className="discover-cohorts" data-columns={Math.min(groups.length, COLUMNS)}>
        {groups.slice(0, COLUMNS).map((group, i) => (
          <GroupColumn
            key={group.key}
            group={group}
            genres={names[i]}
            rows={cohortRows(group, byId).slice(0, ROWS)}
            top={top}
            seriesIdFor={seriesIdFor}
            onOpen={onOpen}
            onSeeAll={() => onSeeAll(group.key)}
          />
        ))}
      </div>
    )
  }

  return (
    <LensColumns
      rows={groups[0] ? cohortRows(groups[0], byId) : rail.items.map((item) => ({ item, share: 0 }))}
      top={top}
      seriesIdFor={seriesIdFor}
      onOpen={onOpen}
    />
  )
}

function GroupColumn({
  group,
  genres,
  rows,
  top,
  seriesIdFor,
  onOpen,
  onSeeAll,
}: {
  group: DiscoverCohortGroup
  genres: string[]
  rows: CohortRow[]
  top: number
  seriesIdFor: (item: RecommendationItem) => number | null
  onOpen: OpenItem
  onSeeAll: () => void
}) {
  const coverOf = useLibraryCoverLookup()
  const name = useCohortName()
  const covers = group.seedIds.map((id) => coverOf(String(id))).filter((c): c is string => c != null)
  return (
    <section className="discover-cohort" aria-label={name(genres)}>
      <div className="discover-cohort-head">
        {covers.length > 0 && (
          <span className="discover-cohort-yours" aria-hidden>
            {covers.map((c) => (
              <img key={c} src={c} alt="" loading="lazy" decoding="async" />
            ))}
          </span>
        )}
        <span className="discover-cohort-name">
          <h3 className="discover-cohort-title">{name(genres)}</h3>
          <span className="discover-cohort-sub">
            <Trans>Based on {formatNumber(group.shared)} of your finished series</Trans>
          </span>
        </span>
        <span className="discover-cohort-label">
          <Trans>Finished it</Trans>
        </span>
      </div>
      {rows.map((row) => (
        <CohortRowView key={row.item.providerId} row={row} top={top} seriesIdFor={seriesIdFor} onOpen={onOpen} />
      ))}
      <button type="button" className="discover-cohort-more" onClick={onSeeAll}>
        <Trans>See all {formatNumber(group.members.length)}</Trans>
        <IconChevronRight size={14} />
      </button>
    </section>
  )
}

/**
 * The fallback for a reader placed in one group: the same picks sorted three ways, each column
 * taking the best picks the columns before it left, so no title shows twice.
 */
function LensColumns({
  rows,
  top,
  seriesIdFor,
  onOpen,
}: {
  rows: CohortRow[]
  top: number
  seriesIdFor: (item: RecommendationItem) => number | null
  onOpen: OpenItem
}) {
  const { t } = useLingui()
  const columns = useMemo(() => {
    const used = new Set<string>()
    const take = (sorted: CohortRow[]) => {
      const picked = sorted.filter((r) => !used.has(r.item.providerId)).slice(0, ROWS)
      for (const r of picked) used.add(r.item.providerId)
      return picked
    }
    const mostFinished = take([...rows].sort((a, b) => b.share - a.share))
    const ratedHighest = take([...rows].sort((a, b) => (b.item.rating ?? 0) - (a.item.rating ?? 0)))
    const unusual = take(rows)
    return [
      { key: 'finished', title: t`Most of them finished`, sub: t`Highest share of readers like you`, rows: mostFinished },
      { key: 'rated', title: t`Rated highest`, sub: t`Score from all readers`, rows: ratedHighest },
      { key: 'radar', title: t`Under the radar`, sub: t`Finished by them far more than by most readers`, rows: unusual },
    ].filter((c) => c.rows.length > 0)
  }, [rows, t])

  return (
    <div className="discover-cohorts" data-columns={columns.length}>
      {columns.map((c) => (
        <section key={c.key} className="discover-cohort" aria-label={c.title}>
          <div className="discover-cohort-head">
            <span className="discover-cohort-name">
              <h3 className="discover-cohort-title">{c.title}</h3>
              <span className="discover-cohort-sub">{c.sub}</span>
            </span>
            <span className="discover-cohort-label">
              <Trans>Finished it</Trans>
            </span>
          </div>
          {c.rows.map((row) => (
            <CohortRowView key={row.item.providerId} row={row} top={top} seriesIdFor={seriesIdFor} onOpen={onOpen} />
          ))}
        </section>
      ))}
    </div>
  )
}

function CohortRowView({
  row,
  top,
  seriesIdFor,
  onOpen,
}: {
  row: CohortRow
  /** The largest share in the section, which a full bar stands for. */
  top: number
  seriesIdFor: (item: RecommendationItem) => number | null
  onOpen: OpenItem
}) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const { item, share } = row
  const thumbnail = item.thumbUrl ?? item.coverUrl
  const percent = Math.round(share * 100)
  const owned = seriesIdFor(item) != null
  return (
    <button
      type="button"
      className="discover-cohort-row"
      onClick={() => onOpen(item, sectionSource('cohort'))}
      aria-label={owned ? t`${item.title}, in your library` : item.title}
    >
      {thumbnail ? (
        <img src={thumbnail} srcSet={item.thumbUrlHiDpi ? `${item.thumbUrlHiDpi} 2x` : undefined} alt="" loading="lazy" decoding="async" />
      ) : (
        <span className="discover-cohort-blank" aria-hidden />
      )}
      <span className="discover-cohort-copy">
        <span className="discover-cohort-row-title">{item.title}</span>
        <MetaList
          inline
          className="discover-cohort-meta"
          items={[
            item.rating != null ? <ScoreInline key="score" score={item.rating} /> : null,
            item.totalChapters != null ? <Trans key="ch">{item.totalChapters} ch.</Trans> : null,
            item.matchedGenres[0] ? renderLabel((GENRE_LABELS[item.matchedGenres[0]] as MessageDescriptor | undefined) ?? item.matchedGenres[0]) : null,
          ]}
        />
      </span>
      {share > 0 && (
        <span className="discover-cohort-share">
          <span className="discover-cohort-percent tnum">{percent}%</span>
          <span className="discover-cohort-bar" aria-hidden>
            <span style={{ width: `${Math.max(4, Math.min(100, (share / top) * 100))}%` }} />
          </span>
        </span>
      )}
    </button>
  )
}
