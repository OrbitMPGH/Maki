import { Group, RingProgress, Stack, Text } from '@mantine/core'
import { IconFlame, IconTrophy } from '@tabler/icons-react'
import { Link } from 'react-router-dom'
import type { ProgressSummary } from '../../api/hooks'
import { formatNumber, formatReadingTime } from '../../format'
import { Trans, useLingui } from '@lingui/react/macro'

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
 * Deliberately without a card of its own: it sits in the page band's glass panel, under the
 * library figures, and that panel supplies the border and the padding. See `.home-hero-progress`
 * in theme.css.
 */
export function ProgressCard({ summary }: { summary: ProgressSummary }) {
  const { t } = useLingui()
  const { level } = summary
  const { level: levelNumber, intoLevel, levelSpan } = level
  const intoLevelFormatted = formatNumber(intoLevel)
  const levelSpanFormatted = formatNumber(levelSpan)
  const nextLevel = levelNumber + 1

  return (
    // A plain Link rather than a Mantine element with `component`: the polymorphic prop types do
    // not carry react-router's `to` through, and this element only needs to be a flex row.
    <Link to="/stats" className="home-hero-progress">
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
            <Trans>Level {levelNumber}</Trans>
          </Text>
          <Text fz={11} c="var(--ink-4)" className="tnum">
            <Trans>
              {intoLevelFormatted} / {levelSpanFormatted} XP to level {nextLevel}
            </Trans>
          </Text>
        </Stack>
      </Group>

      <div className="hero-stats">
        <Figure value={formatNumber(summary.chaptersRead)} label={t`chapters read`} />
        <Figure value={formatReadingTime(summary.readingSeconds)} label={t`time read`} />
        {summary.showStreaks && (
          <>
            <Figure value={summary.currentStreak} label={t`day streak`} icon={IconFlame} />
            <Figure value={summary.longestStreak} label={t`best streak`} />
          </>
        )}
        <Figure
          value={`${summary.earned}/${summary.total}`}
          label={t`achievements`}
          icon={IconTrophy}
        />
      </div>
    </Link>
  )
}
