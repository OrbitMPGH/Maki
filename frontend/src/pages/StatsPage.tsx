import { useMemo, useState } from 'react'
import { Button, Select, Tabs } from '@mantine/core'
// Imported here rather than in main.tsx so the chart stylesheet travels with this route's chunk,
// and here in the shell rather than in a panel so it loads once regardless of which tab opens.
import '@mantine/charts/styles.css'
import { IconPlayerPlay } from '@tabler/icons-react'
import { useRewindStats, useRewindYears } from '../api/hooks'
import { useUsers } from '../api/auth'
import { useAuth } from '../auth/AuthProvider'
import { PageHeader } from '../components/ui/PageHeader'
import { RewindIntro } from './rewind/RewindIntro'
import { AchievementsPanel } from './stats/AchievementsPanel'
import { LibraryPanel } from './stats/LibraryPanel'
import { OverviewPanel } from './stats/OverviewPanel'
import { calendarRange, type RangePreset } from './stats/StatsRange'

type StatsTab = 'overview' | 'library' | 'achievements'

/**
 * The Stats page shell: who is being looked at, which tab, and the Rewind launcher. Each tab owns
 * its own data — Overview and Achievements are per-user, Library is not.
 */
export default function StatsPage() {
  const currentYear = new Date().getFullYear()
  const [tab, setTab] = useState<StatsTab>('overview')

  const { me } = useAuth()
  const isAdmin = me?.isAdmin ?? false
  const { data: users } = useUsers(isAdmin)
  // undefined means "me", which is what every endpoint defaults to. Only an admin can set it, and
  // the server re-checks that — this picker is cosmetic like every other permission check here.
  const [viewUserId, setViewUserId] = useState<number | undefined>(undefined)

  const { data: years } = useRewindYears(viewUserId)
  const yearOptions = (years?.length ? years : [currentYear]).map(String)
  const earliestYear = years?.length ? Math.min(...years) : currentYear

  const [preset, setPreset] = useState<RangePreset>('30d')
  const [year, setYear] = useState(currentYear)
  const [month, setMonth] = useState<number | null>(null)

  // Rewind plays a calendar year, never the page's range: "the last 90 days" is not a retrospective.
  // It follows the year drill-down when that is what is on screen, and the current year otherwise.
  const [introOpen, setIntroOpen] = useState(false)
  const rewindYear = preset === 'year' ? year : currentYear
  const rewindRange = useMemo(() => calendarRange(rewindYear, null), [rewindYear])
  const { data: rewindStats } = useRewindStats(
    rewindRange.from,
    rewindRange.to,
    viewUserId,
    tab === 'overview',
  )

  const canPlayRewind =
    rewindStats !== undefined &&
    (rewindStats.totals.chaptersRead > 0 ||
      rewindStats.totals.volumesRead > 0 ||
      rewindStats.totals.readingSeconds > 0 ||
      rewindStats.totals.chaptersDownloaded > 0 ||
      rewindStats.totals.seriesAdded > 0 ||
      rewindStats.totals.seriesRemoved > 0)

  return (
    <>
      {introOpen && rewindStats && (
        <RewindIntro
          stats={rewindStats}
          label={String(rewindYear)}
          onClose={() => setIntroOpen(false)}
        />
      )}

      <PageHeader
        title="Stats"
        description="What you read, what the library holds, and how far you have come."
        actions={
          <>
            {isAdmin && users && users.length > 1 && tab !== 'library' && (
              <Select
                data={users
                  .filter((u) => !u.pendingSetup)
                  .map((u) => ({ value: String(u.id), label: u.displayName || u.userName }))}
                value={viewUserId === undefined ? String(me?.id ?? '') : String(viewUserId)}
                onChange={(v) => setViewUserId(v && Number(v) !== me?.id ? Number(v) : undefined)}
                w={180}
                aria-label="Reader"
              />
            )}
            {tab === 'overview' && (
              <Button
                leftSection={<IconPlayerPlay size={16} />}
                onClick={() => setIntroOpen(true)}
                disabled={!canPlayRewind}
                title={`Play the ${rewindYear} retrospective`}
              >
                Play Rewind
              </Button>
            )}
          </>
        }
      />

      <Tabs value={tab} onChange={(v) => setTab((v as StatsTab) ?? 'overview')} mb="lg">
        <Tabs.List>
          <Tabs.Tab value="overview">Overview</Tabs.Tab>
          <Tabs.Tab value="library">Library</Tabs.Tab>
          <Tabs.Tab value="achievements">Achievements</Tabs.Tab>
        </Tabs.List>
      </Tabs>

      {tab === 'overview' && (
        <OverviewPanel
          userId={viewUserId}
          preset={preset}
          onPresetChange={setPreset}
          year={year}
          onYearChange={setYear}
          month={month}
          onMonthChange={setMonth}
          yearOptions={yearOptions}
          earliestYear={earliestYear}
          onOpenAchievements={() => setTab('achievements')}
        />
      )}
      {tab === 'library' && <LibraryPanel />}
      {tab === 'achievements' && <AchievementsPanel userId={viewUserId} />}
    </>
  )
}
