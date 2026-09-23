import type { ReactNode } from 'react'
import { Trans } from '@lingui/react/macro'
import { PageHeader } from '../../components/ui/PageHeader'

export type StatsTab = 'overview' | 'library' | 'achievements'

const TABS: StatsTab[] = ['overview', 'library', 'achievements']

/** Stats' page header: the house header row with the underline tab rail below it. */
export function StatsHeader({
  description,
  actions,
  tab,
  onTabChange,
}: {
  description: string
  actions?: ReactNode
  tab: StatsTab
  onTabChange: (tab: StatsTab) => void
}) {
  return (
    <div className="stats-header">
      <PageHeader
        title={<Trans>Stats</Trans>}
        description={description}
        actions={actions}
      />

      <div
        className="series-tabs page-tabs"
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
