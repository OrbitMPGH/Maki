import { memo, type CSSProperties, type ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { Button } from '@mantine/core'
import { IconPlayerPlay, IconPlayerTrackNext } from '@tabler/icons-react'
import { Trans, useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'
import type { HomeReadingItem } from '../../api/hooks'
import { HeroBackdrop } from '../series/HeroBackdrop'
import { SectionHeader } from '../ui/SectionHeader'
import { relativeTime } from '../ui/time'
import { ReadingCardMenu, type ReadingRailKind } from './ReadingCardMenu'
import { ReadingRail } from './ReadingRail'

/**
 * The lead for a Home reading section: one hero card for the series in focus, and a Next up strip
 * of posters for the rest of the section under it. Each poster opens its chapter in the reader,
 * like every other reading card.
 *
 * The backdrop is `HeroBackdrop`, the same recipe the series page and Discover use.
 */
function accentStyle(item: HomeReadingItem): CSSProperties | undefined {
  return item.coverAccent ? ({ '--series-accent': item.coverAccent } as CSSProperties) : undefined
}

export function ContinueHero({
  items,
  rail,
  glance,
}: {
  items: HomeReadingItem[]
  rail: ReadingRailKind
  /** Home's glance, hosted here: a panel on the hero's right on wide screens, a strip above it otherwise. */
  glance?: ReactNode
}) {
  const [lead, ...rest] = items
  const { t } = useLingui()
  if (!lead) return null

  return (
    <>
      <div className="continue-lead">
        <ContinueHeroCard key={lead.chapterId} item={lead} rail={rail} />
        {glance}
      </div>
      {rest.length > 0 && (
        <>
          <SectionHeader
            icon={IconPlayerTrackNext}
            title={rail === 'jumpback' ? t`Next to jump back in` : t`Next up`}
            count={rest.length}
          />
          <ReadingRail items={rest} rail={rail} />
        </>
      )}
    </>
  )
}

const ContinueHeroCard = memo(function ContinueHeroCard({
  item,
  rail,
}: {
  item: HomeReadingItem
  rail: ReadingRailKind
}) {
  const { t } = useLingui()
  // Kavita-imported rows carry no slice length, so there is no honest fraction to draw.
  const hasProgress = item.pageCount > 0
  const resumePct = hasProgress ? Math.min(100, (item.page / item.pageCount) * 100) : 0
  // page 0 means the chapter was never opened, so "Resume" would be a lie.
  const started = item.page > 0
  const lastRead = relativeTime(item.lastReadAt)
  const { seriesTitle, chapterLabel, unreadChapters, pageCount } = item
  const pageNumber = item.page + 1

  return (
    <div className={item.coverAccent ? 'continue-hero has-accent' : 'continue-hero'} style={accentStyle(item)}>
      <HeroBackdrop coverUrl={item.coverUrl} />

      <div className="continue-hero-body">
        <Link
          to={`/read/${item.chapterId}`}
          className="continue-hero-poster"
          aria-label={
            started
              ? t`Resume ${seriesTitle}, ${chapterLabel}`
              : t`Start ${seriesTitle}, ${chapterLabel}`
          }
        >
          {item.coverUrl ? (
            <img src={item.coverUrl} alt="" decoding="async" />
          ) : (
            <span className="continue-hero-placeholder">{seriesTitle}</span>
          )}
        </Link>

        <div className="continue-hero-copy">
          <div className="continue-hero-eyebrow">
            <span>{rail === 'jumpback' ? <Trans>Jump back in</Trans> : <Trans>Continue reading</Trans>}</span>
            {lastRead && (
              <span className="continue-hero-eyebrow-when">
                <Trans>Last read {lastRead}</Trans>
              </span>
            )}
          </div>

          <h3 className="continue-hero-title">
            <Link to={`/series/${item.seriesId}`}>{seriesTitle}</Link>
          </h3>

          <div className="continue-hero-chapter tnum">
            <span>{chapterLabel}</span>
            {hasProgress && (
              <span className="continue-hero-pages">
                <Trans>
                  Page {pageNumber} of {pageCount}
                </Trans>
              </span>
            )}
          </div>

          {hasProgress && (
            <div
              className="continue-hero-bar"
              role="progressbar"
              aria-valuemin={0}
              aria-valuemax={pageCount}
              aria-valuenow={pageNumber}
              aria-label={t`Reading progress`}
            >
              <div className="continue-hero-fill" style={{ width: `${resumePct}%` }} />
            </div>
          )}

          {unreadChapters > 0 && (
            <span className="continue-hero-unread tnum">
              {plural(unreadChapters, { one: '# unread', other: '# unread' })}
            </span>
          )}

          <div className="continue-hero-actions">
            <Button component={Link} to={`/read/${item.chapterId}`} leftSection={<IconPlayerPlay size={16} />}>
              {started ? <Trans>Resume</Trans> : <Trans>Start chapter</Trans>}
            </Button>
            <Button component={Link} to={`/series/${item.seriesId}`} variant="default">
              <Trans>Open series</Trans>
            </Button>
            <ReadingCardMenu item={item} rail={rail} className="continue-hero-menu" variant="default" size={36} />
          </div>
        </div>
      </div>
    </div>
  )
})
