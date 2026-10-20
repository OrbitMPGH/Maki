import { memo, useCallback, useLayoutEffect, useRef, useState, type CSSProperties } from 'react'
import { Link } from 'react-router-dom'
import { Button } from '@mantine/core'
import { useMediaQuery } from '@mantine/hooks'
import { IconChevronDown, IconChevronUp, IconPlayerPlay, IconPlayerPlayFilled } from '@tabler/icons-react'
import { Trans, useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'
import type { HomeReadingItem } from '../../api/hooks'
import { HeroBackdrop } from '../series/HeroBackdrop'
import { relativeTime } from '../ui/time'
import { ReadingCardMenu, type ReadingRailKind } from './ReadingCardMenu'

/** Rows the Next up list shows once it stacks under the hero, before "Show all". */
const STACKED_ROWS = 3
/** Matches the CSS breakpoint where `.continue-hero-grid` drops to one column. */
const STACKED_QUERY = '(max-width: 56.25em)'

/**
 * The lead for a Home reading section: one hero card for the series in focus, and a Next up list
 * of every series in the section beside it.
 *
 * Picking a row swaps it into the hero and leaves the list where it is, so the row you clicked
 * stays under the pointer. Each row also carries its own resume button, so every series is still
 * one click from the reader.
 *
 * The backdrop is `HeroBackdrop`, the same recipe the series page and Discover use.
 */
function accentStyle(item: HomeReadingItem): CSSProperties | undefined {
  return item.coverAccent ? ({ '--series-accent': item.coverAccent } as CSSProperties) : undefined
}

export function ContinueHero({ items, rail }: { items: HomeReadingItem[]; rail: ReadingRailKind }) {
  const stacked = useMediaQuery(STACKED_QUERY) ?? false
  const [activeSeriesId, setActiveSeriesId] = useState<number | null>(null)
  const gridRef = useRef<HTMLDivElement>(null)
  // Keyed by series rather than index: hiding a row or a refetch reorders the list under it.
  const active = items.find((i) => i.seriesId === activeSeriesId) ?? items[0]

  const pick = useCallback(
    (seriesId: number) => {
      setActiveSeriesId(seriesId)
      // Stacked, the hero sits above the list and is usually scrolled out of view by the time a
      // row is tapped.
      if (stacked) {
        const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches
        gridRef.current?.scrollIntoView({ block: 'start', behavior: reduced ? 'auto' : 'smooth' })
      }
    },
    [stacked],
  )

  if (!active) return null

  return (
    <div ref={gridRef} className="continue-hero-grid" data-solo={items.length === 1 || undefined}>
      <ContinueHeroCard key={active.chapterId} item={active} rail={rail} />
      {items.length > 1 && (
        <NextUpPanel items={items} activeSeriesId={active.seriesId} onPick={pick} stacked={stacked} rail={rail} />
      )}
    </div>
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
          {lastRead && (
            <div className="continue-hero-eyebrow">
              <Trans>Last read {lastRead}</Trans>
            </div>
          )}

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

function NextUpPanel({
  items,
  activeSeriesId,
  onPick,
  stacked,
  rail,
}: {
  items: HomeReadingItem[]
  activeSeriesId: number
  onPick: (seriesId: number) => void
  stacked: boolean
  rail: ReadingRailKind
}) {
  const { t } = useLingui()
  const listRef = useRef<HTMLDivElement>(null)
  const frame = useRef(0)
  const [reach, setReach] = useState({ up: false, down: false })
  const [expanded, setExpanded] = useState(false)
  // Stacked, the list stops scrolling inside itself (a scroller inside the page scroll is a trap
  // on a phone) and shortens to a few rows instead.
  const shown = stacked && !expanded ? items.slice(0, STACKED_ROWS) : items

  const measure = useCallback(() => {
    const list = listRef.current
    if (!list) return
    const max = list.scrollHeight - list.clientHeight
    const up = list.scrollTop > 1
    const down = max > 1 && list.scrollTop < max - 1
    setReach((prev) => (prev.up === up && prev.down === down ? prev : { up, down }))
  }, [])

  useLayoutEffect(() => {
    measure()
    const list = listRef.current
    if (!list) return
    const schedule = () => {
      cancelAnimationFrame(frame.current)
      frame.current = requestAnimationFrame(measure)
    }
    list.addEventListener('scroll', schedule, { passive: true })
    const resize = new ResizeObserver(schedule)
    resize.observe(list)
    return () => {
      cancelAnimationFrame(frame.current)
      list.removeEventListener('scroll', schedule)
      resize.disconnect()
    }
  }, [measure, shown.length, stacked])

  const move = (direction: -1 | 1) => {
    const list = listRef.current
    if (!list) return
    const row = list.firstElementChild?.getBoundingClientRect().height ?? 72
    const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches
    list.scrollBy({
      top: direction * Math.max(row, list.clientHeight - row),
      behavior: reduced ? 'auto' : 'smooth',
    })
  }

  const scrollable = !stacked && (reach.up || reach.down)

  return (
    <aside className="continue-queue" aria-label={rail === 'jumpback' ? t`Next up to jump back in` : t`Next up`}>
      <div className="continue-queue-head">
        <h3 className="continue-queue-title">
          <Trans>Next up</Trans>
        </h3>
        {scrollable && (
          <div className="continue-queue-arrows">
            <button
              type="button"
              className="rail-arrow"
              aria-label={t`Scroll back`}
              disabled={!reach.up}
              onClick={() => move(-1)}
            >
              <IconChevronUp size={16} stroke={2} aria-hidden />
            </button>
            <button
              type="button"
              className="rail-arrow"
              aria-label={t`Scroll forward`}
              disabled={!reach.down}
              onClick={() => move(1)}
            >
              <IconChevronDown size={16} stroke={2} aria-hidden />
            </button>
          </div>
        )}
      </div>

      <div
        ref={listRef}
        className="continue-queue-list"
        data-fade-start={(!stacked && reach.up) || undefined}
        data-fade-end={(!stacked && reach.down) || undefined}
      >
        {shown.map((item) => (
          <NextUpRow
            key={item.chapterId}
            item={item}
            selected={item.seriesId === activeSeriesId}
            onPick={onPick}
          />
        ))}
      </div>

      {stacked && items.length > STACKED_ROWS && (
        <button
          type="button"
          className="continue-queue-more"
          aria-expanded={expanded}
          onClick={() => setExpanded((e) => !e)}
        >
          {expanded ? <Trans>Show fewer</Trans> : <Trans>Show all {items.length}</Trans>}
        </button>
      )}
    </aside>
  )
}

// Memoized: `item` keeps its reference across renders, so switching the hero re-renders two rows, not all of them.
const NextUpRow = memo(function NextUpRow({
  item,
  selected,
  onPick,
}: {
  item: HomeReadingItem
  selected: boolean
  onPick: (seriesId: number) => void
}) {
  const { t } = useLingui()
  const hasProgress = item.pageCount > 0
  const resumePct = hasProgress ? Math.min(100, (item.page / item.pageCount) * 100) : 0
  const started = item.page > 0
  const lastRead = relativeTime(item.lastReadAt)
  const { seriesTitle, chapterLabel, unreadChapters } = item

  return (
    <div
      className={item.coverAccent ? 'continue-queue-row has-accent' : 'continue-queue-row'}
      style={accentStyle(item)}
      data-selected={selected || undefined}
    >
      <button
        type="button"
        className="continue-queue-pick"
        aria-pressed={selected}
        onClick={() => onPick(item.seriesId)}
      >
        <span className="continue-queue-thumb">
          {item.coverUrl && <img src={item.coverUrl} alt="" loading="lazy" decoding="async" />}
        </span>
        <span className="continue-queue-copy">
          <span className="continue-queue-name">{seriesTitle}</span>
          <span className="continue-queue-meta tnum">
            <span>{chapterLabel}</span>
            {lastRead && <span>{lastRead}</span>}
          </span>
          <span className="continue-queue-progress">
            {hasProgress && (
              <span className="continue-queue-bar" aria-hidden>
                <span style={{ width: `${resumePct}%` }} />
              </span>
            )}
            {unreadChapters > 0 && (
              <span className="continue-queue-unread tnum">
                {plural(unreadChapters, { one: '# unread', other: '# unread' })}
              </span>
            )}
          </span>
        </span>
      </button>
      <Link
        to={`/read/${item.chapterId}`}
        className="continue-queue-play"
        aria-label={
          started ? t`Resume ${seriesTitle}, ${chapterLabel}` : t`Start ${seriesTitle}, ${chapterLabel}`
        }
      >
        <IconPlayerPlayFilled size={13} aria-hidden />
      </Link>
    </div>
  )
})
