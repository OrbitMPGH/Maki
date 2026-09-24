import { useMemo } from 'react'
import { Badge, SimpleGrid, Text } from '@mantine/core'
import { Select, Trans, useLingui } from '@lingui/react/macro'
import { useActivityStats } from '../../../api/hooks'
import { useStatsInsights, useStatsStanding } from '../../../api/stats'
import { EmptyState } from '../../../components/ui/EmptyState'
import { Panel } from '../../../components/ui/Panel'
import { TagChips } from '../../../components/ui/TagChip'
import { formatPercent, formatSignedDecimal } from '../../../format'
import { ChartSkeleton } from '../ChartSkeleton'
import { CreatorList } from '../CreatorList'
import { LeanBars } from '../charts/LeanBars'
import { RatingDumbbell } from '../charts/RatingDumbbell'
import { SplitBar } from '../charts/SplitBar'
import { GENRE_LABELS, TYPE_LABELS, useNameLabel } from '../labels'
import { StatsInsight } from '../StatsSection'
import type { StatsSectionProps } from './types'

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
        label: label(TYPE_LABELS, item.name),
        value: item.count,
      })),
    [taste, label],
  )

  const demographicItems = useMemo(
    () =>
      (taste?.demographics ?? []).map((item) => ({
        key: item.name || '__none',
        label: item.name || t`Unspecified`,
        value: item.count,
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
      <EmptyState
        compact
        title={t`Reading stats need Kavita`}
        description={t`Reading stats need Kavita: connect it in Settings and Maki will start tracking chapters you read. Downloads and library changes are tracked either way.`}
      />
    )
  }

  const ratings = standing?.ratings ?? null
  const meanGapKind = ratings ? (ratings.meanGap > 0 ? 'above' : ratings.meanGap < 0 ? 'below' : 'same') : 'same'
  const meanGapValue = ratings ? formatSignedDecimal(ratings.meanGap) : '0'

  return (
    <>
      {insight && <StatsInsight>{insight}</StatsInsight>}

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
            <EmptyState compact title={t`Not enough genre data yet`} />
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
              <Trans>
                You rate {meanGapValue}{' '}
                <Select
                  value={meanGapKind}
                  _above="points above the crowd"
                  _below="points below the crowd"
                  other="the same as the crowd"
                />
              </Trans>
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

      <SimpleGrid cols={{ base: 1, md: 3 }} spacing="lg">
        <Panel p="md">
          <div className="stats-panel-title">
            <Trans>Type mix</Trans>
          </div>
          {!taste ? (
            <ChartSkeleton h={120} />
          ) : typeItems.length === 0 ? (
            <EmptyState compact title={t`No type data yet`} />
          ) : (
            <SplitBar items={typeItems} />
          )}
          <Text size="sm" fw={650} mt="md" mb={4}>
            <Trans>Demographic</Trans>
          </Text>
          {!taste ? (
            <ChartSkeleton h={120} />
          ) : demographicItems.length === 0 ? (
            <EmptyState compact title={t`No demographic data yet`} />
          ) : (
            <SplitBar items={demographicItems} />
          )}
        </Panel>

        <Panel p="md">
          <div className="stats-panel-title">
            <Trans>Era</Trans>
          </div>
          {!taste ? (
            <ChartSkeleton h={140} />
          ) : eraTotal === 0 ? (
            <EmptyState compact title={t`No release-year data yet`} />
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

        <Panel p="md">
          <div className="stats-panel-title">
            <Trans>Creators you return to</Trans>
          </div>
          {standing === undefined ? (
            <ChartSkeleton h={140} />
          ) : (standing.creators ?? []).length === 0 ? (
            <EmptyState compact title={t`No repeat creators yet`} />
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
          <EmptyState compact title={t`No tag data yet`} />
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
