import type { ReactNode } from 'react'
import { Paper, Skeleton } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { useProgressSummary } from '../../api/hooks'
import { HeroBackdrop } from '../../components/series/HeroBackdrop'
import { formatNumber, formatReadingTime } from '../../format'

export type StatsTab = 'overview' | 'library' | 'achievements'

const TABS: StatsTab[] = ['overview', 'library', 'achievements']

/**
 * Stats' page band: the same full-bleed hero Home and the series page use, with the underline tab
 * rail at its foot so both pages share one rail rather than two that drift.
 *
 * The art is seeded from a cover the page already holds (the top series of the Rewind window) and
 * falls back to the CSS brand art. Nothing here fetches for the sake of the backdrop.
 *
 * The glass panel is all-time and per viewed user, so it stays put while the tabs and the period
 * control below change what the page is looking at.
 */
export function StatsHero({
  coverUrl,
  description,
  actions,
  tab,
  onTabChange,
  userId,
}: {
  coverUrl: string | null
  description: string
  actions?: ReactNode
  tab: StatsTab
  onTabChange: (tab: StatsTab) => void
  userId?: number
}) {
  const { t } = useLingui()
  const { data: summary, isLoading } = useProgressSummary(userId)
  const showPanel = isLoading || summary?.enabled === true

  // Null values render as skeletons sized to the number they stand in for, so the band settles at
  // its final height on first paint.
  const figures: { label: string; value: string | null }[] = [
    { label: t`Level`, value: summary ? formatNumber(summary.level.level) : null },
    { label: t`Chapters read`, value: summary ? formatNumber(summary.chaptersRead) : null },
    { label: t`Time read`, value: summary ? formatReadingTime(summary.readingSeconds) : null },
    {
      label: t`Achievements`,
      value: summary ? `${formatNumber(summary.earned)}/${formatNumber(summary.total)}` : null,
    },
  ]

  return (
    <div className="series-hero home-hero stats-hero">
      {!coverUrl && <div className="home-hero-art" aria-hidden />}
      <HeroBackdrop coverUrl={coverUrl} />

      <div className="series-hero-body home-hero-body stats-hero-body">
        <div className="home-hero-content">
          <div className="home-hero-identity">
            <span className="home-hero-eyebrow">{t`Stats`}</span>
            <h1 className="home-hero-title">
              <Trans>Stats</Trans>
            </h1>
            <p className="stats-hero-sub">{description}</p>
            {actions && <div className="home-hero-actions">{actions}</div>}
          </div>

          {showPanel && (
            <Paper withBorder radius="lg" p="md" className="series-hero-glass-panel home-hero-panel">
              <div className="home-hero-figures">
                {figures.map((f) => (
                  <div className="hero-stat" key={f.label}>
                    <span className="hero-stat-n tnum">
                      {f.value ?? <Skeleton height={17} width={48} my={3} radius="sm" />}
                    </span>
                    <span className="hero-stat-l">{f.label}</span>
                  </div>
                ))}
              </div>
            </Paper>
          )}
        </div>

        <div
          className="series-tabs stats-hero-tabs"
          role="tablist"
          onKeyDown={(e) => {
            const at = TABS.indexOf(tab)
            let next = at
            if (e.key === 'ArrowRight') next = (at + 1) % TABS.length
            else if (e.key === 'ArrowLeft') next = (at - 1 + TABS.length) % TABS.length
            else if (e.key === 'Home') next = 0
            else if (e.key === 'End') next = TABS.length - 1
            else return

            e.preventDefault()
            onTabChange(TABS[next])
            // The rail only ever holds these three buttons, so position is the identity.
            const buttons = e.currentTarget.querySelectorAll<HTMLButtonElement>('button')
            buttons[next]?.focus()
          }}
        >
          <TabButton value="overview" tab={tab} onTabChange={onTabChange}>
            <Trans>Overview</Trans>
          </TabButton>
          <TabButton value="library" tab={tab} onTabChange={onTabChange}>
            <Trans>Library</Trans>
          </TabButton>
          <TabButton value="achievements" tab={tab} onTabChange={onTabChange}>
            <Trans>Achievements</Trans>
          </TabButton>
        </div>
      </div>
    </div>
  )
}

function TabButton({
  value,
  tab,
  onTabChange,
  children,
}: {
  value: StatsTab
  tab: StatsTab
  onTabChange: (tab: StatsTab) => void
  children: ReactNode
}) {
  return (
    <button
      type="button"
      role="tab"
      aria-selected={tab === value}
      tabIndex={tab === value ? 0 : -1}
      className="series-tab"
      data-active={tab === value ? true : undefined}
      onClick={() => onTabChange(value)}
    >
      {children}
    </button>
  )
}
