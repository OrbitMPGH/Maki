import { useMemo, type CSSProperties, type ReactNode } from 'react'
import { Box, Group, Stack, Text, Title, Tooltip } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { Link } from 'react-router-dom'
import { confidenceLevel, useUpcoming, type UpcomingSeriesDto } from '../../api/releases'
import { formatDayRange, formatPercent, formatRecentDate, formatWeekdayDate, formatWeekdayDateTime } from '../../format'
import { useLabel } from '../../i18n-context'
import { EmptyState } from '../ui/EmptyState'
import { Panel } from '../ui/Panel'
import { TableSkeleton } from '../ui/TableSkeleton'
import { CONFIDENCE_LABEL } from './outlookText'

/** Five weeks: long enough to hold a monthly series' window, short enough to read a day. */
export const UPCOMING_DAYS = 35

const DAY_MS = 24 * 60 * 60 * 1000

function startOfDay(value: Date): Date {
  return new Date(value.getFullYear(), value.getMonth(), value.getDate())
}

/** Whole local days from `today` to the day `value` falls on. Rounded so a DST shift can't skew it. */
function dayIndex(today: Date, value: string): number {
  return Math.round((startOfDay(new Date(value)).getTime() - today.getTime()) / DAY_MS)
}

interface Placed {
  item: UpcomingSeriesDto
  /** Inclusive day indexes, unclamped. Null when only an announced date places the series. */
  span: [number, number] | null
  announced: number | null
  first: number
}

function place(items: UpcomingSeriesDto[], today: Date): Placed[] {
  return items
    .map((item) => {
      const { windowStart, windowEnd, announcedAt, state } = item.outlook
      const span: [number, number] | null =
        state === 'Expected' && windowStart && windowEnd
          ? [dayIndex(today, windowStart), dayIndex(today, windowEnd)]
          : null
      const announced = announcedAt ? dayIndex(today, announcedAt) : null
      const first = Math.min(span ? Math.max(span[0], 0) : Infinity, announced ?? Infinity)
      return { item, span, announced, first }
    })
    .filter((p) => p.span || p.announced != null)
    .sort((a, b) => a.first - b.first || a.item.title.localeCompare(b.item.title))
}

/** Activity's Upcoming tab. */
export function UpcomingView() {
  const { t } = useLingui()
  const query = useUpcoming()

  if (!query.data) {
    return query.isError ? (
      <EmptyState
        compact
        title={t`Couldn't load this section`}
        description={query.error instanceof Error ? query.error.message : String(query.error)}
        actionLabel={t`Retry`}
        onAction={() => void query.refetch()}
      />
    ) : (
      <TableSkeleton columns={4} rows={5} />
    )
  }

  return <UpcomingContent items={query.data} now={new Date()} />
}

export function UpcomingContent({ items, now }: { items: UpcomingSeriesDto[]; now: Date }) {
  const { t } = useLingui()
  const today = startOfDay(now)
  const late = items.filter((i) => i.outlook.state === 'Late' && i.outlook.announcedAt == null)
  const placed = place(items, today)
  const within = placed.filter((p) => p.first < UPCOMING_DAYS)
  const further = placed.filter((p) => p.first >= UPCOMING_DAYS)

  if (late.length === 0 && placed.length === 0) {
    return (
      <EmptyState
        compact
        mood="asleep"
        title={t`Nothing to place yet`}
        description={t`A series shows up here once its chapters come out on a steady enough rhythm, or when its source announces the next one.`}
      />
    )
  }

  return (
    <Stack gap="lg">
      <Stack gap="xs">
        <Text size="sm" c="var(--ink-3)" maw="80ch">
          <Trans>
            Each bar spans the days a chapter is likely, worked out from the series' own release
            history, so it is a span and never a promise. The percentage is how often its recent
            chapters came out inside the span drawn for them. A diamond marks a day the source
            itself announced.
          </Trans>
        </Text>
        <UpcomingLegend />
      </Stack>

      {within.length > 0 && (
        <>
          <Box visibleFrom="sm">
            <Panel p={0} className="upcoming-panel">
              <UpcomingTimeline placed={within} today={today} />
            </Panel>
          </Box>
          <Box hiddenFrom="sm">
            <UpcomingWeeks placed={within} today={today} />
          </Box>
        </>
      )}

      {late.length > 0 && (
        <UpcomingGroup title={t`Running late`}>
          {late.map((item) => (
            <UpcomingLine key={item.seriesId} item={item} late />
          ))}
        </UpcomingGroup>
      )}

      {further.length > 0 && (
        <UpcomingGroup title={t`Further out`}>
          {further.map((p) => (
            <UpcomingLine key={p.item.seriesId} item={p.item} />
          ))}
        </UpcomingGroup>
      )}
    </Stack>
  )
}

