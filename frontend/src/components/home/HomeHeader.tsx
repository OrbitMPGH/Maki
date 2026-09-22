import type { ReactNode } from 'react'
import { Divider, Skeleton } from '@mantine/core'
import { useLingui } from '@lingui/react/macro'
import type { ProgressSummary } from '../../api/hooks'
import { PageHeader } from '../ui/PageHeader'
import { Panel } from '../ui/Panel'
import { formatNumber } from '../../format'
import { ProgressCard } from './ProgressCard'

/** One labelled number in the header's figures panel. */
export interface HomeHeaderFigure {
  label: string
  value: number
  /** A status token name; omitted leaves the number at `--ink-hi`. */
  tone?: 'ok' | 'warn' | 'danger'
}

/**
 * Home's page header: the house header row with the library figures as its aside.
 *
 * It is the page's masthead, not a section's. Home's sections are user-orderable and individually
 * switchable, so nothing below can host figures that stay put — see the note on `ContinueLead`.
 */
export function HomeHeader({
  greeting,
  actions,
  figures,
  readingFigures,
  progress,
  loading,
}: {
  greeting: string
  actions?: ReactNode
  figures: HomeHeaderFigure[]
  /** The unread/started/finished set, only when reading progress is tracked at all. */
  readingFigures?: HomeHeaderFigure[]
  /** Level and streaks, only when progression is switched on. */
  progress?: ProgressSummary
  loading?: boolean
}) {
  const { t } = useLingui()
  const hasPanel = figures.length > 0 || Boolean(readingFigures?.length) || Boolean(progress)

  return (
    <PageHeader
      eyebrow={t`Home`}
      title={greeting}
      actions={actions}
      aside={
        hasPanel && (
          <Panel p="md" className="home-header-panel">
            {figures.length > 0 && <FigureRow figures={figures} loading={loading} />}

            {readingFigures && readingFigures.length > 0 && (
              <>
                <Divider my={8} color="var(--hairline)" />
                <FigureRow figures={readingFigures} loading={loading} />
              </>
            )}

            {progress && (
              <>
                <Divider my={8} color="var(--hairline)" />
                <ProgressCard summary={progress} />
              </>
            )}
          </Panel>
        )
      }
    />
  )
}

/**
 * One row of figures. The skeleton is sized to the number it replaces rather than to a spinner, so
 * the panel settles at its final height on first paint and does not jump when the counts land.
 */
function FigureRow({ figures, loading }: { figures: HomeHeaderFigure[]; loading?: boolean }) {
  return (
    <div className="home-header-figures">
      {figures.map((f) => (
        <div className="hero-stat" key={f.label}>
          <span className="hero-stat-n tnum" style={f.tone ? { color: `var(--${f.tone})` } : undefined}>
            {loading ? <Skeleton height={17} width={48} my={3} radius="sm" /> : formatNumber(f.value)}
          </span>
          <span className="hero-stat-l">{f.label}</span>
        </div>
      ))}
    </div>
  )
}
