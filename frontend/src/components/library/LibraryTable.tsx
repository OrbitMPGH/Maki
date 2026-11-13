import { type CSSProperties, type ReactNode, type Ref, createContext, memo, useContext, useRef } from 'react'
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
} from '../../lib/icons'
import type { SeriesDto, TagDto } from '../../api/types'
import type { OfflineAvailability } from '../ui/CoverCard'
import { TagChip } from '../ui/TagChip'
import { ScoreInline, YourScoreInline } from '../ui/ScoreInline'
import { seriesProgressVisual, seriesStatusVisual, statusColor } from '../ui/status'
import { LightBadge } from '../series/lightMantine'
import { relativeTime } from '../ui/time'
import { useLabel } from '../../i18n-context'
import { formatBytes, formatCalendarDate, formatRecentDate } from '../../format'
import { shownRating, isProvisional } from '../../lib/rating'
import {
  LIBRARY_COLUMN_LABELS,
  LIBRARY_COLUMN_SORTS,
  LIBRARY_COLUMN_WIDTHS,
  type LibraryColumn,
} from './libraryColumns'
import { type ColumnSpec, useColumnWidths } from '../../lib/useColumnWidths'
import { Thumb } from '../ui/Thumb'

const TITLE_MIN = 240
const CHECK_TRACK = '36px'
const WIDTHS_STORAGE_KEY = 'library-table-widths'

type TableColumn = LibraryColumn | 'title'

/**
 * Title's default of 0 means it stretches over whatever the other columns leave. The first resize
 * of any column pins it at its laid-out width, so a drag moves every column after the divider.
 */
const TABLE_COLUMN_WIDTHS: Record<TableColumn, ColumnSpec> = {
  title: { width: 0, min: TITLE_MIN, max: 1200 },
  ...LIBRARY_COLUMN_WIDTHS,
}

/** The grid template every row of one table shares, and the width under which it scrolls sideways. */
function libraryTableLayout(
  columns: readonly LibraryColumn[],
  selectMode: boolean,
  widths: Record<TableColumn, number>,
): Record<string, string> {
  const title = widths.title > 0 ? `${widths.title}px` : `minmax(${TITLE_MIN}px, 1fr)`
  const tracks = [...(selectMode ? [CHECK_TRACK] : []), title, ...columns.map((c) => `${widths[c]}px`)]
  const minWidth = tracks.reduce((sum, track) => sum + Number(/(\d+)px/.exec(track)?.[1] ?? 0), 0)
  return {
    '--library-table-cols': tracks.join(' '),
    '--library-table-min': `${minWidth + 12 * tracks.length}px`,
  }
}

type ResizeHandle = ReturnType<typeof useColumnWidths<TableColumn>>['handleProps']
const ResizeContext = createContext<ResizeHandle | null>(null)

/** The scrolling frame the header and the rows sit in. It owns the column widths. */
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
  const frame = useRef<HTMLDivElement>(null)
  // The rows are grid items, so a drag moves the shared template, not the header cell the hook
  // sizes; that cell's own width is taken straight back off.
  const { widths, handleProps } = useColumnWidths(
    WIDTHS_STORAGE_KEY,
    TABLE_COLUMN_WIDTHS,
    (live, key) => {
      const el = frame.current
      if (!el) return
      for (const [name, value] of Object.entries(libraryTableLayout(columns, selectMode, live))) {
        el.style.setProperty(name, value)
      }
      el.querySelector<HTMLElement>(`.library-table-th[data-col="${key}"]`)?.style.removeProperty('width')
    },
    (start) => {
      if (start.title > 0) return start
      const title = frame.current?.querySelector('.library-table-th[data-col="title"]')
      return title ? { ...start, title: Math.round(title.getBoundingClientRect().width) } : start
    },
  )
  return (
    <div
      ref={frame}
      className="library-table"
      role="table"
      aria-label={t`Library`}
      style={libraryTableLayout(columns, selectMode, widths) as CSSProperties}
    >
      <ResizeContext.Provider value={handleProps}>{children}</ResizeContext.Provider>
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
  const resize = useContext(ResizeContext)
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
        {resize && <div {...resize(key)} aria-label={t`Resize ${label} column`} />}
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
          <LightBadge color={statusColor(status.color)} leftSection={<status.Icon size={12} />}>
            {renderLabel(status.label)}
          </LightBadge>
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
            <LightBadge
              color="var(--brand)"
              leftSection={<IconCircleCheckFilled size={12} />}
              data-tip={t`All downloaded chapters read`}
            >
              <Trans>Read</Trans>
            </LightBadge>
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
          <LightBadge color="var(--info)">
            <Trans>Due</Trans>
          </LightBadge>
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
          <ScoreInline score={series.communityRating * 10} />
        ) : (
          <Empty />
        )
      case 'rating': {
        const rating = shownRating(series)
        if (rating == null) return <Empty />
        const soFar = isProvisional(series)
        return (
          <YourScoreInline rating={rating} data-provisional={soFar || undefined} title={soFar ? t`So far` : undefined} />
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
          <Thumb className="library-table-thumb" src={series.coverUrl} size="sm" />
          <span className="library-table-name series-title" data-lines="1" title={series.title}>
            {series.displayTitle}
          </span>
          {offline === 'saved' && (
            <LightBadge color="var(--ok)" data-offline-badge="saved" data-tip={t`Saved on this device`}>
              <IconDeviceMobileCheck size={12} />
            </LightBadge>
          )}
          {offline === 'unavailable' && (
            <LightBadge color="var(--neutral)" data-offline-badge="unavailable" data-tip={t`Not available offline`}>
              <IconCloudOff size={12} />
            </LightBadge>
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
