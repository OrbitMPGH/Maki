import { memo, useMemo } from 'react'
import { ActionIcon, Badge, Group, Menu, Rating, Stack, Text } from '@mantine/core'
import { IconArchiveOff, IconArrowRight, IconDots, IconFileOff, IconTrash } from '@tabler/icons-react'
import { Link } from 'react-router-dom'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { useChapters } from '../../api/hooks'
import { useSeriesReadProgress } from '../../api/reader'
import type { SeriesDto } from '../../api/types'
import { formatDate } from '../../format'
import { CoverCard, type OfflineAvailability } from '../ui/CoverCard'
import { SeriesRow } from '../ui/SeriesRow'
import { countSkipped } from './chapterTable'

export type ArchivedCardAction = 'restore' | 'removeFiles' | 'delete'

/**
 * A library card in the Archived view: the normal poster or row, with the reader's own rating,
 * how far they read, when it was archived and whether its files are gone. The actions menu is a
 * sibling of the card's link, never inside it, so picking an item does not also open the series.
 *
 * Memoized like the cards it wraps; keep `onRate` and `onAction` stable at the call site.
 */
export const ArchivedSeriesCard = memo(function ArchivedSeriesCard({
  series,
  variant,
  density,
  selectMode,
  selected,
  readTracking,
  onToggle,
  offline,
  canRestore,
  canRemoveFiles,
  canDelete,
  onRate,
  onAction,
}: {
  series: SeriesDto
  variant: 'grid' | 'list'
  density: 'compact' | 'default' | 'comfortable'
  selectMode: boolean
  selected: boolean
  readTracking: boolean
  onToggle: (id: number) => void
  offline?: OfflineAvailability
  canRestore: boolean
  canRemoveFiles: boolean
  canDelete: boolean
  onRate: (seriesId: number, rating: number | null) => void
  onAction: (action: ArchivedCardAction, series: SeriesDto) => void
}) {
  const { t } = useLingui()
  const { displayTitle } = series
  const read = series.readChapterCount ?? 0
  const total = series.wantedChapterCount || series.knownChapterCount || 0
  const date = series.archivedAt ? formatDate(series.archivedAt) : ''
  const filesRemoved = series.chapterFileCount === 0

  // Skipped chapters need a finished one to be skipped past, so a series with nothing read asks for neither list.
  const wantsSkipped = readTracking && read > 0
  const { data: chapters } = useChapters(series.id, wantsSkipped)
  const { data: progressRows } = useSeriesReadProgress(series.id, wantsSkipped)
  const skipped = useMemo(() => {
    if (!chapters || !progressRows) return 0
    return countSkipped(chapters, new Map(progressRows.map((p) => [p.chapterId, p])), null)
  }, [chapters, progressRows])

  const menu = !selectMode && (
    <Menu withinPortal position="bottom-end" shadow="md">
      <Menu.Target>
        <ActionIcon variant="subtle" color="var(--neutral)" size="sm" aria-label={t`Actions for ${displayTitle}`}>
          <IconDots size={16} />
        </ActionIcon>
      </Menu.Target>
      <Menu.Dropdown>
        <Menu.Item component={Link} to={`/series/${series.id}`} leftSection={<IconArrowRight size={14} />}>
          <Trans>Open</Trans>
        </Menu.Item>
        {canRestore && (
          <Menu.Item leftSection={<IconArchiveOff size={14} />} onClick={() => onAction('restore', series)}>
            <Trans context="series">Restore to library</Trans>
          </Menu.Item>
        )}
        {canRemoveFiles && !filesRemoved && (
          <Menu.Item leftSection={<IconFileOff size={14} />} onClick={() => onAction('removeFiles', series)}>
            <Trans>Remove files</Trans>
          </Menu.Item>
        )}
        {canDelete && (
          <>
            <Menu.Divider />
            <Menu.Item
              color="var(--danger)"
              leftSection={<IconTrash size={14} />}
              onClick={() => onAction('delete', series)}
            >
              <Trans>Delete</Trans>
            </Menu.Item>
          </>
        )}
      </Menu.Dropdown>
    </Menu>
  )

  const rating = (
    <Rating
      size="sm"
      count={5}
      fractions={2}
      value={series.rating ? series.rating / 2 : 0}
      readOnly={selectMode}
      onChange={(v) => onRate(series.id, Math.round(v * 2) || null)}
    />
  )

  const facts = (
    <>
      {readTracking && total > 0 && (
        <Text size="xs" c="var(--ink-3)" className="tnum">
          {t`Read ${read} of ${total}`}
        </Text>
      )}
      <Group gap={6} wrap="wrap">
        {date && (
          <Text size="xs" c="var(--ink-3)">
            {t({ message: `Archived ${date}`, context: 'series' })}
          </Text>
        )}
        {filesRemoved && (
          <Badge size="xs" variant="light" color="var(--neutral)">
            <Trans>Files removed</Trans>
          </Badge>
        )}
        {skipped > 0 && (
          <Badge size="xs" variant="light" color="var(--neutral)">
            <Plural value={skipped} one="# skipped" other="# skipped" />
          </Badge>
        )}
      </Group>
    </>
  )

  if (variant === 'list') {
    return (
      <Group gap="xs" wrap="wrap" align="center" justify="flex-end">
        <div style={{ flex: '1 1 18rem', minWidth: 0 }}>
          <SeriesRow
            series={series}
            selectMode={selectMode}
            selected={selected}
            readTracking={readTracking}
            density={density}
            onToggle={onToggle}
            offline={offline}
          />
        </div>
        <Stack gap={4} align="flex-end" style={{ flexShrink: 0 }}>
          <Group gap={4} wrap="nowrap">
            {rating}
            {menu}
          </Group>
          {facts}
        </Stack>
      </Group>
    )
  }

  return (
    <div>
      <CoverCard
        series={series}
        selectMode={selectMode}
        selected={selected}
        readTracking={readTracking}
        onToggle={onToggle}
        offline={offline}
      />
      <Stack gap={4} mt={8}>
        <Group justify="space-between" wrap="nowrap" gap={4}>
          {rating}
          {menu}
        </Group>
        {facts}
      </Stack>
    </div>
  )
})
