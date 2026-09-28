import type { ReactNode } from 'react'
import { RingProgress, SimpleGrid, Skeleton, Stack, Text } from '@mantine/core'
import { Trans, Plural, Select, useLingui } from '@lingui/react/macro'
import { IconArchive, IconBooks, IconChartHistogram, IconFlame } from '@tabler/icons-react'
import { useActivityStats, useLibraryComposition } from '../../../api/hooks'
import type { BehaviourSeries } from '../../../api/hooks'
import { useStatsStanding } from '../../../api/stats'
import { EmptyState } from '../../../components/ui/EmptyState'
import { Panel } from '../../../components/ui/Panel'
import { formatNumber, formatPercent, formatReadingTime } from '../../../format'
import { BacklogBars } from '../charts/BacklogBars'
import { StopHistogram } from '../charts/StopHistogram'
import { ChartSkeleton } from '../ChartSkeleton'
import { SeriesLink, SeriesThumb } from '../SeriesLink'
import { StatsInsight } from '../StatsSection'
import type { StatsSectionProps } from './types'

function HabitsSkeleton() {
  return (
    <Stack gap="lg" aria-hidden>
      <div className="stats-grid-side">
        <Panel p="md">
          <Skeleton h={140} radius="md" />
        </Panel>
        <Panel p="md">
          <ChartSkeleton h={140} />
        </Panel>
      </div>
      <SimpleGrid cols={{ base: 1, md: 3 }} spacing="lg">
        {[0, 1, 2].map((i) => (
          <Panel p="md" key={i}>
            <Skeleton h={120} radius="md" />
          </Panel>
        ))}
      </SimpleGrid>
    </Stack>
  )
}

/** One row of a Savoured/Devoured/Abandoned panel. */
function BehaviourRow({
  item,
  format,
}: {
  item: BehaviourSeries
  format: (item: BehaviourSeries) => ReactNode
}) {
  return (
    <li className="stats-row">
      <SeriesThumb url={item.coverUrl} alt={item.title} />
      <div style={{ minWidth: 0 }}>
        <div className="stats-row-title">
          <SeriesLink id={item.seriesId} title={item.title} />
        </div>
      </div>
      {format(item)}
    </li>
  )
}

