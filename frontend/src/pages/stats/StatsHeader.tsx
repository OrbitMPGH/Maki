import type { ReactNode } from 'react'
import { Skeleton } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { useProgressSummary } from '../../api/hooks'
import { PageHeader } from '../../components/ui/PageHeader'
import { Panel } from '../../components/ui/Panel'
import { formatNumber, formatReadingTime } from '../../format'

export type StatsTab = 'overview' | 'library' | 'achievements'

const TABS: StatsTab[] = ['overview', 'library', 'achievements']

/**
 * Stats' page header: the house header row with the all-time figures as its aside and the
 * underline tab rail below it.
 *
 * The figures are all-time and per viewed user, so they stay put while the tabs and the period
 * control below change what the page is looking at.
 */
export function StatsHeader({
  description,
  actions,
  tab,
  onTabChange,
  userId,
}: {
  description: string
  actions?: ReactNode
  tab: StatsTab
  onTabChange: (tab: StatsTab) => void
  userId?: number
}) {
  const { t } = useLingui()
  const { data: summary, isLoading } = useProgressSummary(userId)
  const showPanel = isLoading || summary?.enabled === true

  // Null values render as skeletons sized to the number they stand in for, so the panel settles at
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
    <div className="stats-header">
      <PageHeader
        title={<Trans>Stats</Trans>}
        description={description}
        actions={actions}
        aside={
          showPanel && (
            <Panel p="md" className="home-header-panel">
              <div className="home-header-figures">
                {figures.map((f) => (
                  <div className="hero-stat" key={f.label}>
                    <span className="hero-stat-n tnum">
                      {f.value ?? <Skeleton height={17} width={48} my={3} radius="sm" />}
                    </span>
                    <span className="hero-stat-l">{f.label}</span>
                  </div>
                ))}
              </div>
            </Panel>
          )
        }
      />

      <div
        className="series-tabs stats-header-tabs"
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
