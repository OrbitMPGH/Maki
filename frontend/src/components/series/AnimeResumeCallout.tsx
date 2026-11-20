import { useState } from 'react'
import { Button, NumberInput, Stack, Text } from '@mantine/core'
import { Link } from 'react-router-dom'
import { Trans, useLingui } from '@lingui/react/macro'
import type { AnimeResume, SeriesAnimeResume } from '../../api/animeResume'
import { ConfirmDialog } from '../ui/ConfirmDialog'
import { Callout } from '../ui/Callout'

/**
 * "Start where the anime ended": a callout saying which anime the reader finished, how far it
 * carries the manga, and where to pick reading back up. Presentational: every action is a prop
 * callback, so the series page and the Discover modal each wire it to their own mutations.
 *
 * `variant` decides how much it can do: `library` has a series to act on (mark watched, jump into
 * the reader, dismiss), `catalogue` only has the fact itself, plus a link into the library when the
 * manga happens to already be there.
 */
export function AnimeResumeCallout({
  resume,
  variant,
  onMarkWatched,
  onRead,
  onDismiss,
  pending,
  inLibrarySeriesId,
}: {
  resume: AnimeResume | SeriesAnimeResume
  variant: 'library' | 'catalogue'
  onMarkWatched?: (coveredTo: number) => void
  onRead?: () => void
  onDismiss?: () => void
  /** Which action is running, so only its button shows pending and the rest go disabled. */
  pending?: 'mark' | 'read' | 'dismiss' | null
  inLibrarySeriesId?: number | null
}) {
  const { t } = useLingui()
  const [confirmOpen, setConfirmOpen] = useState(false)
  const { animeTitle, coveredTo, resumeAt, coveredLabel, nextLabel, nextFrom, nextTo, basis } = resume
  const unmarkedCount = 'unmarkedCount' in resume ? resume.unmarkedCount : undefined
  const [rangeEnd, setRangeEnd] = useState<number | string>(coveredTo)

  const copy =
    basis === 'seasonCount' && nextLabel ? (
      <Trans>
        You finished {animeTitle} ({coveredLabel}). That likely covers up to ch. {coveredTo};{' '}
        {nextLabel} adapts ch. {nextFrom} to {nextTo}. Start reading at ch. {resumeAt}, or watch{' '}
        {nextLabel} first.
      </Trans>
    ) : (
      <Trans>
        You finished {animeTitle}. The anime covers up to ch. {coveredTo}. Start reading at ch.{' '}
        {resumeAt}.
      </Trans>
    )

  const openConfirm = () => {
    setRangeEnd(coveredTo)
    setConfirmOpen(true)
  }

  const confirmMark = () => {
    const n = typeof rangeEnd === 'number' ? rangeEnd : Number(rangeEnd)
    if (!Number.isFinite(n) || n < 1) return
    setConfirmOpen(false)
    onMarkWatched?.(n)
  }

  const actions =
    variant === 'library' ? (
      <>
        {unmarkedCount !== 0 && (
          <Button
            size="xs"
            variant="light"
            color="var(--anime)"
            onClick={openConfirm}
            loading={pending === 'mark'}
            disabled={!!pending && pending !== 'mark'}
          >
            <Trans>Mark ch. 1 to {coveredTo} watched</Trans>
          </Button>
        )}
        <Button size="xs" variant="default" onClick={onRead} loading={pending === 'read'} disabled={!!pending && pending !== 'read'}>
          <Trans>Read ch. {resumeAt}</Trans>
        </Button>
        <Button size="xs" variant="subtle" color="var(--neutral)" onClick={onDismiss} disabled={!!pending}>
          <Trans>Not this anime?</Trans>
        </Button>
      </>
    ) : inLibrarySeriesId != null ? (
      <Button component={Link} to={`/series/${inLibrarySeriesId}`} size="xs" variant="subtle" color="var(--anime)">
        <Trans>Open series</Trans>
      </Button>
    ) : undefined

  return (
    <Callout tone="anime" actions={actions}>
      {copy}
      {variant === 'library' && (
        <ConfirmDialog
          opened={confirmOpen}
          onClose={() => setConfirmOpen(false)}
          title={t`Mark chapters watched`}
          confirmLabel={t`Mark watched`}
          loading={pending === 'mark'}
          onConfirm={confirmMark}
        >
          <Stack gap="sm">
            <NumberInput
              label={t`Last chapter to mark watched`}
              value={rangeEnd}
              onChange={setRangeEnd}
              min={1}
              max={nextTo ?? coveredTo}
            />
            <Text size="xs" c="var(--ink-3)">
              <Trans>
                Watched chapters don't count toward your reading stats. If you have a connected
                tracker, this moves the manga entry there to the same chapter.
              </Trans>
            </Text>
          </Stack>
        </ConfirmDialog>
      )}
    </Callout>
  )
}
