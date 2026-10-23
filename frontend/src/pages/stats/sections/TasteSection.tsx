import { useMemo } from 'react'
import { Badge, SimpleGrid, Text } from '@mantine/core'
import { Select, Trans, useLingui } from '@lingui/react/macro'
import { useActivityStats } from '../../../api/hooks'
import { useStatsInsights, useStatsStanding } from '../../../api/stats'
import { EmptyState } from '../../../components/ui/EmptyState'
import { Panel } from '../../../components/ui/Panel'
import { TagChips } from '../../../components/ui/TagChip'
import { formatDecimal, formatPercent } from '../../../format'
import { ReadTrackingEmpty } from '../ReadTrackingEmpty'
import { ChartSkeleton } from '../ChartSkeleton'
import { CreatorList } from '../CreatorList'
import { LeanBars } from '../charts/LeanBars'
import { RatingDumbbell } from '../charts/RatingDumbbell'
import { SplitBar } from '../charts/SplitBar'
import { GENRE_LABELS, TYPE_LABELS, useNameLabel } from '../labels'
import { StatsInsight } from '../StatsSection'
import type { StatsSectionProps } from './types'

function isUnknownName(name: string): boolean {
  return name === '' || name.toLowerCase() === 'unknown'
}

/** "1980" -> "80s", "2000" -> "00s". */
function decadeLabel(decade: number): string {
  return String(decade % 100).padStart(2, '0')
}