export default function HabitsSection({ userId, range }: StatsSectionProps) {
  const { t } = useLingui()
  const { data: activity } = useActivityStats(range.from, range.to, userId)
  const { data: standing } = useStatsStanding(userId, !!activity?.readTrackingAvailable)
  const { data: library } = useLibraryComposition(!!activity?.readTrackingAvailable)

  if (!activity) {
    return <HabitsSkeleton />
  }

  if (!activity.readTrackingAvailable) {
    return (
      <EmptyState
        compact
        title={t`Reading stats need Kavita`}
        description={t`Connect it in Settings and Maki will start tracking chapters you read. Downloads and library changes are tracked either way.`}
      />
    )
  }

  if (!standing) {
    return <HabitsSkeleton />
  }

  const { behaviour, backlog, seriesFullyRead } = standing
  const { seriesStarted, seriesFinished, finishRate, medianStopPoint, stopPointHistogram } = behaviour

  // The histogram covers every unfinished series, so buckets 1..9 (10%+ in) sum to the same count
  // as "abandoned" elsewhere: unfinished but past the sampling threshold. Bucket 0 is still going.
  const abandonedCount = stopPointHistogram.slice(1).reduce((sum, n) => sum + n, 0)
  const stillGoing = Math.max(0, seriesStarted - seriesFinished - abandonedCount)

  let stopKind: 'early' | 'midway' | 'late' | 'none' = 'none'
  if (medianStopPoint !== null) {
    stopKind = medianStopPoint < 0.33 ? 'early' : medianStopPoint < 0.66 ? 'midway' : 'late'
  }
  const finishRatePct = finishRate !== null ? formatPercent(finishRate) : null
  const stopPointPct = medianStopPoint !== null ? formatPercent(medianStopPoint) : null
  const backlogDuration =
    backlog.hoursAtPace !== null ? formatReadingTime(backlog.hoursAtPace * 3600) : null
  const unread = backlog.unreadChapters

  const emptyLists =
    behaviour.savoured.length === 0 && behaviour.devoured.length === 0 && behaviour.abandoned.length === 0

  return (
    <>
      {seriesStarted > 0 && finishRatePct && (
        <StatsInsight>
          <Trans>
            You finish <em>{finishRatePct}</em> of the series you start.
          </Trans>{' '}
          {stopPointPct && (
            <span className="stats-insight-dim">
              <Select
                value={stopKind}
                _early={`When you stop, it is usually early in, around ${stopPointPct} of the way through.`}
                _midway={`When you stop, it is usually midway in, around ${stopPointPct} of the way through.`}
                _late={`When you stop, it is usually late in, around ${stopPointPct} of the way through.`}
                other=""
              />
            </span>
          )}
        </StatsInsight>
      )}

      <Stack gap="lg">
        <div className="stats-grid-side">
          <Panel p="md">
            <p className="stats-panel-title">
              <IconFlame size={16} style={{ color: 'var(--brand)' }} />
              {t`Finish rate`}
            </p>
            <div style={{ display: 'flex', alignItems: 'center', gap: 16, marginBottom: 12 }}>
              <RingProgress
                size={92}
                thickness={9}
                roundCaps
                sections={[{ value: (finishRate ?? 0) * 100, color: 'var(--brand)' }]}
                label={
                  <Text ta="center" fw={700} className="tnum">
                    {finishRate !== null ? formatPercent(finishRate, 0) : '-'}
                  </Text>
                }
              />
            </div>
            <div className="stats-facts">
              <div className="stats-fact">
                <span className="stats-fact-label">{t`Finished`}</span>
                <span className="stats-fact-value tnum">{formatNumber(seriesFinished)}</span>
              </div>
              <div className="stats-fact">
                <span className="stats-fact-label">{t`Still going`}</span>
                <span className="stats-fact-value tnum">{formatNumber(stillGoing)}</span>
              </div>
              <div className="stats-fact">
                <span className="stats-fact-label">{t`Abandoned`}</span>
                <span className="stats-fact-value tnum">{formatNumber(abandonedCount)}</span>
                <span className="stats-fact-hint">{t`Stalled without finishing`}</span>
              </div>
              <div className="stats-fact">
                <span className="stats-fact-label">{t`Fully read owned`}</span>
                <span className="stats-fact-value tnum">
                  {formatNumber(seriesFullyRead)}
                  {library && <small>{`/${formatNumber(library.totals.seriesCount)}`}</small>}
                </span>
              </div>
            </div>
          </Panel>

          <Panel p="md">
            <p className="stats-panel-title">
              <IconChartHistogram size={16} style={{ color: 'var(--brand)' }} />
              {t`Where you stop`}
            </p>
            <p className="stats-panel-sub">
              {stopPointPct
                ? t`How far through a series you were when it stalled. Median ${stopPointPct}.`
                : t`How far through a series you were when it stalled.`}
            </p>
            {stopPointHistogram.some((n) => n > 0) ? (
              <StopHistogram buckets={stopPointHistogram} medianFraction={medianStopPoint} />
            ) : (
              <Text c="var(--ink-3)" size="sm">
                {t`Nothing unfinished to chart.`}
              </Text>
            )}
          </Panel>
        </div>

        {!emptyLists && (
          <SimpleGrid cols={{ base: 1, md: 3 }} spacing="lg">
            <Panel p="md">
              <p className="stats-panel-title">{t`Savoured`}</p>
              <p className="stats-panel-sub">{t`Read slowly over a long span`}</p>
              {behaviour.savoured.length === 0 ? (
                <Text c="var(--ink-3)" size="sm">
                  {t`Nothing slow enough to call savoured yet.`}
                </Text>
              ) : (
                <ul className="stats-rows">
                  {behaviour.savoured.map((item) => (
                    <BehaviourRow
                      key={item.seriesId}
                      item={item}
                      format={(i) => (
                        <span className="stats-row-value">
                          {formatReadingTime(i.measure)}
                          <small>{t`per chapter`}</small>
                        </span>
                      )}
                    />
                  ))}
                </ul>
              )}
            </Panel>

            <Panel p="md">
              <p className="stats-panel-title">{t`Devoured`}</p>
              <p className="stats-panel-sub">{t`Finished in a rush`}</p>
              {behaviour.devoured.length === 0 ? (
                <Text c="var(--ink-3)" size="sm">
                  {t`Nothing fast enough to call a rush yet.`}
                </Text>
              ) : (
                <ul className="stats-rows">
                  {behaviour.devoured.map((item) => (
                    <BehaviourRow
                      key={item.seriesId}
                      item={item}
                      format={(i) => (
                        <span className="stats-row-value">
                          {formatReadingTime(i.measure)}
                          <small>{t`per chapter`}</small>
                        </span>
                      )}
                    />
                  ))}
                </ul>
              )}
            </Panel>

            <Panel p="md">
              <p className="stats-panel-title">
                <IconArchive size={16} style={{ color: 'var(--brand)' }} />
                {t`Abandoned`}
              </p>
              <p className="stats-panel-sub">{t`Stopped before finishing`}</p>
              {behaviour.abandoned.length === 0 ? (
                <Text c="var(--ink-3)" size="sm">
                  {t`Nothing dropped. Keep it that way.`}
                </Text>
              ) : (
                <ul className="stats-rows">
                  {behaviour.abandoned.map((item) => (
                    <BehaviourRow
                      key={item.seriesId}
                      item={item}
                      format={(i) => (
                        <span
                          className="stats-row-value"
                          style={{ color: 'var(--brand)', cursor: 'pointer' }}
                        >
                          <SeriesLink id={i.seriesId} title={t`Resume`} />
                        </span>
                      )}
                    />
                  ))}
                </ul>
              )}
            </Panel>
          </SimpleGrid>
        )}

        {backlog.unreadChapters > 0 && (
          <Panel p="md">
            <p className="stats-panel-title">
              <IconBooks size={16} style={{ color: 'var(--brand)' }} />
              {t`Backlog`}
            </p>
            <p className="stats-panel-sub">
              {backlogDuration ? (
                <Trans>
                  <Plural value={unread} one="# chapter" other="# chapters" /> you own but
                  have not read. At your pace that is about {backlogDuration}.
                </Trans>
              ) : (
                <Trans>
                  <Plural value={unread} one="# chapter" other="# chapters" /> you own but
                  have not read.
                </Trans>
              )}
            </p>
            {backlog.top.length > 0 && (
              <>
                <BacklogBars items={backlog.top} />
                <ul className="stats-legend">
                  <li>
                    <span className="stats-legend-swatch" style={{ background: 'var(--brand)' }} aria-hidden />
                    {t`Read`}
                  </li>
                  <li>
                    <span className="stats-legend-swatch" style={{ background: 'var(--neutral)' }} aria-hidden />
                    {t`Unread`}
                  </li>
                </ul>
              </>
            )}
          </Panel>
        )}
      </Stack>
    </>
  )
}
