import { memo, type ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { IconPlus } from '@tabler/icons-react'
import type { HomeAnimeResumeItem } from '../../api/animeResume'
import type { RecommendationItem } from '../../api/hooks'
import { Trans, useLingui } from '@lingui/react/macro'

/**
 * Horizontal rail of series whose anime the reader finished but the manga hasn't caught up to.
 * Same card markup as {@link RecentlyAddedRail}, but the corner badge names the resume chapter
 * instead of a chapter count, and there is no Read button: the resume chapter may not even be
 * downloaded yet, so a library card only ever opens the series page.
 *
 * A card with no library copy opens the Discover modal instead, whose Add can tick the anime's
 * chapters off in the same step.
 */
export function AnimeResumeRail({
  items,
  onOpen,
}: {
  items: HomeAnimeResumeItem[]
  onOpen: (item: RecommendationItem) => void
}) {
  return (
    <div className="discover-rail">
      {items.map((item) => (
        <div key={item.seriesId ?? `mb-${item.catalogue?.providerId}`} className="discover-rail-item">
          <AnimeResumeCard item={item} onOpen={onOpen} />
        </div>
      ))}
    </div>
  )
}

// Memoized: `item` keeps its reference across renders, so unrelated Home state does not re-render the cards.
const AnimeResumeCard = memo(function AnimeResumeCard({
  item,
  onOpen,
}: {
  item: HomeAnimeResumeItem
  onOpen: (item: RecommendationItem) => void
}) {
  const { t } = useLingui()
  const title = item.seriesTitle

  if (item.seriesId == null && item.catalogue) {
    const catalogue = item.catalogue
    return (
      <div className="cover-card discover-card">
        <button
          type="button"
          className="discover-card-action"
          aria-label={t`View and add ${title}`}
          onClick={() => onOpen(catalogue)}
        />
        <AnimeResumePoster item={item}>
          <span className="discover-corner" data-add="true" data-tip={t`View & add`} aria-hidden="true">
            <IconPlus size={16} />
          </span>
        </AnimeResumePoster>
      </div>
    )
  }

  return (
    <Link to={`/series/${item.seriesId}`} className="cover-card" aria-label={title}>
      <AnimeResumePoster item={item} />
    </Link>
  )
})

function AnimeResumePoster({ item, children }: { item: HomeAnimeResumeItem; children?: ReactNode }) {
  const next = Math.floor(item.coveredTo) + 1

  return (
    <div className="cover-poster">
      {item.coverUrl ? (
        <img src={item.coverUrl} alt={item.seriesTitle} loading="lazy" decoding="async" />
      ) : (
        <div className="cover-placeholder">{item.seriesTitle}</div>
      )}
      <div className="cover-scrim" />

      <div className="cover-corners">
        <div className="cover-corner cover-corner-left">
          <span className="cover-badge home-chapter-badge" data-tip={item.animeTitle}>
            {item.resumeChapterLabel ?? <Trans>ch. {next}</Trans>}
          </span>
        </div>
      </div>
      {children}

      <div className="cover-meta">
        <span className="cover-title" title={item.seriesTitle}>
          {item.seriesTitle}
        </span>
        <span className="home-chapter-label">
          <Trans>Anime ends at ch. {item.coveredTo}</Trans>
        </span>
      </div>
    </div>
  )
}
