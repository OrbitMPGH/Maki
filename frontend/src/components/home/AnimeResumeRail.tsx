import { Link } from 'react-router-dom'
import type { HomeAnimeResumeItem } from '../../api/animeResume'
import { Trans } from '@lingui/react/macro'

/**
 * Horizontal rail of series whose anime the reader finished but the manga hasn't caught up to.
 * Same card markup as {@link RecentlyAddedRail}, but the corner badge names the resume chapter
 * instead of a chapter count, and there is no Read button: the resume chapter may not even be
 * downloaded yet, so the card only ever opens the series page.
 */
export function AnimeResumeRail({ items }: { items: HomeAnimeResumeItem[] }) {
  return (
    <div className="discover-rail">
      {items.map((item) => (
        <div key={item.seriesId} className="discover-rail-item">
          <AnimeResumeCard item={item} />
        </div>
      ))}
    </div>
  )
}

function AnimeResumeCard({ item }: { item: HomeAnimeResumeItem }) {
  const next = Math.floor(item.coveredTo) + 1

  return (
    <Link to={`/series/${item.seriesId}`} className="cover-card" aria-label={item.seriesTitle}>
      <div className="cover-poster">
        {item.coverUrl ? (
          <img src={item.coverUrl} alt={item.seriesTitle} loading="lazy" decoding="async" />
        ) : (
          <div className="cover-placeholder">{item.seriesTitle}</div>
        )}
        <div className="cover-scrim" />

        <div className="cover-corner cover-corner-left">
          <span className="cover-badge" data-tip={item.animeTitle}>
            {item.resumeChapterLabel ?? <Trans>ch. {next}</Trans>}
          </span>
        </div>

        <div className="cover-meta">
          <span className="cover-title" title={item.seriesTitle}>
            {item.seriesTitle}
          </span>
          <span className="home-chapter-label">
            <Trans>Anime ends at ch. {item.coveredTo}</Trans>
          </span>
        </div>
      </div>
    </Link>
  )
}
