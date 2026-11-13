import { forwardRef, memo, useMemo } from 'react'
import { Link } from 'react-router-dom'
import { ActionIcon, Button, Checkbox, Rating, Tooltip } from '@mantine/core'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { IconArchiveOff, IconCheck, IconFileOff, IconTrash, IconX } from '../../lib/icons'
import { useArchivePreview, useChapters } from '../../api/hooks'
import { useSeriesReadProgress } from '../../api/reader'
import type { SeriesDto } from '../../api/types'
import { formatBytes, formatDate } from '../../format'
import { isProvisional, shownRating } from '../../lib/rating'
import { SignalStatus } from '../../pages/discover/SignalStatus'
import { countSkipped } from '../series/chapterTable'
import type { ArchivedCardAction } from '../series/ArchivedSeriesCard'
import { Panel } from '../ui/Panel'
import { Thumb } from '../ui/Thumb'

interface RowProps {
  selectMode: boolean
  selectedIds: ReadonlySet<number>
  readTracking: boolean
  onToggle: (id: number, shiftKey?: boolean) => void
  canRestore: boolean
  canRemoveFiles: boolean
  canDelete: boolean
  onRate: (seriesId: number, rating: number | null) => void
  onAction: (action: ArchivedCardAction, series: SeriesDto) => void
}

const ArchivedRow = memo(function ArchivedRow({
  series,
  selected,
  selectMode,
  readTracking,
  onToggle,
  canRestore,
  canRemoveFiles,
  canDelete,
  onRate,
  onAction,
}: Omit<RowProps, 'selectedIds'> & { series: SeriesDto; selected: boolean }) {
  const { t } = useLingui()
  const { displayTitle } = series
  const read = series.readChapterCount ?? 0
  const total = series.wantedChapterCount || series.knownChapterCount || 0
  const date = series.archivedAt ? formatDate(series.archivedAt) : ''
  const kept = series.chapterFileCount > 0
  const filesRemoved = !kept && series.removedChapterCount > 0

  const wantsSkipped = readTracking && read > 0
  const { data: chapters } = useChapters(series.id, wantsSkipped)
  const { data: progressRows } = useSeriesReadProgress(series.id, wantsSkipped)
  const skipped = useMemo(() => {
    if (!chapters || !progressRows) return 0
    return countSkipped(chapters, new Map(progressRows.map((p) => [p.chapterId, p])), null)
  }, [chapters, progressRows])

  // The size stats every file, so only rows that hold files ask, and only for those who may restore.
  const { data: preview } = useArchivePreview(series.id, kept && canRestore)
  const onDisk = preview ? formatBytes(preview.freeableBytes) : null

  const score = shownRating(series)
  const showRead = readTracking && total > 0

  return (
    <div
      className="archived-row"
      data-selected={selected || undefined}
      onClick={selectMode ? (e) => onToggle(series.id, e.shiftKey) : undefined}
    >
      {selectMode && (
        <Checkbox
          checked={selected}
          onChange={(e) => onToggle(series.id, (e.nativeEvent as MouseEvent).shiftKey === true)}
          aria-label={t`Select ${displayTitle}`}
          onClick={(e) => e.stopPropagation()}
        />
      )}
      <Thumb src={series.coverUrl} size="md" aria-hidden data-series-cover={series.id} />
      <div className="archived-row-main">
        <div className="archived-row-title series-title" data-lines="1">
          {selectMode ? (
            displayTitle
          ) : (
            <Link to={`/series/${series.id}`} className="stats-series-link">
              {displayTitle}
            </Link>
          )}
        </div>
        <div className="archived-row-meta tnum">
          {showRead && t`Read ${read} of ${total}`}
          {skipped > 0 && (
            <>
              {showRead && ' · '}
              <Plural value={skipped} one="# skipped" other="# skipped" />
            </>
          )}
        </div>
      </div>
      <div className="archived-row-rating">
        <Rating
          size="sm"
          count={5}
          fractions={2}
          value={score ? score / 2 : 0}
          color={isProvisional(series) ? 'var(--ink-3)' : undefined}
          readOnly={selectMode}
          role="group"
          aria-label={t`Your rating for ${displayTitle}`}
          onChange={(v) => onRate(series.id, Math.round(v * 2) || null)}
        />
        {score && !selectMode ? (
          <Tooltip label={t`Clear rating`} withArrow>
            <ActionIcon
              size="xs"
              variant="subtle"
              color="var(--neutral)"
              onClick={() => onRate(series.id, null)}
              aria-label={t`Clear rating`}
            >
              <IconX size={12} />
            </ActionIcon>
          </Tooltip>
        ) : null}
      </div>
      <div className="archived-row-date tnum">{date && t({ message: `Archived ${date}`, context: 'series' })}</div>
      <div className="archived-row-files">
        <SignalStatus
          icon={kept ? IconCheck : IconFileOff}
          label={kept ? t`Kept` : t`Removed`}
          color={kept ? 'var(--ok)' : 'var(--ink-3)'}
        />
        <div className="archived-row-meta tnum">
          {kept ? (
            onDisk ? (
              t`${onDisk} on disk`
            ) : (
              <Plural value={series.chapterFileCount} one="# file" other="# files" />
            )
          ) : filesRemoved ? (
            <Plural value={series.removedChapterCount} one="# chapter removed" other="# chapters removed" />
          ) : null}
        </div>
      </div>
      {!selectMode && (
        <div className="archived-row-actions">
          {canRestore && (
            <Button
              size="compact-sm"
              variant="default"
              leftSection={<IconArchiveOff size={16} />}
              aria-label={t`Restore ${displayTitle}`}
              onClick={() => onAction('restore', series)}
            >
              <Trans context="series">Restore</Trans>
            </Button>
          )}
          {canRemoveFiles && kept && (
            <Button
              size="compact-sm"
              variant="subtle"
              color="var(--danger)"
              leftSection={<IconFileOff size={16} />}
              aria-label={t`Delete files for ${displayTitle}`}
              onClick={() => onAction('removeFiles', series)}
            >
              <Trans>Delete files</Trans>
            </Button>
          )}
          {canDelete && (
            <Tooltip label={t`Delete series`} withArrow>
              <ActionIcon
                variant="subtle"
                color="var(--danger)"
                aria-label={t`Delete ${displayTitle}`}
                onClick={() => onAction('delete', series)}
              >
                <IconTrash size={16} />
              </ActionIcon>
            </Tooltip>
          )}
        </div>
      )}
    </div>
  )
})

/**
 * The Archived view as hairline rows in one panel. Pass the slice to draw (the page windows long
 * lists) and keep `onRate` and `onAction` stable, as with `ArchivedSeriesCard`. The ref lands on the
 * element that holds the rows, for `useWindowedRows`.
 */
export const ArchivedList = forwardRef<HTMLDivElement, { series: SeriesDto[] } & RowProps>(function ArchivedList(
  { series, selectedIds, ...rowProps },
  ref,
) {
  return (
    <Panel p={0} className="archived-list">
      <div ref={ref}>
        {series.map((s) => (
          <ArchivedRow key={s.id} series={s} selected={selectedIds.has(s.id)} {...rowProps} />
        ))}
      </div>
    </Panel>
  )
})
