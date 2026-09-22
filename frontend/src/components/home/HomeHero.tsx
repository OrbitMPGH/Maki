import type { ReactNode } from 'react'
import { Divider, Paper, Skeleton } from '@mantine/core'
import { useLingui } from '@lingui/react/macro'
import type { ProgressSummary } from '../../api/hooks'
import { HeroBackdrop } from '../series/HeroBackdrop'
import { formatNumber } from '../../format'
import { ProgressCard } from './ProgressCard'

/** One labelled number in the band's glass panel. */
export interface HomeHeroFigure {
  label: string
  value: number
  /** A status token name; omitted leaves the number at `--ink-hi`. */
  tone?: 'ok' | 'warn' | 'danger'
}

/**
 * Home's page band: the same full-bleed hero the series page and the Discover modal use, at the
 * modal's compact height.
 *
 * It is the page's masthead, not a section's. Home's sections are user-orderable and individually
 * switchable, so nothing below can host a band that stays put — see the note on `ContinueLead`.
 * The figures that used to sit in their own row of panels live in the band's glass panel instead.
 *
 * The art is seeded from whatever cover the page already has in hand, so the band is the library's
 * own rather than a stock gradient. With no cover at all it falls back to the CSS art the auth
 * screens use, which is the same recipe and not a second copy of it.
 */
export function HomeHero({
  coverUrl,
  greeting,
  actions,
  figures,
  readingFigures,
  progress,
  loading,
}: {
  coverUrl: string | null
  greeting: string
  actions?: ReactNode
  figures: HomeHeroFigure[]
  /** The unread/started/finished set, only when reading progress is tracked at all. */
  readingFigures?: HomeHeroFigure[]
  /** Level and streaks, only when progression is switched on. */
  progress?: ProgressSummary
  loading?: boolean
}) {
  const { t } = useLingui()
  const hasPanel = figures.length > 0 || Boolean(readingFigures?.length) || Boolean(progress)

  return (
    <div className="series-hero home-hero">
      {!coverUrl && <div className="home-hero-art" aria-hidden />}
      <HeroBackdrop coverUrl={coverUrl} />

      <div className="series-hero-body home-hero-body">
        <div className="home-hero-content">
          <div className="home-hero-identity">
            <span className="home-hero-eyebrow">{t`Home`}</span>
            <h1 className="home-hero-title">{greeting}</h1>
            {actions && <div className="home-hero-actions">{actions}</div>}
          </div>

          {hasPanel && (
            <Paper withBorder radius="lg" p="md" className="series-hero-glass-panel home-hero-panel">
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
            </Paper>
          )}
        </div>
      </div>
    </div>
  )
}

/**
 * One row of figures. The skeleton is sized to the number it replaces rather than to a spinner, so
 * the band settles at its final height on first paint and does not jump when the counts land.
 */
function FigureRow({ figures, loading }: { figures: HomeHeroFigure[]; loading?: boolean }) {
  return (
    <div className="home-hero-figures">
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
