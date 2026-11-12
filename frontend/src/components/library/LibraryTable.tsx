import { type CSSProperties, type ReactNode, type Ref, memo } from 'react'
import { Link } from 'react-router-dom'
import { useLingui } from '@lingui/react'
import { Trans, useLingui as useLinguiMacro } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'
import {
  IconCircleCheckFilled,
  IconCloudOff,
  IconDeviceMobileCheck,
  IconSortAscending,
  IconSortDescending,
  IconStarFilled,
} from '../../lib/icons'
import type { SeriesDto, TagDto } from '../../api/types'
import type { OfflineAvailability } from '../ui/CoverCard'
import { TagChip } from '../ui/TagChip'
import { badgeFill, seriesProgressVisual, seriesStatusVisual } from '../ui/status'
import { relativeTime } from '../ui/time'
import { useLabel } from '../../i18n-context'
import { formatBytes, formatCalendarDate, formatDecimal, formatRecentDate } from '../../format'
import { shownRating, isProvisional } from '../../lib/rating'
import {
  LIBRARY_COLUMN_LABELS,
  LIBRARY_COLUMN_SORTS,
  LIBRARY_COLUMN_TRACKS,
  type LibraryColumn,
} from './libraryColumns'

const TITLE_TRACK = 'minmax(240px, 2fr)'
const CHECK_TRACK = '36px'

/** The grid template every row of one table shares, and the width under which it scrolls sideways. */
function libraryTableLayout(columns: readonly LibraryColumn[], selectMode: boolean): CSSProperties {
  const tracks = [...(selectMode ? [CHECK_TRACK] : []), TITLE_TRACK, ...columns.map((c) => LIBRARY_COLUMN_TRACKS[c])]
  const minWidth = tracks.reduce((sum, track) => sum + Number(/(\d+)px/.exec(track)?.[1] ?? 0), 0)
  return {
    '--library-table-cols': tracks.join(' '),
    '--library-table-min': `${minWidth + 12 * tracks.length}px`,
  } as CSSProperties
}

/** The scrolling frame the header and the rows sit in. */
export function LibraryTableFrame({
  columns,
  selectMode,
  children,
}: {
  columns: readonly LibraryColumn[]
  selectMode: boolean
  children: ReactNode
}) {
  const { t } = useLinguiMacro()
  return (
    <div className="library-table" role="table" aria-label={t`Library`} style={libraryTableLayout(columns, selectMode)}>
      {children}
    </div>
  )
}

/**
 * The header row. A header whose column has a Library sort is a button that sets it, or reverses
 * it when it is already the sort; the rest are plain labels.
 */
export function LibraryTableHeader({
  columns,
  selectMode,
  sort,
  sortDir,
  sorts,
  onSort,
}: {
  columns: readonly LibraryColumn[]
  selectMode: boolean
  sort: string
  sortDir: 'asc' | 'desc'
  /** The sorts this view offers; a column whose sort is missing here is not clickable. */
  sorts: readonly string[]
  onSort: (sort: string) => void
}) {
  const { _ } = useLingui()
  const { t } = useLinguiMacro()
  const header = (key: LibraryColumn | 'title', label: string) => {
    const value = LIBRARY_COLUMN_SORTS[key]
    const sortable = value != null && sorts.includes(value)
    const active = sortable && sort === value
    const ariaSort = active ? (sortDir === 'asc' ? 'ascending' : 'descending') : undefined
    return (
      <div key={key} role="columnheader" aria-sort={ariaSort} className="library-table-th" data-col={key}>
        {sortable ? (
          <button type="button" className="library-table-sort" data-active={active || undefined} onClick={() => onSort(value)}>
            <span>{label}</span>
            {active &&
              (sortDir === 'asc' ? <IconSortAscending size={14} aria-hidden /> : <IconSortDescending size={14} aria-hidden />)}
          </button>
        ) : (
          <span>{label}</span>
        )}
      </div>
    )
  }
  return (
    <div role="rowgroup" className="library-table-head">
      <div role="row" className="library-table-row">
        {selectMode && <div role="columnheader" className="library-table-th" aria-label={t`Selected`} />}
        {header('title', t`Title`)}
        {columns.map((c) => header(c, _(LIBRARY_COLUMN_LABELS[c])))}
      </div>
    </div>
  )
}

