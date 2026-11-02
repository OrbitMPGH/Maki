import { memo, useCallback, useLayoutEffect, useRef, useState, type CSSProperties } from 'react'
import { Link } from 'react-router-dom'
import { VisuallyHidden } from '@mantine/core'
import { IconPlayerPlayFilled } from '@tabler/icons-react'
import { Trans, useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'
import type { HomeReadingItem } from '../../api/hooks'
import { relativeTime } from '../ui/time'

/**
 * Next up as a list of rows in a card beside the Continue hero: thumb, title, chapter and recency,
 * and a progress bar with the unread count. The list scrolls inside the card once it runs out of
 * hero, with a fade at whichever end has more. Each row opens its chapter in the reader, like
 * every other reading card.
 */
export function NextUpList({ items }: { items: HomeReadingItem[] }) {
  const { t } = useLingui()
  const listRef = useRef<HTMLDivElement>(null)
  const frame = useRef(0)
  const [reach, setReach] = useState({ up: false, down: false })

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
  }, [measure, items.length])

  return (
    <section className="next-up" aria-label={t`Next up`}>
      <div className="next-up-head">
        <h2 className="next-up-title">
          <Trans>Next up</Trans>
        </h2>
        <span className="next-up-count tnum">{items.length}</span>
      </div>
      <div
        ref={listRef}
        className="next-up-list"
        data-fade-start={reach.up || undefined}
        data-fade-end={reach.down || undefined}
      >
        {items.map((item) => (
          <NextUpRow key={item.chapterId} item={item} />
        ))}
      </div>
    </section>
  )
}

const NextUpRow = memo(function NextUpRow({ item }: { item: HomeReadingItem }) {
  const hasProgress = item.pageCount > 0
  const resumePct = hasProgress ? Math.min(100, (item.page / item.pageCount) * 100) : 0
  const started = item.page > 0
  const lastRead = relativeTime(item.lastReadAt)
  const { seriesTitle, chapterLabel, unreadChapters } = item
  const style = item.coverAccent ? ({ '--series-accent': item.coverAccent } as CSSProperties) : undefined

  return (
    <Link
      to={`/read/${item.chapterId}`}
      className={item.coverAccent ? 'next-up-row has-accent' : 'next-up-row'}
      style={style}
    >
      {/* The verb first, hidden: the row's text names the link, so a reader hears "Resume <title>, <chapter>". */}
      <VisuallyHidden>{started ? <Trans>Resume</Trans> : <Trans>Start</Trans>} </VisuallyHidden>
      <span className="next-up-thumb">
        {item.coverUrl && <img src={item.coverUrl} alt="" loading="lazy" decoding="async" />}
      </span>
      <span className="next-up-copy">
        <span className="next-up-name">{seriesTitle}</span>
        <span className="next-up-meta tnum">
          <span>{chapterLabel}</span>
          {lastRead && <span>{lastRead}</span>}
        </span>
        <span className="next-up-progress">
          {hasProgress && (
            <span className="next-up-bar" aria-hidden>
              <span style={{ width: `${resumePct}%` }} />
            </span>
          )}
          {unreadChapters > 0 && (
            <span className="next-up-unread tnum">{plural(unreadChapters, { one: '# unread', other: '# unread' })}</span>
          )}
        </span>
      </span>
      <span className="next-up-play" aria-hidden>
        <IconPlayerPlayFilled size={12} />
      </span>
    </Link>
  )
})
