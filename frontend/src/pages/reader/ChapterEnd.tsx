import { Button, Group } from '@mantine/core'
import { IconArrowLeft, IconArrowRight, IconNote } from '../../lib/icons'
import { type ReactNode, useEffect, useRef } from 'react'
import { Link } from 'react-router-dom'
import { Plural, Trans } from '@lingui/react/macro'
import { useReaderEnding, type ReaderManifest } from '../../api/reader'
import { useSeriesQueue } from '../../api/hooks'
import { useChapterNotes } from '../../api/notes'
import NoteBody from '../../components/notes/NoteBody'
import { useAuthorLabel } from '../../components/notes/useAuthorLabel'
import { formatDateTime, formatReadingTime } from '../../format'
import ChapterEndDownloads from './ChapterEndDownloads'
import ChapterEndOutlook from './ChapterEndOutlook'
import ChapterEndRating from './ChapterEndRating'
import { activeDownloads, seriesFinishedHere, seriesSecondsLeft } from './endScreen'

/**
 * Chapter numbers the jump to the next chapter passes over. Only whole chapters count: 19.5 to 20
 * and 1.2 to 2.1 are straight continuations, 19 to 22 skips 20 and 21.
 */
function skipped(current: number | null, next: number | null): [number, number] | null {
  if (current == null || next == null) return null
  const from = Math.floor(current) + 1
  const to = Math.floor(next) - 1
  return to >= from ? [from, to] : null
}

/** The newest two shared notes, or a way to leave the first one. Nothing while the list is unavailable. */
function ChapterEndNotes({ chapterId, onOpenNotes }: { chapterId: number; onOpenNotes: () => void }) {
  const { data } = useChapterNotes(chapterId)
  const authorLabel = useAuthorLabel()
  if (!data) return null
  const shared = data.filter((n) => n.visibility === 'shared' && n.deleted === null)
  const count = shared.length
  const newest = shared.slice(-2).reverse()
  return (
    <div className="reader-end-notes">
      {count > 0 ? (
        <>
          <span className="reader-end-eyebrow">
            <Trans>Notes</Trans>
          </span>
          {newest.map((note) => {
            const who = authorLabel(note)
            return (
              <div key={note.id} className="reader-end-note-item">
                <span className="reader-end-note-head">
                  {who} <time dateTime={note.createdAt}>{formatDateTime(note.createdAt)}</time>
                </span>
                <div className="reader-end-note-text">
                  <NoteBody text={note.body} />
                </div>
              </div>
            )
          })}
          <Button
            variant="subtle"
            color="gray"
            size="compact-sm"
            className="reader-end-quiet"
            leftSection={<IconNote size={16} />}
            onClick={onOpenNotes}
          >
            <Plural value={count} one="See all # note" other="See all # notes" />
          </Button>
        </>
      ) : (
        <Button
          variant="subtle"
          color="gray"
          size="compact-sm"
          className="reader-end-quiet"
          leftSection={<IconNote size={16} />}
          onClick={onOpenNotes}
        >
          <Trans>Add a note</Trans>
        </Button>
      )}
    </div>
  )
}

/**
 * Shown when a page turn runs off the last page and auto-advance is off, or there is nothing to
 * advance to. Names what comes next, so the second press is a choice made knowing where it goes.
 */