/** The element the windowing hook measures: rows only, one per child. */
export function LibraryTableBody({
  innerRef,
  children,
}: {
  innerRef?: Ref<HTMLDivElement>
  children: ReactNode
}) {
  return (
    <div ref={innerRef} role="rowgroup" className="library-table-body">
      {children}
    </div>
  )
}

const Empty = () => <span className="library-table-empty">–</span>

export const LibraryTableRow = memo(function LibraryTableRow({
  series,
  columns,
  selectMode,
  selected,
  readTracking,
  onToggle,
  offline,
  tagsById,
  profileNames,
  sourceLabel,
}: {
  series: SeriesDto
  columns: readonly LibraryColumn[]
  selectMode: boolean
  selected: boolean
  readTracking: boolean
  onToggle: (id: number, shiftKey?: boolean) => void
  offline?: OfflineAvailability
  tagsById: ReadonlyMap<number, TagDto>
  profileNames: ReadonlyMap<number, string>
  sourceLabel: (key: string) => string
}) {
  const renderLabel = useLabel()
  const { t } = useLinguiMacro()
  const progress = seriesProgressVisual(series, readTracking)

  const cell = (column: LibraryColumn): ReactNode => {
    switch (column) {
      case 'status': {
        const status = seriesStatusVisual(series.status)
        return (
          <span className="cover-badge" style={badgeFill(status.color)}>
            <status.Icon size={11} />
            {renderLabel(status.label)}
          </span>
        )
      }
      case 'chapters': {
        const { total, have, pct, complete } = progress
        return (
          <span className="library-table-progress">
            <span className="row-bar">
              <span className="cover-bar-fill" data-complete={complete || undefined} style={{ width: `${pct}%`, display: 'block' }} />
            </span>
            <span className="tnum">{total > 0 ? `${have}/${total}` : have}</span>
          </span>
        )
      }
      case 'unread': {
        const { unread } = progress
        if (unread === null) return <Empty />
        if (unread === 0) {
          return (
            <span className="cover-badge cover-badge-read" data-tip={t`All downloaded chapters read`}>
              <IconCircleCheckFilled size={11} />
              <Trans>Read</Trans>
            </span>
          )
        }
        const tip = plural(unread, { one: '# unread', other: '# unread' })
        return (
          <span className="cover-badge cover-badge-unread" data-tip={tip} role="img" aria-label={tip}>
            {unread}
          </span>
        )
      }
      case 'added':
        return <span className="tnum">{formatRecentDate(series.added)}</span>
      case 'lastRead':
        return series.lastReadAt ? (
          <span className="tnum" title={formatCalendarDate(series.lastReadAt)}>
            {relativeTime(series.lastReadAt, 'narrow')}
          </span>
        ) : (
          <Empty />
        )
      case 'released':
        return series.latestChapterAt ? (
          <span className="tnum" title={formatCalendarDate(series.latestChapterAt)}>
            {relativeTime(series.latestChapterAt, 'narrow')}
          </span>
        ) : (
          <Empty />
        )
      case 'size':
        return series.sizeOnDisk != null ? <span className="tnum">{formatBytes(series.sizeOnDisk)}</span> : <Empty />
      case 'sources': {
        const linked = series.sources ?? []
        if (linked.length === 0) return <Empty />
        const enabled = new Set(series.enabledSources ?? [])
        return (
          <span className="library-table-chips">
            {linked.map((key) => (
              <TagChip
                key={key}
                size="sm"
                dot={enabled.has(key) ? 'var(--ok)' : 'var(--ink-4)'}
                title={enabled.has(key) ? undefined : t`Disabled`}
                className={enabled.has(key) ? undefined : 'library-table-chip-off'}
              >
                {sourceLabel(key)}
              </TagChip>
            ))}
          </span>
        )
      }
      case 'quality': {
        const name = series.upgradeProfileId != null ? profileNames.get(series.upgradeProfileId) : undefined
        return (
          <TagChip size="sm" dot={series.upgradeProfileId != null ? 'var(--brand)' : undefined}>
            {series.upgradeProfileId == null ? t`Instance default` : (name ?? String(series.upgradeProfileId))}
          </TagChip>
        )
      }
      case 'nextCheck': {
        const at = series.nextCheckAt
        if (!at) return <Empty />
        const due = new Date(at).getTime() <= Date.now()
        return due ? (
          <span className="cover-badge" style={badgeFill('var(--info)')}>
            <Trans>Due</Trans>
          </span>
        ) : (
          <span className="tnum" title={formatCalendarDate(at)}>
            {relativeTime(at, 'narrow')}
          </span>
        )
      }
      case 'year':
        return series.year != null ? <span className="tnum">{series.year}</span> : <Empty />
      case 'author': {
        const name = (series.authorStory || series.authorArt || '').trim()
        return name ? <span className="library-table-text" title={name}>{name}</span> : <Empty />
      }
      case 'score':
        return series.communityRating != null ? (
          <span className="library-table-score">
            <IconStarFilled size={12} aria-hidden />
            <span className="tnum">{formatDecimal(series.communityRating)}</span>
          </span>
        ) : (
          <Empty />
        )
      case 'rating': {
        const rating = shownRating(series)
        if (rating == null) return <Empty />
        const soFar = isProvisional(series)
        return (
          <span className="library-table-score" data-provisional={soFar || undefined} title={soFar ? t`So far` : undefined}>
            <IconStarFilled size={12} aria-hidden />
            <span className="tnum">{rating}</span>
          </span>
        )
      }
      case 'tags': {
        const tags = (series.tagIds ?? []).map((id) => tagsById.get(id)).filter((tag): tag is TagDto => tag != null)
        if (tags.length === 0) return <Empty />
        return (
          <span className="library-table-chips">
            {tags.map((tag) => (
              <TagChip key={tag.id} size="sm" dot={`var(--mantine-color-${tag.color}-6)`}>
                {tag.label}
              </TagChip>
            ))}
          </span>
        )
      }
    }
  }

  return (
    <div
      role="row"
      className="library-table-row"
      data-selected={selected || undefined}
      data-offline={offline}
      onClick={(e) => {
        if (!selectMode) return
        if ((e.target as HTMLElement).closest('a')) return
        onToggle(series.id, e.shiftKey)
      }}
    >
      {selectMode && (
        <div role="cell" className="library-table-td">
          <span className="row-check library-table-check" data-checked={selected || undefined} aria-hidden />
        </div>
      )}
      <div role="cell" className="library-table-td library-table-title-cell">
        <Link
          to={`/series/${series.id}`}
          className="library-table-title"
          aria-pressed={selectMode ? selected : undefined}
          onClick={(e) => {
            if (selectMode) {
              e.preventDefault()
              onToggle(series.id, e.shiftKey)
            }
          }}
        >
          <span className="library-table-thumb row-cover">
            {series.coverUrl ? <img src={series.coverUrl} alt="" loading="lazy" decoding="async" /> : null}
          </span>
          <span className="library-table-name" title={series.title}>
            {series.displayTitle}
          </span>
          {offline === 'saved' && (
            <span className="cover-badge" data-offline-badge="saved" data-tip={t`Saved on this device`}>
              <IconDeviceMobileCheck size={11} />
            </span>
          )}
          {offline === 'unavailable' && (
            <span className="cover-badge" data-offline-badge="unavailable" data-tip={t`Not available offline`}>
              <IconCloudOff size={11} />
            </span>
          )}
        </Link>
      </div>
      {columns.map((c) => (
        <div key={c} role="cell" className="library-table-td" data-col={c}>
          {cell(c)}
        </div>
      ))}
    </div>
  )
})