export default function TasteSection({ userId, range }: StatsSectionProps) {
  const { t, i18n } = useLingui()
  const label = useNameLabel()

  const { data: activity } = useActivityStats(range.from, range.to, userId)
  const { data: insights } = useStatsInsights(range.from, range.to, userId, !!activity?.readTrackingAvailable)
  const { data: standing } = useStatsStanding(userId, !!activity?.readTrackingAvailable)

  const taste = insights?.taste

  const insight = useMemo(() => {
    if (!taste || taste.lean.length === 0) return null
    const withDiff = taste.lean.map((g) => ({ ...g, diff: g.readShare - g.libraryShare }))
    const readGenre = [...withDiff].sort((a, b) => b.diff - a.diff)[0]
    const ownedGenre = [...withDiff].sort((a, b) => a.diff - b.diff)[0]
    if (!readGenre || !ownedGenre || readGenre.name === ownedGenre.name) return null

    const readGenreName = label(GENRE_LABELS, readGenre.name)
    const ownedGenreName = label(GENRE_LABELS, ownedGenre.name)
    const readShare = formatPercent(readGenre.readShare)
    const libraryShare = formatPercent(readGenre.libraryShare)
    return (
      <>
        <Trans>
          You collect <em>{ownedGenreName}</em> but you read <em>{readGenreName}</em>.
        </Trans>{' '}
        <span className="stats-insight-dim">
          <Trans>
            {readGenreName} is {readShare} of what you read and {libraryShare} of the library.
          </Trans>
        </span>
      </>
    )
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [taste, label, i18n.locale])

  const typeItems = useMemo(
    () =>
      (taste?.types ?? []).map((item) => ({
        key: item.name,
        label: item.name ? label(TYPE_LABELS, item.name) : t`Unspecified`,
        value: item.count,
        known: !isUnknownName(item.name),
      })),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [taste, label, i18n.locale],
  )

  const demographicItems = useMemo(
    () =>
      (taste?.demographics ?? []).map((item) => ({
        key: item.name || '__none',
        label: item.name || t`Unspecified`,
        value: item.count,
        known: !isUnknownName(item.name),
      })),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [taste, i18n.locale],
  )

  const eras = useMemo(() => {
    const list = taste?.eras ?? []
    const total = list.reduce((sum, e) => sum + e.chapters, 0)
    const maxChapters = Math.max(...list.map((e) => e.chapters), 1)
    return list
      .slice()
      .sort((a, b) => (a.decade ?? Infinity) - (b.decade ?? Infinity))
      .map((e) => {
        const decade = e.decade !== null ? decadeLabel(e.decade) : ''
        return {
          key: e.decade === null ? 'unknown' : String(e.decade),
          known: e.decade !== null,
          title: e.decade === null ? t`Unknown` : t`${decade}s`,
          value: total > 0 ? formatPercent(e.chapters / total) : '-',
          barPct: (e.chapters / maxChapters) * 100,
        }
      })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [taste, i18n.locale])

  const eraTotal = (taste?.eras ?? []).reduce((sum, e) => sum + e.chapters, 0)

  const tagTiers = useMemo(() => {
    const tags = activity?.topTags ?? []
    const sorted = [...tags].sort((a, b) => b.weight - a.weight)
    const cut = Math.ceil(sorted.length / 3)
    return new Map(sorted.map((tag, i) => [tag.name, i < cut ? 'filled' : 'light'] as const))
  }, [activity])

  if (activity && activity.readTrackingAvailable === false) {
    return (
      <ReadTrackingEmpty />
    )
  }

  const ratings = standing?.ratings ?? null

  const quiet =
    !!taste &&
    !!activity &&
    standing !== undefined &&
    taste.lean.length === 0 &&
    !ratings &&
    typeItems.length === 0 &&
    demographicItems.length === 0 &&
    eraTotal === 0 &&
    (standing.creators ?? []).length === 0 &&
    activity.topTags.length === 0
  if (quiet) {
    const hasHistory = (standing?.behaviour.chaptersRead ?? 0) > 0 || (standing?.seriesFullyRead ?? 0) > 0
    return (
      <StatsInsight>
        <span className="stats-insight-dim">
          {hasHistory ? (
            <Trans>Nothing read in this window. Pick a wider range to see your taste.</Trans>
          ) : (
            <Trans>Not enough reading yet to say what your taste is. This fills in after a few series.</Trans>
          )}
        </span>
      </StatsInsight>
    )
  }

  // A chart with one value says the same thing a bar at 100% would, so it becomes a sentence.
  // A lone Unknown or Unspecified bucket stays a panel: "Everything you read was Unspecified" says nothing.
  const oneType = taste && typeItems.length === 1 && typeItems[0].known ? typeItems[0].label : null
  const oneDemographic =
    taste && demographicItems.length === 1 && demographicItems[0].known ? demographicItems[0].label : null
  const oneEra = taste && eras.length === 1 && eraTotal > 0 && eras[0].known ? eras[0].title : null
  const showTypes = oneType === null
  const showDemographics = oneDemographic === null
  const showEra = oneEra === null
  const smallPanels = (showTypes || showDemographics ? 1 : 0) + (showEra ? 1 : 0) + 1
  // Judged on the value as printed, so a gap that rounds to 0.0 reads as "the same", not "0.0 above".
  const meanGapRounded = ratings ? Math.round(ratings.meanGap * 10) / 10 : 0
  const meanGapKind = meanGapRounded > 0 ? 'above' : meanGapRounded < 0 ? 'below' : 'same'
  const meanGapValue = formatDecimal(Math.abs(meanGapRounded))

  return (
    <>
      {(insight || oneType !== null || oneDemographic !== null || oneEra !== null) && (
        <StatsInsight>
          {insight}
          {insight && (oneType !== null || oneDemographic !== null || oneEra !== null) && ' '}
          <span className="stats-insight-dim">
            {oneType !== null && (
              <Trans>
                Everything you read was <em>{oneType}</em>.
              </Trans>
            )}
            {oneType !== null && (oneDemographic !== null || oneEra !== null) && ' '}
            {oneDemographic !== null && (
              <Trans>
                Everything you read is in the <em>{oneDemographic}</em> demographic.
              </Trans>
            )}
            {oneDemographic !== null && oneEra !== null && ' '}
            {oneEra !== null && (
              <Trans>
                Everything you read came out in the <em>{oneEra}</em>.
              </Trans>
            )}
          </span>
        </StatsInsight>
      )}

      <div className="stats-grid-main">
        <Panel p="md">
          <div className="stats-panel-title">
            <Trans>Lean</Trans>
          </div>
          <p className="stats-panel-sub">
            <Trans>
              Share of chapters read minus share of the library. Right means you read more of it
              than you own.
            </Trans>
          </p>
          {!taste ? (
            <ChartSkeleton h={200} />
          ) : taste.lean.length === 0 ? (
            <EmptyState compact mood="asleep" title={t`Not enough genre data yet`} />
          ) : (
            <>
              <LeanBars items={taste.lean} />
              <ul className="stats-legend">
                <li>
                  <span className="stats-legend-swatch" style={{ background: 'var(--brand)' }} aria-hidden />
                  <Trans>Read more than you own</Trans>
                </li>
                <li>
                  <span className="stats-legend-swatch" style={{ background: 'var(--warn)' }} aria-hidden />
                  <Trans>Own more than you read</Trans>
                </li>
              </ul>
            </>
          )}
        </Panel>

        <Panel p="md">
          <div className="stats-panel-title">
            <Trans>You vs the crowd</Trans>
          </div>
          {ratings && (
            <p className="stats-panel-sub">
              {meanGapKind === 'same' ? (
                <Trans>You rate the same as the crowd</Trans>
              ) : (
                <Trans>
                  You rate {meanGapValue}{' '}
                  <Select
                    value={meanGapKind}
                    _above="points above the crowd"
                    _below="points below the crowd"
                    other="points from the crowd"
                  />
                </Trans>
              )}
            </p>
          )}
          {standing === undefined ? (
            <ChartSkeleton h={200} />
          ) : !ratings ? (
            <EmptyState compact title={t`Rate a few series to compare yourself with the crowd.`} />
          ) : (
            <>
              <RatingDumbbell series={ratings.series} />
              <ul className="stats-legend">
                <li>
                  <span className="stats-legend-swatch" style={{ background: 'var(--brand)' }} aria-hidden />
                  <Trans>You</Trans>
                </li>
                <li>
                  <span className="stats-legend-swatch" style={{ background: 'var(--neutral)' }} aria-hidden />
                  <Trans>Crowd</Trans>
                </li>
              </ul>
            </>
          )}
        </Panel>
      </div>

      <SimpleGrid cols={{ base: 1, md: smallPanels }} spacing="lg">
        {(showTypes || showDemographics) && (
          <Panel p="md">
            {showTypes && (
              <>
                <div className="stats-panel-title">
                  <Trans>Type mix</Trans>
                </div>
                {!taste ? (
                  <ChartSkeleton h={120} />
                ) : typeItems.length === 0 ? (
                  <EmptyState compact mood="asleep" title={t`No type data yet`} />
                ) : (
                  <SplitBar items={typeItems} />
                )}
              </>
            )}
            {showDemographics && (
              <>
                {showTypes ? (
                  <Text size="sm" fw={650} mt="md" mb={4}>
                    <Trans>Demographic</Trans>
                  </Text>
                ) : (
                  <div className="stats-panel-title">
                    <Trans>Demographic</Trans>
                  </div>
                )}
                {!taste ? (
                  <ChartSkeleton h={120} />
                ) : demographicItems.length === 0 ? (
                  <EmptyState compact mood="asleep" title={t`No demographic data yet`} />
                ) : (
                  <SplitBar items={demographicItems} />
                )}
              </>
            )}
          </Panel>
        )}

        {showEra && (
          <Panel p="md">
            <div className="stats-panel-title">
              <Trans>Era</Trans>
            </div>
            {!taste ? (
              <ChartSkeleton h={140} />
            ) : eraTotal === 0 ? (
              <EmptyState compact mood="asleep" title={t`No release-year data yet`} />
            ) : (
              <div className="stats-hist">
                {eras.map((e) => (
                  <div className="stats-hist-col" key={e.key}>
                    <span className="stats-hist-value">{e.value}</span>
                    <span className="stats-hist-bar" style={{ height: `${Math.max(2, e.barPct * 0.65)}%` }} />
                    <span className="stats-hist-label">{e.title}</span>
                  </div>
                ))}
              </div>
            )}
          </Panel>
        )}

        <Panel p="md">
          <div className="stats-panel-title">
            <Trans>Creators you return to</Trans>
          </div>
          {standing === undefined ? (
            <ChartSkeleton h={140} />
          ) : (standing.creators ?? []).length === 0 ? (
            <EmptyState compact mood="asleep" title={t`No repeat creators yet`} />
          ) : (
            <CreatorList creators={standing.creators} />
          )}
        </Panel>
      </SimpleGrid>

      <Panel p="md">
        <div className="stats-panel-title">
          <Trans>Tags that keep showing up</Trans>
        </div>
        {!activity ? (
          <ChartSkeleton h={80} />
        ) : activity.topTags.length === 0 ? (
          <EmptyState compact mood="asleep" title={t`No tag data yet`} />
        ) : (
          <TagChips>
            {activity.topTags.map((tag) => (
              <Badge
                key={tag.name}
                variant={tagTiers.get(tag.name) ?? 'light'}
                color="var(--brand)"
              >
                {tag.name}
              </Badge>
            ))}
          </TagChips>
        )}
      </Panel>
    </>
  )
}
