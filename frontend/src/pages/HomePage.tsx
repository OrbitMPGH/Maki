import { useState } from 'react'
import { Link } from 'react-router-dom'
import { Button, Card, Group, SimpleGrid, Skeleton, Text } from '@mantine/core'
import {
  IconBook,
  IconBookmarks,
  IconChevronRight,
  IconCircleCheck,
  IconClock,
  IconDownload,
  IconEye,
  IconFlame,
  IconLibrary,
  IconPlayerPlay,
  IconPlus,
  IconSparkles,
} from '@tabler/icons-react'
import {
  useDiscover,
  useHomeReading,
  useHomeRecentlyAdded,
  useLibraryStats,
  useMetadataSettings,
  useQueue,
  useRecommendations,
  useRootFolders,
  useSeries,
  useSeriesIdLookup,
  type RecommendationItem,
} from '../api/hooks'
import { useReadTracking } from '../api/reader'
import { DiscoverDetailModal } from '../components/DiscoverDetailModal'
import { DownloadingStrip } from '../components/home/DownloadingStrip'
import { ReadingRail } from '../components/home/ReadingRail'
import { RecentlyAddedRail } from '../components/home/RecentlyAddedRail'
import { DiscoverRailRow } from '../components/ui/DiscoverRail'
import { EmptyState } from '../components/ui/EmptyState'
import { PageHeader } from '../components/ui/PageHeader'
import { SectionHeader } from '../components/ui/SectionHeader'
import { StatTile } from '../components/ui/StatTile'
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
      Find more
    </Button>
  )
}

export default function HomePage() {
  const { data: series, isLoading: seriesLoading } = useSeries()
  const { data: metadata } = useMetadataSettings()
  const { data: rootFolders } = useRootFolders()
  const readTracking = useReadTracking()
  const stats = useLibraryStats()

  // Opposite default to the nav's in App.tsx on purpose: there, assuming "available" while the
  // settings load stops the Discover tab flickering in and out. Here it would fire two requests
  // that 400 on an install with no local MangaBaka database, and both surface as error toasts.
  const discoverAvailable = Boolean(metadata?.useLocalDb && metadata?.dumpPresent)
  const hasLibrary = (series?.length ?? 0) > 0

  const { data: reading, isLoading: readingLoading } = useHomeReading()
  const { data: recent } = useHomeRecentlyAdded()
  const { data: queue } = useQueue()
  const { data: rails } = useDiscover(0, discoverAvailable && hasLibrary)
  // An empty request object is deliberate: it hits the same server-side cache slot as Discover's
  // default Recommended tab, so this rail can never thrash that shared pool with different seeds.
  const recommendations = useRecommendations({}, discoverAvailable && hasLibrary)

  const seriesIdFor = useSeriesIdLookup()
  const [detailItem, setDetailItem] = useState<RecommendationItem | null>(null)

  const continueReading = reading?.continueReading ?? []
  const jumpBackIn = reading?.jumpBackIn ?? []
  const downloading = (queue?.items ?? []).filter((q) => isQueueActive(q.status))
  const popular = rails?.find((r) => r.key === 'popular')?.items ?? []
  const youMightLike = recommendations.data?.pages[0]?.similar?.slice(0, RAIL_SIZE) ?? []

  const header = (
    <PageHeader
      title="Home"
      description="Pick up where you left off."
      actions={
        <Button component={Link} to="/add" leftSection={<IconPlus size={16} />}>
          Add series
        </Button>
      }
    />
  )

  if (!seriesLoading && !hasLibrary) {
    return (
      <>
        {header}
        <EmptyState
          icon={IconLibrary}
          title="Nothing in your library yet"
          description="Add a series and Maki will start tracking chapters for it. This page fills up as you read and download."
          actionLabel="Add series"
          actionTo="/add"
        />
      </>
    )
  }

  return (
    <>
      {header}

      {readingLoading ? (
        <RailSkeleton />
      ) : continueReading.length > 0 ? (
        <>
          <SectionHeader icon={IconPlayerPlay} title="Continue reading" count={continueReading.length} />
          <ReadingRail items={continueReading} />
        </>
      ) : (
        // Only nudge when there is genuinely nothing to resume *and* nothing to jump back into —
        // otherwise a user mid-way through their library gets told to start reading.
        jumpBackIn.length === 0 && <StartReadingPrompt tracking={readTracking} />
      )}

      {downloading.length > 0 && (
        <>
          <SectionHeader icon={IconDownload} title="Downloading now" count={downloading.length} />
          <DownloadingStrip items={downloading} />
        </>
      )}

      {recent && recent.length > 0 && (
        <>
          <SectionHeader icon={IconBookmarks} title="Recently added" count={recent.length} />
          <RecentlyAddedRail items={recent} />
        </>
      )}

      {jumpBackIn.length > 0 && (
        <>
          <SectionHeader icon={IconBook} title="Jump back in" count={jumpBackIn.length} />
          <ReadingRail items={jumpBackIn} />
        </>
      )}

      {youMightLike.length > 0 && (
        <>
          <SectionHeader icon={IconSparkles} title="You might like" action={<FindMore />} />
          <DiscoverRailRow items={youMightLike} seriesIdFor={seriesIdFor} onOpen={setDetailItem} />
        </>
      )}

      {popular.length > 0 && (
        <>
          <SectionHeader icon={IconFlame} title="Currently popular" action={<FindMore />} />
          <DiscoverRailRow
            items={popular.slice(0, RAIL_SIZE)}
            seriesIdFor={seriesIdFor}
            onOpen={setDetailItem}
          />
        </>
      )}

      <SectionHeader icon={IconLibrary} title="Library at a glance" />
      <SimpleGrid cols={{ base: 2, sm: readTracking ? 5 : 4 }} spacing="sm">
        <StatTile label="Series" value={stats.total} icon={IconLibrary} accent="brand" />
        <StatTile label="Monitored" value={stats.monitored} icon={IconEye} accent="info" />
        <StatTile label="On disk" value={stats.downloaded} icon={IconCircleCheck} accent="ok" />
        <StatTile label="Missing" value={stats.missing} icon={IconDownload} accent="warn" />
        {readTracking && (
          <StatTile label="Chapters read" value={stats.read ?? 0} icon={IconBook} accent="brand" />
        )}
      </SimpleGrid>

      <DiscoverDetailModal
        item={detailItem}
        inLibrarySeriesId={detailItem ? seriesIdFor(detailItem) : null}
        rootFolders={rootFolders}
        onClose={() => setDetailItem(null)}
      />
    </>
  )
}

function RailSkeleton() {
  return (
    <div className="discover-rail" style={{ marginTop: 'var(--mantine-spacing-xl)' }}>
      {Array.from({ length: 6 }, (_, i) => (
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
  return (
    <Card withBorder radius="lg" padding="lg" mt="xl">
      <Group gap="sm" justify="space-between" wrap="wrap">
        <div style={{ minWidth: 0 }}>
          <Text fw={650}>Nothing to pick up yet</Text>
          <Text size="sm" c="dimmed" mt={4}>
            {tracking
              ? 'Open a chapter and it will show up here, ready to resume.'
              : 'Open any chapter in the built-in reader — or connect Kavita — and Maki starts tracking where you are.'}
          </Text>
        </div>
        <Button component={Link} to="/library" variant="light" leftSection={<IconClock size={16} />}>
          Browse library
        </Button>
      </Group>
    </Card>
  )
}
