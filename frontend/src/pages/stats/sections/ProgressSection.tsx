import { useMemo } from 'react'
import {
  Alert,
  Group,
  RingProgress,
  SimpleGrid,
  Skeleton,
  Stack,
  Table,
  Text,
  Title,
} from '@mantine/core'
import { IconAlertTriangle, IconInfoCircle } from '@tabler/icons-react'
import { Trans, Plural, useLingui } from '@lingui/react/macro'
import { msg, plural } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { useAchievements, useProgressSummary, useLeaderboard } from '../../../api/hooks'
import type { ReadingGoal } from '../../../api/hooks'
import { Panel } from '../../../components/ui/Panel'
import { formatNumber } from '../../../format'
import { useLabel } from '../../../i18n-context'
import {
  AchievementGrid,
  closestToNextTier,
  formatAchievementValue,
} from '../AchievementGrid'
import { StatsInsight } from '../StatsSection'
import type { StatsSectionProps } from './types'

const GOAL_LABELS: Record<ReadingGoal['period'], MessageDescriptor> = {
  Day: msg`Today`,
  Week: msg`This week`,
  Month: msg`This month`,
  Year: msg`This year`,
}

/** Target count, pluralised by metric: "12 / 50 chapters", "1 / 30 minutes". */
function metricProgress(metric: ReadingGoal['metric'], target: number): string {
  switch (metric) {
    case 'Chapters':
      return plural(target, { one: '# chapter', other: '# chapters' })
    case 'Minutes':
      return plural(target, { one: '# minute', other: '# minutes' })
    case 'SeriesFinished':
      return plural(target, { one: '# series finished', other: '# series finished' })
  }
}

function GoalRow({ goal }: { goal: ReadingGoal }) {
  const renderLabel = useLabel()
  const done = Math.min(1, goal.progress / Math.max(1, goal.target))
  return (
    <li className="stats-row">
      <RingProgress
        size={30}
        thickness={4}
        roundCaps
        sections={[{ value: done * 100, color: done >= 1 ? 'var(--ok)' : 'var(--brand)' }]}
      />
      <div style={{ minWidth: 0 }}>
        <div className="stats-row-title">{renderLabel(GOAL_LABELS[goal.period])}</div>
        <div className="stats-row-meter">
          <span style={{ width: `${done * 100}%`, background: done >= 1 ? 'var(--ok)' : undefined }} />
        </div>
      </div>
      <div className="stats-row-value tnum">
        {formatNumber(goal.progress)}
        <small>/ {metricProgress(goal.metric, goal.target)}</small>
      </div>
    </li>
  )
}

/**
 * Level, goals, badges and the household leaderboard: the progression system, which has no window
 * of its own. It is the one section on the page that ignores `range`.
 */
