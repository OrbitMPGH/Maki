import { memo, type CSSProperties, type ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { Button, Skeleton } from '@mantine/core'
import { IconPlayerPlay, IconPlayerTrackNext } from '@tabler/icons-react'
import { Trans, useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'
import type { HomeReadingItem } from '../../api/hooks'
import { useCoverTint } from '../../lib/coverTint'
import { HeroBackdrop } from '../series/HeroBackdrop'
import { RailSkeleton } from '../ui/RailSkeleton'
import { SectionHeader } from '../ui/SectionHeader'
import { relativeTime } from '../ui/time'
import { NextUpList } from './NextUpList'
import { ReadingCardMenu } from './ReadingCardMenu'
import { ReadingRail } from './ReadingRail'

/** Where the rest of the section goes: a tray of posters under the hero, or a list in a card beside it. */
export type HeroQueue = 'tray' | 'beside'

/**
 * The lead for Home's Continue reading: one hero card for the series in focus, and a Next up strip
 * of posters for the rest of the section in a tray joined to the card's foot, so the two read as
 * one object rather than a hero and one more rail. Each poster opens its chapter in the reader,
 * like every other reading card.
 *
 * The backdrop is `HeroBackdrop`, the same recipe the series page and Discover use.
 */
function accentStyle(item: HomeReadingItem): CSSProperties | undefined {
  return item.coverAccent ? ({ '--series-accent': item.coverAccent } as CSSProperties) : undefined
}

export function ContinueHero({
  items,
  glance,
  queue = 'tray',
}: {
  items: HomeReadingItem[]
  /**
   * Home's glance (`ReadingWeeks`). Rendered twice and placed by width (see `.continue-glance-below`
   * in theme.css): inside the hero as a column on its right when wide or a row under the actions on a
   * phone, and as a raised card under the lead in between.
   */
  glance?: ReactNode
  queue?: HeroQueue
}) {
  const [lead, ...rest] = items
  const { t } = useLingui()
  useCoverTint(lead?.coverUrl)
  if (!lead) return null

  const lane =
    queue === 'beside' ? (
      <div className="continue-lead" data-queue="beside" data-solo={rest.length === 0 || undefined}>
        <ContinueHeroCard key={lead.chapterId} item={lead} glance={glance} />
        {rest.length > 0 && <NextUpList items={rest} />}
      </div>
    ) : (
      <div className="continue-lead">
        <ContinueHeroCard key={lead.chapterId} item={lead} glance={glance} />
        {rest.length > 0 && (
          <div className="continue-tray">
            <SectionHeader icon={IconPlayerTrackNext} title={t`Next up`} count={rest.length} />
            <ReadingRail items={rest} />
          </div>
        )}
      </div>
    )

  return (
    <div className="continue-section" data-queue={queue}>
      {lane}
      {glance && <div className="continue-glance-below">{glance}</div>}
    </div>
  )
}

/**
 * Continue reading's shape while it loads, built from the same classes so the page holds still
 * when the hero lands: two rail-sized skeletons in its place let everything under them jump by
 * several hundred pixels.
 */
export function ContinueHeroSkeleton({ queue = 'tray' }: { queue?: HeroQueue }) {
  return (
    <div className="continue-section" data-queue={queue} aria-hidden>
      <div className="continue-lead" data-queue={queue === 'beside' ? 'beside' : undefined} data-solo={queue === 'beside' || undefined}>
        <div className="continue-hero">
          <Skeleton pos="absolute" inset={0} radius={0} />
        </div>
        {queue === 'tray' && (
          <div className="continue-tray">
            <Skeleton h={18} w={110} mb="sm" />
            <RailSkeleton flush />
          </div>
        )}
      </div>
    </div>
  )
}

const ContinueHeroCard = memo(function ContinueHeroCard({
  item,
  glance,
}: {
  item: HomeReadingItem
  glance?: ReactNode
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
          data-series-cover={item.seriesId}
          aria-label={
            started
              ? t`Resume ${seriesTitle}, ${chapterLabel}`
              : t`Start ${seriesTitle}, ${chapterLabel}`
          }
        >
          {item.coverUrl ? (
            <img src={item.coverUrl} alt="" decoding="async" fetchPriority="high" />
          ) : (
            <span className="continue-hero-placeholder">{seriesTitle}</span>
          )}
        </Link>

        <div className="continue-hero-copy">
          <div className="continue-hero-label">
            {started ? <Trans>Continue reading</Trans> : <Trans>New chapter</Trans>}
          </div>

          <h2 className="continue-hero-title">
            <Link to={`/series/${item.seriesId}`}>{seriesTitle}</Link>
          </h2>

          {lastRead && (
            <div className="continue-hero-when">
              <Trans>Last read {lastRead}</Trans>
            </div>
          )}

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
            <ReadingCardMenu item={item} className="continue-hero-menu" variant="default" size={36} />
          </div>
        </div>
        {/* Last in the DOM so Resume stays the first tab stop; the grid places it. */}
        {glance}
      </div>
    </div>
  )
})
