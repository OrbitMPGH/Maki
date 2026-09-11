import { Group, RingProgress, Stack, Text } from '@mantine/core'
import { IconFlame, IconTrophy } from '@tabler/icons-react'
import { Link } from 'react-router-dom'
import type { ProgressSummary } from '../../api/hooks'
import { formatReadingTime } from '../../pages/stats/duration'

function Figure({
  value,
  label,
  icon: FigIcon,
}: {
  value: string | number
  label: string
  icon?: typeof IconFlame
}) {
  return (
    <div className="hero-stat">
      <span className="hero-stat-n tnum">
        {FigIcon && (
          <FigIcon size={16} style={{ color: 'var(--brand)', marginRight: 5, verticalAlign: -2 }} />
        )}
        {value}
      </span>
      <span className="hero-stat-l">{label}</span>
    </div>
  )
}

/**
 * Home's progression figures, matching the Stats overview's ProgressStrip so the same numbers read
 * the same in both places. The whole group links to Stats for the full picture.
 *
 * Deliberately without a card of its own: it is one half of Home's glance strip, which supplies
 * the border and the padding for both halves. See `.home-glance` in theme.css.
 */
export function ProgressCard({ summary }: { summary: ProgressSummary }) {
  const { level } = summary

  return (
    // A plain Link rather than a Mantine element with `component`: the polymorphic prop types do
    // not carry react-router's `to` through, and this element only needs to be a flex row.
    <Link to="/stats" className="home-glance-progress">
      <Group gap={11} wrap="nowrap">
        <RingProgress
          size={46}
          thickness={5}
          roundCaps
          sections={[{ value: level.progress * 100, color: 'var(--brand)' }]}
          label={
            <Text ta="center" fw={700} fz={11} className="tnum" c="var(--ink-hi)">
              {level.level}
            </Text>
          }
        />
        <Stack gap={0}>
          <Text fw={700} fz={14} c="var(--ink-hi)">
            Level {level.level}
          </Text>
          <Text fz={11} c="var(--ink-4)" className="tnum">
            {level.intoLevel.toLocaleString()} / {level.levelSpan.toLocaleString()} XP to level{' '}
            {level.level + 1}
          </Text>
        </Stack>
      </Group>

      <div className="hero-stats">
        <Figure value={summary.chaptersRead.toLocaleString()} label="chapters read" />
        <Figure value={formatReadingTime(summary.readingSeconds)} label="time read" />
        {summary.showStreaks && (
          <>
            <Figure value={summary.currentStreak} label="day streak" icon={IconFlame} />
            <Figure value={summary.longestStreak} label="best streak" />
          </>
        )}
        <Figure value={`${summary.earned}/${summary.total}`} label="achievements" icon={IconTrophy} />
      </div>
    </Link>
  )
}