function UpcomingTimeline({ placed, today }: { placed: Placed[]; today: Date }) {
  const base = today.getTime()
  const days = useMemo(() => {
    const first = new Date(base)
    return Array.from({ length: UPCOMING_DAYS }, (_, i) => new Date(first.getFullYear(), first.getMonth(), first.getDate() + i))
  }, [base])
  // Week labels start on each Monday, plus the first column so the grid never opens unlabelled.
  const weeks = useMemo(() => {
    const starts = days.map((d, i) => ({ d, i })).filter(({ d, i }) => i === 0 || d.getDay() === 1)
    return starts.map(({ d, i }, n) => ({ day: d, from: i, to: n + 1 < starts.length ? starts[n + 1].i : days.length }))
  }, [days])
  const columns = { '--upcoming-days': UPCOMING_DAYS } as CSSProperties

  return (
    <div className="upcoming-timeline" style={columns}>
      <div className="upcoming-head">
        <div className="upcoming-name-cell" />
        <div className="upcoming-days">
          {weeks.map((w) => (
            <div key={w.from} className="upcoming-week" style={{ gridColumn: `${w.from + 1} / ${w.to + 1}` }}>
              {formatRecentDate(w.day)}
            </div>
          ))}
          {days.map((d, i) => (
            <div
              key={i}
              className="upcoming-day tnum"
              data-today={i === 0 || undefined}
              data-weekend={d.getDay() === 0 || d.getDay() === 6 || undefined}
              title={formatWeekdayDate(d)}
            >
              {d.getDate()}
            </div>
          ))}
        </div>
      </div>
      {placed.map((p) => (
        <TimelineRow key={p.item.seriesId} placed={p} />
      ))}
    </div>
  )
}

function TimelineRow({ placed }: { placed: Placed }) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const { item, span, announced } = placed
  const { outlook } = item
  const level = confidenceLevel(outlook.confidence)
  const { hits, checked, announcedBy } = outlook
  const range = outlook.windowStart && outlook.windowEnd ? formatDayRange(outlook.windowStart, outlook.windowEnd) : ''
  const confidence = renderLabel(CONFIDENCE_LABEL[level])
  const when = outlook.announcedAt ? formatWeekdayDateTime(outlook.announcedAt) : ''
  const from = span ? Math.max(span[0], 0) : 0
  const to = span ? Math.min(span[1], UPCOMING_DAYS - 1) : 0
  // An announcement from earlier today's window can fall on yesterday's column; draw it on today's.
  const announcedDay = announced == null ? null : Math.max(announced, 0)

  return (
    <div className="upcoming-row">
      <UpcomingName item={item} />
      <div className="upcoming-track">
        {span && to >= from && (
          <Tooltip
            withArrow
            multiline
            w={260}
            label={t`Likely ${range}. ${confidence}: ${hits} of the last ${checked} came out inside their window.`}
          >
            <div
              className="upcoming-bar"
              data-level={level}
              data-open-start={span[0] < 0 || undefined}
              data-open-end={span[1] >= UPCOMING_DAYS || undefined}
              style={{ gridColumn: `${from + 1} / ${to + 2}` }}
              role="img"
              tabIndex={0}
              aria-label={t`Likely ${range}`}
            />
          </Tooltip>
        )}
        {announcedDay != null && announcedDay < UPCOMING_DAYS && (
          <Tooltip withArrow label={t`${announcedBy} has it scheduled for ${when}`}>
            <div
              className="upcoming-announced"
              style={{ gridColumn: `${announcedDay + 1} / ${announcedDay + 2}` }}
              role="img"
              tabIndex={0}
              aria-label={t`${announcedBy} has it scheduled for ${when}`}
            />
          </Tooltip>
        )}
      </div>
    </div>
  )
}

