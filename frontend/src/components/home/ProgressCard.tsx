import { RingProgress, Text } from '@mantine/core'
import { IconFlame, IconTrophy } from '@tabler/icons-react'
import { Link } from 'react-router-dom'
import type { ProgressSummary } from '../../api/hooks'
import { formatNumber, formatReadingTime } from '../../format'
import { Trans, useLingui } from '@lingui/react/macro'
import { GlanceFigures, type GlanceFigure } from './HomeGlance'

/**
 * Home's progression row, matching the Stats page's Progress section so the same numbers read the
 * same in both places. The whole row links to Stats for the full picture.
 *
 * A row of the glance, not a card: `HomeGlance` supplies the surface. The ring is the XP bar, and
 * the sidebar footer already has its own, so there is no bar under the level.
 */
export function ProgressCard({ summary }: { summary: ProgressSummary }) {
  const { t } = useLingui()
  const { level } = summary
  const { level: levelNumber, intoLevel, levelSpan } = level
  const intoLevelFormatted = formatNumber(intoLevel)
  const levelSpanFormatted = formatNumber(levelSpan)
  const nextLevel = levelNumber + 1

  const figures: GlanceFigure[] = [
    { value: formatNumber(summary.chaptersRead), label: t`Chapters read` },
    { value: formatReadingTime(summary.readingSeconds), label: t`Time read` },
    ...(summary.showStreaks
      ? [
          { value: summary.currentStreak, label: t`Day streak`, icon: IconFlame },
          { value: summary.longestStreak, label: t`Best streak` },
        ]
      : []),
    { value: `${summary.earned}/${summary.total}`, label: t`Achievements`, icon: IconTrophy },
  ]

  return (
    // A plain Link rather than a Mantine element with `component`: the polymorphic prop types do
    // not carry react-router's `to` through, and this element only needs to be a flex row.
    <Link to="/stats?section=progress" className="glance-row glance-progress">
      <div className="glance-level">
        <RingProgress
          size={38}
          thickness={4}
          roundCaps
          aria-hidden
          sections={[{ value: level.progress * 100, color: 'var(--brand)' }]}
          label={
            <Text ta="center" fw={700} fz="var(--type-micro)" className="figure" c="var(--ink-hi)">
              {level.level}
            </Text>
          }
        />
        <div className="glance-level-text">
          <Text fw={700} fz="var(--type-meta)" c="var(--ink-hi)">
            <Trans>Level {levelNumber}</Trans>
          </Text>
          <Text fz="var(--type-micro)" c="var(--ink-4)" className="tnum">
            <Trans>
              {intoLevelFormatted} / {levelSpanFormatted} XP to level {nextLevel}
            </Trans>
          </Text>
        </div>
      </div>
      <GlanceFigures figures={figures} />
    </Link>
  )
}
