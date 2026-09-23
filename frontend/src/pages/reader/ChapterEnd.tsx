import { Button, Group } from '@mantine/core'
import { IconArrowLeft, IconArrowRight } from '@tabler/icons-react'
import { Link } from 'react-router-dom'
import { Plural, Trans } from '@lingui/react/macro'
import type { ReaderManifest } from '../../api/reader'

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

/**
 * Shown when a page turn runs off the last page and auto-advance is off, or there is nothing to
 * advance to. Names what comes next, so the second press is a choice made knowing where it goes.
 */
export default function ChapterEnd({
  manifest,
  incognito,
  rtl,
  onNext,
  onStay,
}: {
  manifest: ReaderManifest
  incognito: boolean
  rtl: boolean
  onNext: () => void
  onStay: () => void
}) {
  const { label: chapterLabel, seriesChapterCount, nextChapterLabel } = manifest
  // Reaching this screen is reaching the last page, which the server counts as read, so the meter
  // counts it too rather than waiting for the next manifest. Incognito writes nothing.
  const readCount = Math.min(
    seriesChapterCount,
    manifest.seriesReadCount + (!incognito && !manifest.completed ? 1 : 0),
  )
  const percent = seriesChapterCount > 0 ? (readCount / seriesChapterCount) * 100 : 0
  const unread = Math.max(0, seriesChapterCount - readCount)
  const pending = Math.max(0, manifest.seriesWantedCount - seriesChapterCount)
  const gap = skipped(manifest.number, manifest.nextChapterNumber)
  const [gapFrom, gapTo] = gap ?? [0, 0]
  const hasNext = manifest.nextChapterId != null
  const forwardKey = rtl ? '←' : '→'

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
              ) : unread === 0 ? (
                <Trans>You're caught up</Trans>
              ) : (
                <Trans>Last chapter on disk</Trans>
              )}
            </span>
            <h1 className="reader-end-title">{chapterLabel}</h1>
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
              <Button onClick={onNext} rightSection={<IconArrowRight size={16} />}>
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
            <span className="reader-end-note">
              {pending > 0 ? (
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
      </div>
    </div>
  )
}