export default function ChapterEnd({
  manifest,
  readingCounted,
  rtl,
  onNext,
  onStay,
  onOpenNotes,
  ratingPrompt,
}: {
  manifest: ReaderManifest
  readingCounted: boolean
  rtl: boolean
  /** Takes the chapter to open, which is the live next one when it arrived after the manifest. */
  onNext: (chapterId: number) => void
  onStay: () => void
  onOpenNotes: () => void
  /** The "how is it so far?" line, when a checkpoint is waiting. */
  ratingPrompt?: ReactNode
}) {
  // Read live, so a chapter that finishes downloading while this is up shows as the next one. The
  // manifest stays as it was when the chapter opened; this overlays what has moved since.
  const { data: ending } = useReaderEnding(manifest.seriesId, manifest.chapterId)
  const live = ending ?? manifest
  const { seriesChapterCount, nextChapterId, nextChapterLabel, nextChapterNumber } = live
  const chapterLabel = manifest.label
  // The ending's read count already includes this chapter once its completing write has landed.
  const readCount = Math.min(
    seriesChapterCount,
    live.seriesReadCount + (readingCounted && !ending?.completed ? 1 : 0),
  )
  const percent = seriesChapterCount > 0 ? (readCount / seriesChapterCount) * 100 : 0
  const unread = Math.max(0, seriesChapterCount - readCount)
  const pending = Math.max(0, live.seriesWantedCount - seriesChapterCount)
  const gap = skipped(manifest.number, nextChapterNumber)
  const [gapFrom, gapTo] = gap ?? [0, 0]
  const hasNext = nextChapterId != null
  const finished = seriesFinishedHere(manifest, ending, readingCounted)
  const askRating = finished && ending != null && ending.rating == null && !ending.rateHintDismissed
  const timeLeft = finished ? null : seriesSecondsLeft(manifest.readingPace, readCount)
  const timeLeftText = timeLeft != null ? formatReadingTime(timeLeft) : ''
  const queue = useSeriesQueue(manifest.seriesId, !hasNext && pending > 0)
  const downloads = activeDownloads(queue.data?.items ?? [])
  const downloading = downloads.working.length + downloads.waiting.length > 0
  const forwardKey = rtl ? '←' : '→'
  // Focus lands on the heading, not a button: Space activates a button on key-up, so a button focused
  // by the very page turn that opened this screen would take that key-up as a click.
  const heading = useRef<HTMLHeadingElement>(null)
  useEffect(() => heading.current?.focus(), [])

  return (
    <div className="reader-end">
      {manifest.seriesCoverUrl && (
        <div
          className="reader-end-backdrop"
          style={{ backgroundImage: `url("${manifest.seriesCoverUrl}")` }}
          aria-hidden
        />
      )}

      <div className="reader-end-body">
        <div className="reader-end-finished">
          {manifest.seriesCoverUrl && (
            <img className="reader-end-cover" src={manifest.seriesCoverUrl} alt="" />
          )}
          <div className="reader-end-heading">
            <span className="reader-end-eyebrow">
              {hasNext ? (
                <Trans>Finished</Trans>
              ) : finished ? (
                <Trans>You finished the series</Trans>
              ) : unread === 0 ? (
                <Trans>You're caught up</Trans>
              ) : (
                <Trans>Last chapter on disk</Trans>
              )}
            </span>
            <h1 ref={heading} tabIndex={-1} className="reader-end-title" style={{ outline: 'none' }}>
              {chapterLabel}
            </h1>
            <span className="reader-end-series">{manifest.seriesTitle}</span>
            {seriesChapterCount > 0 && (
              <div className="reader-end-meter">
                <div className="reader-end-meter-track">
                  <div className="reader-end-meter-fill" style={{ width: `${percent}%` }} />
                </div>
                <span className="tnum">
                  <Trans>
                    {readCount} of {seriesChapterCount} read
                  </Trans>
                </span>
              </div>
            )}
            {timeLeft != null && (
              <span className="reader-end-timeleft tnum">
                <Trans>About {timeLeftText} left in the series</Trans>
              </span>
            )}
            {finished ? askRating && <ChapterEndRating seriesId={manifest.seriesId} soFar={ending?.provisionalRating ?? null} /> : ratingPrompt}
          </div>
        </div>

        {hasNext ? (
          <div className="reader-end-next">
            <span className="reader-end-eyebrow">
              <Trans>Up next</Trans>
            </span>
            <span className="reader-end-next-label">{nextChapterLabel}</span>
            {gap && (
              <span className="reader-end-note">
                {gapFrom === gapTo ? (
                  <Trans>Chapter {gapFrom} isn't downloaded, so this skips it.</Trans>
                ) : (
                  <Trans>
                    Chapters {gapFrom} to {gapTo} aren't downloaded, so this skips them.
                  </Trans>
                )}
              </span>
            )}
            <Group gap="xs" mt="md">
              <Button onClick={() => onNext(nextChapterId)} rightSection={<IconArrowRight size={16} />}>
                {nextChapterLabel ? <Trans>Read {nextChapterLabel}</Trans> : <Trans>Next chapter</Trans>}
              </Button>
              <Button variant="subtle" color="gray" className="reader-end-quiet" onClick={onStay}>
                <Trans>Stay here</Trans>
              </Button>
            </Group>
            <span className="reader-end-hint">
              <Trans>{forwardKey} or Space opens it, Esc goes back to the series.</Trans>
            </span>
          </div>
        ) : (
          <div className="reader-end-next">
            {downloading ? (
              <ChapterEndDownloads items={queue.data?.items ?? []} />
            ) : (
              <span className="reader-end-note">
                {finished ? (
                  <Trans>That's the whole series.</Trans>
                ) : pending > 0 ? (
                  <Plural
                    value={pending}
                    one="# more chapter is wanted. It shows up here once it downloads."
                    other="# more chapters are wanted. They show up here once they download."
                  />
                ) : unread > 0 ? (
                  <Plural
                    value={unread}
                    one="# earlier chapter is still unread."
                    other="# earlier chapters are still unread."
                  />
                ) : (
                  <Trans>That's every chapter on disk.</Trans>
                )}
              </span>
            )}
            {!finished && !downloading && unread === 0 && pending === 0 && (
              <ChapterEndOutlook seriesId={manifest.seriesId} />
            )}
            <Group gap="xs" mt="md">
              <Button component={Link} to={`/series/${manifest.seriesId}`} leftSection={<IconArrowLeft size={16} />}>
                <Trans>Back to series</Trans>
              </Button>
              <Button variant="subtle" color="gray" className="reader-end-quiet" onClick={onStay}>
                <Trans>Stay here</Trans>
              </Button>
            </Group>
          </div>
        )}

        <ChapterEndNotes chapterId={manifest.chapterId} onOpenNotes={onOpenNotes} />
      </div>
    </div>
  )
}
