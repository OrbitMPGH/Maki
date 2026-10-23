import { useId, useMemo } from 'react'
import { Link } from 'react-router-dom'
import { Tooltip } from '@mantine/core'
import { IconFlame } from '@tabler/icons-react'
import { Trans, useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'
import type { HeatmapDay, ProgressSummary, ReadingGoal } from '../../api/hooks'
import { formatDate, formatNumber, formatReadingTime, monthName, weekdayName } from '../../format'
import { useLabel } from '../../i18n-context'
import { HEAT_SHADES, heatLevel } from '../../pages/stats/ReadingHeatmap'
import { GOAL_LABELS, metricProgress } from '../../pages/stats/sections/ProgressSection'

const WEEKS = 5

interface WeekCell {
  date: string
  chapters: number
  seconds: number
  future: boolean
  today: boolean
  /** 1-based month number on the 1st, so the grid shows where a month starts. */
  monthStart: number | null
}

const dayKey = (d: Date) =>
  `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`

/**
 * The last five Monday-first weeks, ending with this one, so today always sits in the bottom row and
 * the grid is never empty on the 1st of a month. Totals cover the days up to today.
 */
export function rollingWeeks(days: HeatmapDay[], today = new Date()) {
  const byDate = new Map(days.map((d) => [d.date.slice(0, 10), d]))
  const todayKey = dayKey(today)
  const start = new Date(today.getFullYear(), today.getMonth(), today.getDate() - ((today.getDay() + 6) % 7) - (WEEKS - 1) * 7)

  const cells: WeekCell[] = []
  for (let i = 0; i < WEEKS * 7; i++) {
    const d = new Date(start.getFullYear(), start.getMonth(), start.getDate() + i)
    const key = dayKey(d)
    const found = byDate.get(key)
    cells.push({
      date: key,
      chapters: found?.chapters ?? 0,
      seconds: found?.seconds ?? 0,
      future: key > todayKey,
      today: key === todayKey,
      monthStart: d.getDate() === 1 ? d.getMonth() + 1 : null,
    })
  }

  const past = cells.filter((c) => !c.future)
  const read = (c: WeekCell) => c.chapters > 0 || c.seconds > 0
  return {
    cells,
    // This week alone, for the phone form, which draws only the grid's last row.
    weekChapters: cells.slice(-7).reduce((sum, c) => sum + c.chapters, 0),
    chapters: past.reduce((sum, c) => sum + c.chapters, 0),
    seconds: past.reduce((sum, c) => sum + c.seconds, 0),
    daysRead: past.filter(read).length,
    daysSoFar: past.length,
    readToday: past.some((c) => c.today && read(c)),
  }
}

/** A goal that fits a five-week window: this week's, else today's. Month and year goals live on Stats. */
function shortGoal(goals: ReadingGoal[]): ReadingGoal | undefined {
  return goals.find((g) => g.period === 'Week') ?? goals.find((g) => g.period === 'Day')
}

/**
 * Home's glance: the last five weeks as a day grid in the Stats heatmap's shades, the streak, and the
 * window's totals. Hosted in the Continue hero it is a card in the hero's right column on wide
 * screens and a row under the actions otherwise; elsewhere it stands at its own slot. Its inner
 * layout follows its own width (`.reading-weeks` in theme.css). On a phone it keeps only this week's
 * row and this week's count, so it does not push the rest of Home a screen down.
 */
export function ReadingWeeks({
  days,
  summary,
  hosted,
  loading,
}: {
  days: HeatmapDay[]
  summary: ProgressSummary
  hosted?: boolean
  /** Same markup with the content hidden, held in the slot while the real data loads so the hero does not grow when it lands. */
  loading?: boolean
}) {
  const { t, i18n } = useLingui()
  const renderLabel = useLabel()
  const headingId = useId()
  const today = dayKey(new Date())
  // Keyed on the day so a page left open overnight rolls its window over on the next render.
  const weeks = useMemo(() => rollingWeeks(days, new Date(`${today}T00:00:00`)), [days, today])
  const { daysRead, daysSoFar, weekChapters } = weeks
  const weekdays = useMemo(() => Array.from({ length: 7 }, (_, i) => weekdayName(i, 'narrow')), [i18n.locale])

  const { currentStreak, longestStreak, showStreaks } = summary
  const goal = shortGoal(summary.goals)
  const goalDone = goal ? Math.min(1, goal.progress / Math.max(1, goal.target)) : 0
  const streakLine =
    currentStreak === 0
      ? t`Read a chapter to start a streak`
      : weeks.readToday
        ? plural(longestStreak, { one: 'Today counts. Best: # day', other: 'Today counts. Best: # days' })
        : t`Read today to keep it going`

  return (
    <section
      className="home-glance reading-weeks"
      data-hosted={hosted || undefined}
      data-loading={loading || undefined}
      aria-hidden={loading || undefined}
      aria-labelledby={loading ? undefined : headingId}
    >
      <div className="reading-weeks-card">
        <div className="reading-weeks-head">
          <div className="reading-weeks-heading">
            <h2 id={headingId} className="reading-weeks-title">
              <Link to="/stats?section=rhythm">
                {/* One of the two is hidden by width; a hidden one drops out of the section's name too. */}
                <span className="reading-weeks-title-weeks">
                  <Trans>Last 5 weeks</Trans>
                </span>
                <span className="reading-weeks-title-week">
                  <Trans>This week</Trans>
                </span>
              </Link>
            </h2>
            {showStreaks && <span className="reading-weeks-sub">{streakLine}</span>}
          </div>
          {showStreaks && (
            <span
              role="img"
              className="reading-weeks-streak tnum"
              data-lit={weeks.readToday || undefined}
              aria-label={plural(currentStreak, { one: '# day streak', other: '# day streak' })}
            >
              <IconFlame size={15} aria-hidden />
              {formatNumber(currentStreak)}
              <span className="reading-weeks-streak-l" aria-hidden>
                <Trans>day streak</Trans>
              </span>
            </span>
          )}
        </div>

        <div
          className="reading-weeks-grid"
          role="img"
          aria-label={t`Read on ${daysRead} of the last ${daysSoFar} days`}
        >
          {weekdays.map((letter, i) => (
            <span key={`d${i}`} className="reading-weeks-weekday" aria-hidden>
              {letter}
            </span>
          ))}
          {weeks.cells.map((cell) => {
            // Level 0 is the Stats panel's ground, which is this card's too, so an empty day keeps its own fill.
            const level = cell.future ? 0 : heatLevel(cell.chapters, cell.seconds)
            const shade = level > 0 ? HEAT_SHADES[level] : undefined
            const mark = cell.today ? String(Number(cell.date.slice(8))) : cell.monthStart ? monthName(cell.monthStart, 'short') : null
            if (cell.future) {
              return (
                <span key={cell.date} className="reading-weeks-day" data-future>
                  {mark}
                </span>
              )
            }
            const cellDate = formatDate(`${cell.date}T00:00:00`)
            const chapters = cell.chapters
            const label =
              chapters === 0 && cell.seconds === 0
                ? t`${cellDate}: nothing read`
                : plural(chapters, { one: `${cellDate}: # chapter`, other: `${cellDate}: # chapters` })
            return (
              <Tooltip key={cell.date} label={label}>
                <span
                  role="img"
                  aria-label={label}
                  className="reading-weeks-day"
                  data-today={cell.today || undefined}
                  data-empty={level === 0 || undefined}
                  style={shade ? { background: shade } : undefined}
                >
                  {mark}
                </span>
              </Tooltip>
            )
          })}
        </div>

        <div className="reading-weeks-figures">
          <div className="reading-weeks-fig">
            <span className="reading-weeks-n figure tnum">{formatNumber(weeks.chapters)}</span>
            <span className="reading-weeks-l">
              <Trans>Chapters</Trans>
            </span>
          </div>
          <div className="reading-weeks-fig">
            <span className="reading-weeks-n figure tnum">{formatReadingTime(weeks.seconds)}</span>
            <span className="reading-weeks-l">
              <Trans>Time read</Trans>
            </span>
          </div>
          <div className="reading-weeks-fig">
            <span className="reading-weeks-n figure tnum">
              {formatNumber(daysRead)}
              <small>/{formatNumber(daysSoFar)}</small>
            </span>
            <span className="reading-weeks-l">
              <Trans>Days read</Trans>
            </span>
          </div>
        </div>

        {!goal && (
          <span className="reading-weeks-week tnum">
            {plural(weekChapters, { one: '# chapter this week', other: '# chapters this week' })}
          </span>
        )}

        {goal && (
          <div className="reading-weeks-goal">
            <span className="reading-weeks-goal-t tnum">
              {renderLabel(GOAL_LABELS[goal.period])}: <b>{formatNumber(goal.progress)}</b> / {metricProgress(goal.metric, goal.target)}
            </span>
            <div
              className="reading-weeks-bar"
              role="progressbar"
              aria-valuemin={0}
              aria-valuemax={goal.target}
              aria-valuenow={Math.min(goal.progress, goal.target)}
              aria-valuetext={`${formatNumber(goal.progress)} / ${metricProgress(goal.metric, goal.target)}`}
              aria-label={renderLabel(GOAL_LABELS[goal.period])}
            >
              <span style={{ width: `${goalDone * 100}%` }} data-done={goalDone >= 1 || undefined} />
            </div>
          </div>
        )}
      </div>
    </section>
  )
}

const PLACEHOLDER_SUMMARY: ProgressSummary = {
  enabled: true,
  showStreaks: true,
  level: { level: 1, xp: 0, intoLevel: 0, levelSpan: 1, nextLevelXp: 1, progress: 0 },
  chaptersRead: 0,
  readingSeconds: 0,
  seriesFinished: 0,
  daysRead: 0,
  currentStreak: 0,
  longestStreak: 0,
  earned: 0,
  total: 0,
  recent: [],
  goals: [],
}

/** Holds the glance's slot at its height while the summary and heatmap load. */
export function ReadingWeeksPlaceholder({ hosted }: { hosted?: boolean }) {
  return <ReadingWeeks days={[]} summary={PLACEHOLDER_SUMMARY} hosted={hosted} loading />
}