function UpcomingName({ item }: { item: UpcomingSeriesDto }) {
  const { seriesId, title, coverUrl, outlook } = item
  const showsWindow = outlook.state === 'Expected' || outlook.state === 'Late'
  return (
    <Link to={`/series/${seriesId}`} className="upcoming-name-cell upcoming-name">
      {coverUrl ? <img src={coverUrl} alt="" className="upcoming-cover" loading="lazy" /> : <span className="upcoming-cover" />}
      <span className="upcoming-title">{title}</span>
      {showsWindow && (
        <span className="upcoming-confidence tnum" data-level={confidenceLevel(outlook.confidence)}>
          {formatPercent(outlook.confidence)}
        </span>
      )}
    </Link>
  )
}

/** One series as a line of text: the phone layout, and the late and further-out lists. */
function UpcomingLine({ item, late = false }: { item: UpcomingSeriesDto; late?: boolean }) {
  const renderLabel = useLabel()
  const { outlook } = item
  const range = outlook.windowStart && outlook.windowEnd ? formatDayRange(outlook.windowStart, outlook.windowEnd) : null
  const when = outlook.announcedAt ? formatWeekdayDateTime(outlook.announcedAt) : null
  const { announcedBy } = outlook
  const level = confidenceLevel(outlook.confidence)

  return (
    <div className="upcoming-line">
      <UpcomingName item={item} />
      <Stack gap={2} className="upcoming-line-text">
        {when && announcedBy && (
          <Text size="sm" fw={600}>
            <Trans>
              {announcedBy} has it scheduled for {when}
            </Trans>
          </Text>
        )}
        {range && (
          <Text size="sm" c={late ? 'var(--warn)' : undefined}>
            {late ? <Trans>Running late. It was likely {range}.</Trans> : <Trans>Likely {range}</Trans>}
          </Text>
        )}
        {range && (
          <Text size="xs" c="var(--ink-3)">
            {renderLabel(CONFIDENCE_LABEL[level])}
          </Text>
        )}
      </Stack>
    </div>
  )
}

function UpcomingGroup({ title, children }: { title: string; children: ReactNode }) {
  return (
    <div>
      <Title order={2} size="h4" fz="var(--type-body)" mb="xs">
        {title}
      </Title>
      <Stack gap="xs">{children}</Stack>
    </div>
  )
}

/** The phone layout: one list per week, in order. */
function UpcomingWeeks({ placed, today }: { placed: Placed[]; today: Date }) {
  const { t } = useLingui()
  const weekOf = (index: number) => {
    const day = new Date(today.getFullYear(), today.getMonth(), today.getDate() + Math.max(index, 0))
    // Monday-based, matching the timeline's week labels.
    return new Date(day.getFullYear(), day.getMonth(), day.getDate() - ((day.getDay() + 6) % 7))
  }
  const thisWeek = weekOf(0).getTime()
  const groups = new Map<number, Placed[]>()
  for (const p of placed) {
    const key = weekOf(p.first).getTime()
    groups.set(key, [...(groups.get(key) ?? []), p])
  }

  return (
    <Stack gap="lg">
      {[...groups.entries()].map(([week, members]) => {
        const weeksAhead = Math.round((week - thisWeek) / (7 * DAY_MS))
        const start = formatRecentDate(week)
        const title = weeksAhead === 0 ? t`This week` : weeksAhead === 1 ? t`Next week` : t`Week of ${start}`
        return (
          <UpcomingGroup key={week} title={title}>
            {members.map((p) => (
              <UpcomingLine key={p.item.seriesId} item={p.item} />
            ))}
          </UpcomingGroup>
        )
      })}
    </Stack>
  )
}

function UpcomingLegend() {
  const renderLabel = useLabel()
  return (
    <Group gap="md" className="upcoming-legend">
      <span className="upcoming-legend-item">
        <span className="upcoming-bar" data-level="high" />
        {renderLabel(CONFIDENCE_LABEL.high)}
      </span>
      <span className="upcoming-legend-item">
        <span className="upcoming-bar" data-level="medium" />
        {renderLabel(CONFIDENCE_LABEL.medium)}
      </span>
      <span className="upcoming-legend-item">
        <span className="upcoming-announced" />
        <Trans>Announced by the source</Trans>
      </span>
    </Group>
  )
}
