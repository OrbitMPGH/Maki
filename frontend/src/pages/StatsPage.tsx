import { useMemo, useState, type ComponentType } from 'react'
import { Button, Select } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
// Imported here rather than in main.tsx so the chart stylesheet travels with this route's chunk,
// and here in the shell rather than in a section so it loads once.
import '@mantine/charts/styles.css'
import { IconPlayerPlay } from '@tabler/icons-react'
import { useActivityStats, useActivityYears } from '../api/hooks'
import { useUsers } from '../api/auth'
import { useAuth } from '../auth/AuthProvider'
import { PageHeader } from '../components/ui/PageHeader'
import { SurfaceFrame } from '../components/ui/SurfaceFrame'
import { useLabel } from '../i18n-context'
import { RewindIntro } from './rewind/RewindIntro'
import { RangeControl } from './stats/RangeControl'
import { StatsRail } from './stats/StatsRail'
import { StatsSection } from './stats/StatsSection'
import {
  calendarRange,
  previousRange,
  rangeLabel,
  resolveRange,
  type RangePreset,
} from './stats/StatsRange'
import { STATS_SECTIONS, type StatsSectionKey } from './stats/sectionList'
import HabitsSection from './stats/sections/HabitsSection'
import LibrarySection from './stats/sections/LibrarySection'
import ProgressSection from './stats/sections/ProgressSection'
import ReadingSection from './stats/sections/ReadingSection'
import RhythmSection from './stats/sections/RhythmSection'
import TasteSection from './stats/sections/TasteSection'
import type { StatsSectionProps } from './stats/sections/types'

const SECTION_COMPONENTS: Record<StatsSectionKey, ComponentType<StatsSectionProps>> = {
  reading: ReadingSection,
  rhythm: RhythmSection,
  taste: TasteSection,
  habits: HabitsSection,
  library: LibrarySection,
  progress: ProgressSection,
}

/**
 * The Stats page shell: who is being looked at, which window, and the Rewind launcher. Each
 * section fetches its own data; Library is the one that is not per-reader.
 */
export default function StatsPage() {
  const { t, i18n } = useLingui()
  const renderLabel = useLabel()
  const currentYear = new Date().getFullYear()

  const { me } = useAuth()
  const isAdmin = me?.isAdmin ?? false
  const { data: users } = useUsers(isAdmin)
  // undefined means "me", which is what every endpoint defaults to. Only an admin can set it, and
  // the server re-checks that. This picker is cosmetic like every other permission check here.
  const [viewUserId, setViewUserId] = useState<number | undefined>(undefined)

  const { data: years } = useActivityYears(viewUserId)
  const yearOptions = (years?.length ? years : [currentYear]).map(String)
  const earliestYear = years?.length ? Math.min(...years) : currentYear

  const [preset, setPreset] = useState<RangePreset>('30d')
  const [year, setYear] = useState(currentYear)
  const [month, setMonth] = useState<number | null>(null)

  const range = useMemo(
    () => resolveRange(preset, year, month, earliestYear),
    [preset, year, month, earliestYear],
  )
  const previous = useMemo(() => previousRange(preset, range), [preset, range])
  // rangeLabel reads the catalogue when it runs, so the locale has to be a dependency.
  const windowLabel = useMemo(
    () => rangeLabel(preset, year, month),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [preset, year, month, i18n.locale],
  )

  // Rewind plays a calendar year, never the page's range: "the last 90 days" is not a retrospective.
  // It follows the year drill-down when that is what is on screen, and the current year otherwise.
  const [introOpen, setIntroOpen] = useState(false)
  const rewindYear = preset === 'year' ? year : currentYear
  const rewindRange = useMemo(() => calendarRange(rewindYear, null), [rewindYear])
  const { data: rewindStats } = useActivityStats(rewindRange.from, rewindRange.to, viewUserId)

  const canPlayRewind =
    rewindStats !== undefined &&
    (rewindStats.totals.chaptersRead > 0 ||
      rewindStats.totals.volumesRead > 0 ||
      rewindStats.totals.readingSeconds > 0 ||
      rewindStats.totals.chaptersDownloaded > 0 ||
      rewindStats.totals.seriesAdded > 0 ||
      rewindStats.totals.seriesRemoved > 0)

  const sectionProps: StatsSectionProps = { userId: viewUserId, range, previous, preset, windowLabel }
  const scopes: Partial<Record<StatsSectionKey, string>> = {
    habits: t`All time`,
    library: t`Whole library`,
  }

  return (
    <SurfaceFrame width="full" pageStyle="editorial">
      {introOpen && rewindStats && (
        <RewindIntro
          stats={rewindStats}
          label={String(rewindYear)}
          onClose={() => setIntroOpen(false)}
        />
      )}

      <PageHeader
        title={<Trans>Stats</Trans>}
        description={t`What you read, when you read it, and what the library holds.`}
        actions={
          <>
            <RangeControl
              preset={preset}
              onPresetChange={setPreset}
              year={year}
              onYearChange={setYear}
              month={month}
              onMonthChange={setMonth}
              yearOptions={yearOptions}
            />
            {isAdmin && users && users.length > 1 && (
              <Select
                data={users
                  .filter((u) => !u.pendingSetup)
                  .map((u) => ({ value: String(u.id), label: u.displayName || u.userName }))}
                value={viewUserId === undefined ? String(me?.id ?? '') : String(viewUserId)}
                onChange={(v) => setViewUserId(v && Number(v) !== me?.id ? Number(v) : undefined)}
                w={180}
                size="sm"
                aria-label={t`Reader`}
              />
            )}
            <Button
              leftSection={<IconPlayerPlay size={16} />}
              onClick={() => setIntroOpen(true)}
              disabled={!canPlayRewind}
              title={t`Play the ${rewindYear} retrospective`}
            >
              <Trans>Play Rewind</Trans>
            </Button>
          </>
        }
      />

      <StatsRail />

      <div className="stats-layout">
        {STATS_SECTIONS.map((s) => {
          const Section = SECTION_COMPONENTS[s.key]
          return (
            <StatsSection
              key={s.key}
              sectionKey={s.key}
              icon={s.icon}
              title={renderLabel(s.label)}
              scope={scopes[s.key]}
            >
              <Section {...sectionProps} />
            </StatsSection>
          )
        })}
      </div>
    </SurfaceFrame>
  )
}
