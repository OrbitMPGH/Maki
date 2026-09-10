import { Link } from 'react-router-dom'
import { Button } from '@mantine/core'
import { IconPlayerPlay } from '@tabler/icons-react'
import type { HomeReadingItem } from '../../api/hooks'
import { HeroBackdrop } from '../series/HeroBackdrop'
import { relativeTime } from '../ui/time'

/** How many chapters get the hero treatment before the rest fall back to the rail. */
export const CONTINUE_LEAD_MAX = 3

/**
 * The lead for Home's Continue reading section: the most recent chapters as small hero bands,
 * with whatever is left over continuing in the rail underneath.
 *
 * It leads the *section*, not the page. Home's sections are user-orderable and individually
 * switchable in settings, so Continue reading is not reliably first and may be absent entirely;
 * a page-level hero would either move around or vanish.
 *
 * The backdrop is `HeroBackdrop`, the same component the series page, the Discover hero and the
 * Discover detail modal use. Those gradients are tuned as one recipe, so a local copy of them
 * drifts the moment any of the four is touched.
 */
export function ContinueLead({ items }: { items: HomeReadingItem[] }) {
  return (
    // The count drives how much the tile can spend on its title: one tile across the full row
    // carries a feature-sized title, three sharing it cannot.
    <div className="continue-lead-grid" data-count={Math.min(items.length, CONTINUE_LEAD_MAX)}>
      {items.map((item) => (
        <ContinueTile key={item.chapterId} item={item} />
      ))}
    </div>
  )
}

function ContinueTile({ item }: { item: HomeReadingItem }) {
  // Kavita-imported rows carry no slice length, so there is no honest fraction to draw.
  // Same rule as ReadingRail's card: no pageCount means no bar and no "page x of y".
  const hasProgress = item.pageCount > 0
  const resumePct = hasProgress ? Math.min(100, (item.page / item.pageCount) * 100) : 0
  // page 0 means the chapter was never opened, so "Resume" would be a lie.
  const started = item.page > 0
  const lastRead = relativeTime(item.lastReadAt)

  return (
    <div className="continue-tile">
      <HeroBackdrop coverUrl={item.coverUrl} />

      <div className="continue-tile-content">
        <Link
          to={`/read/${item.chapterId}`}
          className="continue-tile-poster"
          aria-label={`${started ? 'Resume' : 'Start'} ${item.seriesTitle}, ${item.chapterLabel}`}
        >
          {item.coverUrl ? (
            <img src={item.coverUrl} alt="" loading="lazy" decoding="async" />
          ) : (
            <span className="continue-tile-placeholder">{item.seriesTitle}</span>
          )}
        </Link>

        <div className="continue-tile-body">
          <div className="continue-tile-meta">
            {lastRead && <span>{lastRead}</span>}
            {item.unreadChapters > 0 && (
              <>
                <span className="continue-tile-dot" aria-hidden="true" />
                <span>{item.unreadChapters} unread</span>
              </>
            )}
          </div>

          <Link to={`/series/${item.seriesId}`} className="continue-tile-title">
            {item.seriesTitle}
          </Link>

          <div className="continue-tile-chapter">{item.chapterLabel}</div>

          {hasProgress && (
            <div className="continue-tile-progress">
              <div
                className="continue-tile-bar"
                role="progressbar"
                aria-valuemin={0}
                aria-valuemax={item.pageCount}
                aria-valuenow={item.page}
                aria-label="Reading progress"
              >
                <div className="continue-tile-fill" style={{ width: `${resumePct}%` }} />
              </div>
              <span className="continue-tile-pages tnum">
                {item.page + 1}/{item.pageCount}
              </span>
            </div>
          )}

          <Button
            component={Link}
            to={`/read/${item.chapterId}`}
            size="sm"
            leftSection={<IconPlayerPlay size={15} />}
            className="continue-tile-action"
          >
            {started ? 'Resume' : 'Start chapter'}
          </Button>
        </div>
      </div>
    </div>
  )
}
