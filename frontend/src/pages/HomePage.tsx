import { Fragment, useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { Box, Button, Skeleton } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import {
  IconBook,
  IconBookmarks,
  IconChevronRight,
  IconClock,
  IconDownload,
  IconFlame,
  IconLayoutList,
  IconLibrary,
  IconPlayerPlay,
  IconPlus,
  IconSparkles,
} from '@tabler/icons-react'
import {
  HOME_SECTIONS,
  useDiscover,
  useProgressSummary,
  useHomeReading,
  useHomeRecentlyAdded,
  useLibraryStats,
  useMetadataSettings,
  useQueue,
  useRecommendations,
  useRootFolders,
  useSeries,
  useSeriesIdLookup,
  useUiSettings,
  type HomeSectionKey,
  type RecommendationItem,
} from '../api/hooks'
import { useReadTracking } from '../api/reader'
import { DiscoverDetailModal } from '../components/discover/DiscoverDetailModal'
import { ContinueLead, CONTINUE_LEAD_MAX } from '../components/home/ContinueLead'
import { ContinueRail } from '../components/home/ContinueRail'
import { DownloadingStrip } from '../components/home/DownloadingStrip'
import { HomeHero, type HomeHeroFigure } from '../components/home/HomeHero'
import { RecentlyAddedRail } from '../components/home/RecentlyAddedRail'
import { DiscoverRailRow, EngineRailRow } from '../components/ui/DiscoverRail'
import { EmptyState } from '../components/ui/EmptyState'
import { SectionHeader } from '../components/ui/SectionHeader'
import { SurfaceFrame } from '../components/ui/SurfaceFrame'
import { isQueueActive } from '../components/ui/status'

/** How many catalogue picks each borrowed Discover rail shows before "Find more". */
const RAIL_SIZE = 20

function FindMore() {
  return (
    <Button
      component={Link}
      to="/discover"
      variant="subtle"
      size="compact-sm"
      rightSection={<IconChevronRight size={14} />}
    >
      <Trans>Find more</Trans>
    </Button>
  )
}

export default function HomePage() {
  const { t } = useLingui()
  const { data: series, isLoading: seriesLoading } = useSeries()
  const { data: metadata } = useMetadataSettings()
  const { data: rootFolders } = useRootFolders()
  const { data: ui } = useUiSettings()
  const readTracking = useReadTracking()
  const stats = useLibraryStats()

  // Opposite default to the nav's in App.tsx on purpose: there, assuming "available" while the
  // settings load stops the Discover tab flickering in and out. Here it would fire two requests
  // that 400 on an install with no local MangaBaka database, and both surface as error toasts.
  const discoverAvailable = Boolean(metadata?.useLocalDb && metadata?.dumpPresent)
  const hasLibrary = (series?.length ?? 0) > 0

  // Default to the shipping order while the setting loads, so the page doesn't reflow once it
  // arrives. `on` is what every query below gates on: a section the user turned off must not
  // cost a request, which is most of the point of being able to turn one off.
  const layout = ui?.homeLayout.sections ?? HOME_SECTIONS.map((key) => ({ key, enabled: true }))
  const on = (key: HomeSectionKey) => layout.some((s) => s.key === key && s.enabled)

  const needsReading = on('continue') || on('jumpback')
  const needsDiscover = discoverAvailable && hasLibrary

  const { data: reading, isLoading: readingLoading } = useHomeReading(12, needsReading)
  const { data: recent } = useHomeRecentlyAdded(12, on('recent'))
  const { data: queue } = useQueue()
  const { data: rails } = useDiscover(0, needsDiscover && on('popular'))
  // An empty request object is deliberate: it hits the same server-side cache slot as Discover's
  // default Recommended tab, so this rail can never thrash that shared pool with different seeds.
  const recommendations = useRecommendations({}, needsDiscover && on('recommended'))
  const { data: progress } = useProgressSummary(undefined, on('progress'))

  const seriesIdFor = useSeriesIdLookup()
  const [detailItem, setDetailItem] = useState<RecommendationItem | null>(null)

  const continueReading = reading?.continueReading ?? []
  const jumpBackIn = reading?.jumpBackIn ?? []
  const downloading = (queue?.items ?? []).filter((q) => isQueueActive(q.status))

  // What is left to read, off the library list that is already loaded.
  //
  // `readChapterCount` null means "never tracked" rather than "nothing read", so those series are
  // left out of every figure here: counting them would report a whole untracked library as unread.
  const waiting = useMemo(() => {
    let unread = 0
    let started = 0
    let finished = 0
    for (const s of series ?? []) {
      if (s.readChapterCount == null || s.chapterFileCount === 0) continue
      unread += Math.max(0, s.chapterFileCount - s.readChapterCount)
      if (s.readChapterCount >= s.chapterFileCount) finished++
      else if (s.readChapterCount > 0) started++
    }
    return { unread, started, finished }
  }, [series])
  const popular = rails?.find((r) => r.key === 'popular')?.items ?? []
  const youMightLike = recommendations.data?.pages[0]?.similar?.slice(0, RAIL_SIZE) ?? []

  // Every figure the band carries, gated the same way the glance panels were: each of these three
  // groups is still its own switch in Settings, they just share the band's panel now instead of a
  // row of panels of their own.
  const libraryFigures: HomeHeroFigure[] = on('stats')
    ? [
        { label: t`Series`, value: stats.total },
        { label: t`Monitored`, value: stats.monitored },
        { label: t`On disk`, value: stats.downloaded, tone: 'ok' },
        { label: t`Missing`, value: stats.missing, tone: 'warn' },
      ]
    : []

  // Only ever with tracking on: without it every downloaded chapter reads as unread, and the band
  // would tell a Kavita-less library that it has 12,000 chapters waiting.
  const readingFigures: HomeHeroFigure[] =
    on('toread') && readTracking
      ? [
          { label: t`Unread`, value: waiting.unread },
          { label: t`Started`, value: waiting.started },
          { label: t`Finished`, value: waiting.finished, tone: 'ok' },
        ]
      : []

  // First cover the page already holds, in the order the sections read in: what you are part-way
  // through, then what you put down, then what just arrived.
  const heroCover =
    continueReading.find((i) => i.coverUrl)?.coverUrl ??
    jumpBackIn.find((i) => i.coverUrl)?.coverUrl ??
    (recent ?? []).find((i) => i.coverUrl)?.coverUrl ??
    null

  const header = (
    <HomeHero
      coverUrl={heroCover}
      greeting={t`Pick up where you left off.`}
      loading={seriesLoading}
      figures={libraryFigures}
      readingFigures={readingFigures}
      progress={progress?.enabled ? progress : undefined}
      actions={
        <Button component={Link} to="/add" leftSection={<IconPlus size={16} />}>
          <Trans>Add series</Trans>
        </Button>
      }
    />
  )

  if (!seriesLoading && !hasLibrary) {
    return (
      <SurfaceFrame width="full" pageStyle="editorial">
        {header}
        <EmptyState
          icon={IconLibrary}
          title={t`Nothing in your library yet`}
          description={t`Add a series and Maki will start tracking chapters for it. This page fills up as you read and download.`}
          actionLabel={t`Add series`}
          actionTo="/add"
        />
      </SurfaceFrame>
    )
  }

  // One node per section key. Rendered in the user's order below; a section with nothing to show
  // yields null and takes up no space, exactly as when it is switched off.
  const sections: Record<HomeSectionKey, React.ReactNode> = {
    continue: readingLoading ? (
      <RailSkeleton />
    ) : continueReading.length > 0 ? (
      <>
        <SectionHeader icon={IconPlayerPlay} title={t`Continue reading`} count={continueReading.length} />
        <ContinueLead items={continueReading.slice(0, CONTINUE_LEAD_MAX)} />
        {continueReading.length > CONTINUE_LEAD_MAX && (
          <ContinueRail items={continueReading.slice(CONTINUE_LEAD_MAX)} />
        )}
      </>
    ) : (
      // Only nudge when there is genuinely nothing to resume *and* nothing to jump back into,
      // otherwise a user mid-way through their library gets told to start reading.
      jumpBackIn.length === 0 && <StartReadingPrompt tracking={readTracking} />
    ),

    downloading: downloading.length > 0 && (
      <>
        <SectionHeader icon={IconDownload} title={t`Downloading now`} count={downloading.length} />
        <DownloadingStrip items={downloading} />
      </>
    ),

    recent: recent && recent.length > 0 && (
      <>
        <SectionHeader icon={IconBookmarks} title={t`Recently added`} count={recent.length} />
        <RecentlyAddedRail items={recent} />
      </>
    ),

    jumpback: jumpBackIn.length > 0 && (
      <>
        <SectionHeader icon={IconBook} title={t`Jump back in`} count={jumpBackIn.length} />
        <ContinueLead items={jumpBackIn.slice(0, CONTINUE_LEAD_MAX)} />
        {jumpBackIn.length > CONTINUE_LEAD_MAX && (
          <ContinueRail items={jumpBackIn.slice(CONTINUE_LEAD_MAX)} />
        )}
      </>
    ),

    recommended: youMightLike.length > 0 && (
      <>
        <SectionHeader icon={IconSparkles} title={t`You might like`} action={<FindMore />} />
        <EngineRailRow items={youMightLike} seriesIdFor={seriesIdFor} onOpen={setDetailItem} />
      </>
    ),

    popular: popular.length > 0 && (
      <>
        <SectionHeader icon={IconFlame} title={t`Currently popular`} action={<FindMore />} />
        <DiscoverRailRow
          items={popular.slice(0, RAIL_SIZE)}
          seriesIdFor={seriesIdFor}
          onOpen={setDetailItem}
        />
      </>
    ),

    // All three live in the page band above, not in the ordered list.
    stats: null,
    progress: null,
    toread: null,
  }

  const visible = layout.filter((s) => s.enabled)

  return (
    <SurfaceFrame width="full" pageStyle="editorial">
      {header}

      {visible.length === 0 ? (
        <EmptyState
          icon={IconLayoutList}
          title={t`Every section is switched off`}
          description={t`Home has nothing to show. Turn sections back on, or disable Home entirely, in Settings.`}
          actionLabel={t`Open settings`}
          actionTo="/settings"
        />
      ) : (
        visible.map((s) => <Fragment key={s.key}>{sections[s.key]}</Fragment>)
      )}

      <DiscoverDetailModal
        item={detailItem}
        feedbackContext={{ surface: 'home' }}
        inLibrarySeriesId={detailItem ? seriesIdFor(detailItem) : null}
        rootFolders={rootFolders}
        onClose={() => setDetailItem(null)}
      />
    </SurfaceFrame>
  )
}

function RailSkeleton() {
  return (
    <div className="discover-rail" style={{ marginTop: 'var(--mantine-spacing-xl)' }}>
      {Array.from({ length: 12 }, (_, i) => (
        <div key={i} className="discover-rail-item">
          <Skeleton radius="lg" style={{ aspectRatio: '2 / 3' }} />
        </div>
      ))}
    </div>
  )
}

/**
 * Shown when nothing has been read yet. Split by whether progress is tracked at all: with no
 * tracking configured the rails would stay empty no matter how much the user reads elsewhere.
 */
function StartReadingPrompt({ tracking }: { tracking: boolean }) {
  const { t } = useLingui()
  return (
    <Box mt="xl">
      <EmptyState
        icon={IconClock}
        title={t`Nothing to pick up yet`}
        description={
          tracking ? (
            <Trans>Open a chapter and it will show up here, ready to resume.</Trans>
          ) : (
            <Trans>
              Open any chapter in the built-in reader, or connect Kavita, and Maki starts tracking
              where you are.
            </Trans>
          )
        }
        actionLabel={t`Browse library`}
        actionTo="/library"
      />
    </Box>
  )
}