export default function ProgressSection({ userId }: StatsSectionProps) {
  const { t, i18n } = useLingui()
  const { data: summary, isLoading, isError } = useProgressSummary(userId)
  const { data: achievements } = useAchievements(userId, summary?.enabled !== false)
  // Only meaningful for your own view: the endpoint answers about who opted in, not about whoever
  // an admin is currently looking at.
  const { data: leaderboard } = useLeaderboard(!userId)

  const insight = useMemo(() => {
    if (!summary || !summary.enabled) return null
    const level = summary.level.level
    const xpToNext = Math.max(0, summary.level.levelSpan - summary.level.intoLevel)
    const closest = achievements ? closestToNextTier(achievements) : null
    const badge = closest?.name ?? ''
    const remaining = closest?.nextThreshold != null ? closest.nextThreshold - closest.value : 0
    // Marathoner grades on seconds: a raw count of seconds "to go" is not a plural quantity the way
    // a badge that grades on chapters or days is, so it gets the formatted duration instead.
    const remainingIsTimed = closest?.key === 'marathoner'
    const remainingDuration = remainingIsTimed && closest ? formatAchievementValue(closest, remaining) : ''

    return (
      <>
        <Trans>
          Level <em>{level}</em>, <Plural value={xpToNext} one="# XP" other="# XP" /> from the next
          one.
        </Trans>
        {closest &&
          (remainingIsTimed ? (
            <>
              {' '}
              <Trans>
                Closest badge: <em>{badge}</em>, {remainingDuration} to go.
              </Trans>
            </>
          ) : (
            <>
              {' '}
              <Trans>
                Closest badge: <em>{badge}</em>,{' '}
                <Plural value={remaining} one="# to go" other="# to go" />.
              </Trans>
            </>
          ))}
      </>
    )
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [summary, achievements, i18n.locale])

  if (isLoading && !summary) {
    return (
      <>
        <SimpleGrid cols={{ base: 1, md: 3 }} spacing="md" aria-hidden>
          <Panel p="md">
            <Group gap="md" wrap="nowrap">
              <Skeleton h={84} w={84} circle />
              <Stack gap={8}>
                <Skeleton h={16} w={90} />
                <Skeleton h={10} w={140} />
              </Stack>
            </Group>
          </Panel>
          <Panel p="md">
            <Skeleton h={12} w={64} mb="sm" />
            <Skeleton h={80} />
          </Panel>
          <Panel p="md">
            <Skeleton h={12} w={90} mb="sm" />
            <Skeleton h={80} />
          </Panel>
        </SimpleGrid>
        <SimpleGrid cols={{ base: 1, sm: 2, lg: 4 }} spacing="sm" mt="lg">
          {Array.from({ length: 8 }, (_, i) => (
            <Skeleton key={i} h={104} radius="lg" />
          ))}
        </SimpleGrid>
      </>
    )
  }

  // Kept apart from the switched-off case on purpose. Both leave `summary.enabled` falsy, and
  // reporting a failed request as "you turned this off" sends the reader to a settings toggle that
  // is already in the position they want.
  if (isError || !summary) {
    return (
      <Alert icon={<IconAlertTriangle size={16} />} color="var(--danger)" variant="light">
        <Trans>Could not load your progress. The server logs will say why.</Trans>
      </Alert>
    )
  }

  if (!summary.enabled) {
    return (
      <Alert icon={<IconInfoCircle size={16} />} color="var(--neutral)" variant="light">
        <Trans>
          Progress tracking is switched off. Turn it back on under Settings to see levels,
          achievements and streaks. Nothing was lost while it was off: all of it is worked out from
          your reading history whenever it is asked for.
        </Trans>
      </Alert>
    )
  }

  const { level } = summary
  const currentLevel = level.level
  const nextLevel = level.level + 1
  const intoLevel = formatNumber(level.intoLevel)
  const levelSpan = formatNumber(level.levelSpan)
  const showLeaderboard = !userId
  const streak = summary.currentStreak
  const best = summary.longestStreak
  const total = formatNumber(summary.total)

  return (
    <>
      {insight && <StatsInsight>{insight}</StatsInsight>}

      <SimpleGrid cols={{ base: 1, md: 3 }} spacing="md">
        <Panel p="md">
          <Group gap="md" wrap="nowrap">
            <RingProgress
              size={84}
              thickness={8}
              roundCaps
              sections={[{ value: level.progress * 100, color: 'var(--brand)' }]}
              label={
                <Text ta="center" fw={700} size="lg" className="tnum">
                  {level.level}
                </Text>
              }
            />
            <Stack gap={2}>
              <Title order={4}>
                <Trans>Level {currentLevel}</Trans>
              </Title>
              <Text size="sm" c="var(--ink-3)" className="tnum">
                <Trans>
                  {intoLevel} / {levelSpan} XP to level {nextLevel}
                </Trans>
              </Text>
            </Stack>
          </Group>
          {summary.showStreaks && (
            <Text size="xs" c="var(--ink-3)" mt="sm" className="tnum">
              <Trans>
                <b>{streak}</b> day streak, best <b>{best}</b>
              </Trans>
            </Text>
          )}
        </Panel>

        <Panel p="md">
          <p className="stats-panel-title">
            <Trans>Goals</Trans>
          </p>
          {summary.goals.length > 0 ? (
            <ul className="stats-rows">
              {summary.goals.map((goal) => (
                <GoalRow key={goal.id} goal={goal} />
              ))}
            </ul>
          ) : (
            <p className="stats-row-sub" style={{ margin: 0 }}>
              {t`No goals set yet.`}
            </p>
          )}
        </Panel>

        {showLeaderboard && (
          <Panel p="md">
            <p className="stats-panel-title">
              <Trans>Leaderboard</Trans>
            </p>
            {leaderboard && leaderboard.length >= 2 ? (
              <Table className="panel-table ops-table" highlightOnHover>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>
                      <Trans>Reader</Trans>
                    </Table.Th>
                    <Table.Th ta="right">
                      <Trans>Level</Trans>
                    </Table.Th>
                    <Table.Th ta="right">
                      <Trans>Chapters</Trans>
                    </Table.Th>
                    <Table.Th ta="right">
                      <Trans>Streak</Trans>
                    </Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {leaderboard.map((row) => (
                    <Table.Tr key={row.userId}>
                      <Table.Td>{row.name}</Table.Td>
                      <Table.Td ta="right" className="tnum">
                        {row.level}
                      </Table.Td>
                      <Table.Td ta="right" className="tnum">
                        {formatNumber(row.chaptersRead)}
                      </Table.Td>
                      <Table.Td ta="right" className="tnum">
                        {row.currentStreak}
                      </Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            ) : (
              <p className="stats-row-sub" style={{ margin: 0 }}>
                {t`No one else on this instance has opted in yet.`}
              </p>
            )}
          </Panel>
        )}
      </SimpleGrid>

      {achievements && achievements.length > 0 && (
        <Panel p="md">
          <Group justify="space-between" align="baseline" mb="xs">
            <p className="stats-panel-title" style={{ margin: 0 }}>
              <Trans>Badges</Trans>
            </p>
            <Text size="sm" c="var(--ink-3)" className="tnum">
              <Trans>All {total}</Trans>
            </Text>
          </Group>
          <AchievementGrid achievements={achievements} />
        </Panel>
      )}
    </>
  )
}
