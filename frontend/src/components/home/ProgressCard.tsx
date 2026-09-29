import { Group, Progress, Text } from '@mantine/core'
import { IconFlame, IconTrophy } from '@tabler/icons-react'
import { Link } from 'react-router-dom'
import type { ProgressSummary } from '../../api/hooks'
import { formatNumber, formatReadingTime } from '../../format'
import { Trans, useLingui } from '@lingui/react/macro'

function Meta({
  value,
  label,
  icon: MetaIcon,
}: {
  value: string | number
  label: string
  icon?: typeof IconFlame
}) {
  return (
    <span className="tnum">
      {MetaIcon && <MetaIcon size={12} style={{ color: 'var(--brand-fg)', marginRight: 3, verticalAlign: -1 }} />}
      <b style={{ color: 'var(--ink-hi)', fontWeight: 600 }}>{value}</b> {label}
    </span>
  )
}

/**
 * Home's progression figures, matching the Stats page's Progress section so the same numbers read
 * the same in both places. Rendered as the middle cell of Home's `FigureStrip variant="panel"`, and
 * the whole cell links to Stats for the full picture.
 */
export function ProgressCard({ summary }: { summary: ProgressSummary }) {
  const { t } = useLingui()
  const { level } = summary
  const { level: levelNumber, intoLevel, levelSpan } = level
  const intoLevelFormatted = formatNumber(intoLevel)
  const levelSpanFormatted = formatNumber(levelSpan)
  const nextLevel = levelNumber + 1
  const toNext = t`${intoLevelFormatted} / ${levelSpanFormatted} XP to level ${nextLevel}`

  return (
    <Link
      to="/stats?section=progress"
      style={{ display: 'grid', gap: 5, color: 'inherit', textDecoration: 'none' }}
    >
      <Group justify="space-between" gap={8} wrap="nowrap" align="baseline">
        <Text fz={15} c="var(--ink-hi)" className="figure">
          <Trans>Level {levelNumber}</Trans>
        </Text>
        <Text fz={12} c="var(--ink-3)" className="tnum" title={toNext}>
          <Trans>
            {intoLevelFormatted} / {levelSpanFormatted} XP
          </Trans>
        </Text>
      </Group>

      <Progress
        value={level.progress * 100}
        size={4}
        radius="xl"
        color="var(--brand)"
        bg="var(--surface-2)"
        aria-label={toNext}
      />

      <Group gap="4px 12px" fz={12} c="var(--ink-3)" wrap="wrap" justify="space-between">
        <Meta value={formatNumber(summary.chaptersRead)} label={t`chapters read`} />
        <Meta value={`${summary.earned}/${summary.total}`} label={t`achievements`} icon={IconTrophy} />
      </Group>

      <Group gap="4px 12px" fz={12} c="var(--ink-3)" wrap="wrap" justify="space-between">
        <Meta value={formatReadingTime(summary.readingSeconds)} label={t`time read`} />
        {summary.showStreaks && (
          <>
            <Meta value={summary.currentStreak} label={t`day streak`} icon={IconFlame} />
            <Meta value={summary.longestStreak} label={t`best streak`} />
          </>
        )}
      </Group>
    </Link>
  )
}
