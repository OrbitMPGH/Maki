import { Tooltip } from '@mantine/core'
import { t } from '@lingui/core/macro'
import { plural } from '@lingui/core/macro'
import type { SeriesReadingStats } from '../../api/seriesReadingStats'
import { formatReadingTime, formatRecentDate, formatStatsBucket, formatTimeSpent } from '../../format'
import { IconClock } from '../../lib/icons'
import { FigureStrip, type Figure } from '../ui/FigureStrip'
import { Panel } from '../ui/Panel'
import { PanelTitle } from '../ui/PanelTitle'
import { relativeTime } from '../ui/time'

export interface ReadTimeEstimate {
  seconds: number
  remainingChapters: number
  style: 'scrolling' | 'paged'
  sampleChapters: number
  seriesSpecific: boolean
}

/**
 * "Your reading" on the series Details tab: time spent in the built-in reader, pace, time left and
 * a per-week (or per-month) bar strip. The caller renders it only when there are stats, so a series
 * read in another app has no panel rather than an empty one.
 */
export function SeriesReadingPanel({
  stats,
  estimate,
}: {
  stats: SeriesReadingStats
  estimate: ReadTimeEstimate | null
}) {
  const figures: Figure[] = [
    {
      label: t`Time read`,
      value: formatTimeSpent(stats.seconds),
      hint:
        stats.sittings > 0
          ? plural(stats.sittings, { one: 'across # sitting', other: 'across # sittings' })
          : plural(stats.timedChapters, { one: 'over # chapter', other: 'over # chapters' }),
    },
  ]
  if (stats.chapterMedianSeconds != null) {
    const usual = stats.elsewhereMedianSeconds
    figures.push({
      label: t`Per chapter`,
      value: formatReadingTime(stats.chapterMedianSeconds),
      hint: usual != null ? t`Usually ${formatReadingTime(usual)} in other series` : undefined,
    })
  }
  if (estimate) {
    const { remainingChapters } = estimate
    const chaptersLeft = plural(remainingChapters, { one: '# chapter left', other: '# chapters left' })
    const readingTime = formatReadingTime(estimate.seconds)
    figures.push({
      label: t`Time to finish`,
      value: t`About ${readingTime}`,
      hint: estimate.style === 'scrolling' ? t`${chaptersLeft} at your scrolling pace` : t`${chaptersLeft} at your paged pace`,
    })
  }
  const lastRead = relativeTime(stats.lastReadAt)
  figures.push({
    label: t`Reading since`,
    value: formatRecentDate(stats.firstReadAt),
    hint: t`Last read ${lastRead}`,
  })

  return (
    <Panel className="series-detail-reading-panel series-machine">
      <PanelTitle order={2} icon={IconClock}>
        {t`Your reading`}
      </PanelTitle>
      <FigureStrip figures={figures} flush className="series-reading-figures" />
      {stats.timeline.length > 0 && <ReadingTimeline stats={stats} />}
    </Panel>
  )
}

/**
 * The last line of the series band's Progress card. Nothing at all for a series with no reading
 * recorded and no pace to estimate from, so the card ends on its bars.
 */
export function ReadingFootLine({ firstReadAt, estimate }: { firstReadAt: string | null; estimate: ReadTimeEstimate | null }) {
  if (!firstReadAt && !estimate) return null
  const since = firstReadAt ? formatRecentDate(firstReadAt) : null
  const toFinish = estimate ? formatReadingTime(estimate.seconds) : null

  return (
    <div className="series-progress-foot tnum">
      {since && <span>{t`Reading since ${since}`}</span>}
      {toFinish && <span>{t`About ${toFinish} to finish`}</span>}
    </div>
  )
}

function ReadingTimeline({ stats }: { stats: SeriesReadingStats }) {
  const { timeline } = stats
  const weekly = stats.bucketUnit === 'week'
  const max = Math.max(...timeline.map((b) => b.seconds))
  const current = timeline[timeline.length - 1]
  const currentTime = formatTimeSpent(current.seconds)
  const title = weekly ? t`Time per week` : t`Time per month`

  return (
    <div className="series-reading-timeline">
      <div className="series-reading-timeline-head">
        <span>{title}</span>
        <span className="tnum">{weekly ? t`This week: ${currentTime}` : t`This month: ${currentTime}`}</span>
      </div>
      <div className="series-reading-bars" role="img" aria-label={title}>
        {timeline.map((b, i) => {
          const label = formatStatsBucket(b.bucket)
          const time = formatTimeSpent(b.seconds)
          return (
            <Tooltip key={b.bucket} label={weekly ? t`Week of ${label}: ${time}` : `${label}: ${time}`} withArrow>
              <div className="series-reading-bar-slot">
                <div
                  className="series-reading-bar"
                  data-empty={b.seconds === 0 || undefined}
                  data-current={i === timeline.length - 1 || undefined}
                  style={b.seconds > 0 ? { height: `${Math.max(6, (b.seconds / max) * 100)}%` } : undefined}
                />
              </div>
            </Tooltip>
          )
        })}
      </div>
      <div className="series-reading-timeline-axis tnum">
        <span>{formatStatsBucket(timeline[0].bucket)}</span>
        <span>{formatStatsBucket(current.bucket)}</span>
      </div>
    </div>
  )
}
