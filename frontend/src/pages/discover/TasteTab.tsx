import { useMemo, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import {
  Alert,
  Anchor,
  Badge,
  Button,
  Card,
  Center,
  Divider,
  Group,
  Loader,
  SegmentedControl,
  SimpleGrid,
  Stack,
  Text,
  Tooltip,
} from '@mantine/core'
import { BarChart, DonutChart } from '@mantine/charts'
import {
  IconAlertCircle,
  IconArrowsShuffle,
  IconCalendar,
  IconChartPie,
  IconClock,
  IconCompass,
  IconFilter,
  IconLock,
  IconPencil,
  IconRoute,
  IconSparkles,
  IconTags,
  IconTelescope,
} from '@tabler/icons-react'
import type {
  BehaviourSeries,
  ReadingBehaviour,
  TasteCluster,
  TasteFacet,
  TasteInsights,
  TasteMember,
  TasteView,
} from '../../api/hooks'
import { useReadingBehaviour, useTasteInsights, useTasteProfile } from '../../api/hooks'
import { SectionHeader } from '../../components/ui/SectionHeader'
import { StatTile } from '../../components/ui/StatTile'
import { SeriesLink, SeriesThumb } from '../stats/SeriesLink'
import { buildFiltersFromProfile, hasAnyFilter } from './tasteFilters'

const SLICE_COLORS = [
  'var(--brand)',
  'var(--info)',
  'var(--ok)',
  'var(--warn)',
  'var(--danger)',
  'var(--mantine-color-dark-3)',
]

/** How many of each composition facet the demoted row shows. */
const HEAD = 6

/**
 * Over-index badges only start here. Between roughly 1 and this a facet is proportional to the
 * shelf, and labelling that "1.1x" invites reading noise as a preference.
 */
const NOTEWORTHY = 1.25

function percent(share: number): string {
  return `${Math.round(share * 100)}%`
}

function ratio(value: number): string {
  return `${value >= 10 ? Math.round(value) : value.toFixed(1)}x`
}

/** A group's name is whatever makes it different from the reader's other groups. */
function clusterName(cluster: TasteCluster, index: number): string {
  return cluster.distinctiveTags.length > 0
    ? cluster.distinctiveTags.slice(0, 2).join(' + ')
    : `Group ${index + 1}`
}

/**
 * Coherence in words. The raw cosine means nothing to a reader, and the useful distinction is only
 * ever three-way: this group is one thing, a theme, or a loose pile.
 */
function coherenceLabel(coherence: number): string {
  if (coherence >= 0.72) return 'very tight'
  if (coherence >= 0.6) return 'consistent'
  return 'loose'
}

function ClusterCard({
  cluster,
  index,
  onRecommend,
}: {
  cluster: TasteCluster
  index: number
  onRecommend: (cluster: TasteCluster) => void
}) {
  return (
    <Card padding="md" radius="lg" withBorder>
      <Group justify="space-between" align="flex-start" wrap="nowrap" mb="xs">
        <div style={{ minWidth: 0 }}>
          <Text fw={650} truncate>
            {clusterName(cluster, index)}
          </Text>
          <Text c="dimmed" size="xs">
            {cluster.size} series, {percent(cluster.share)} of this view,{' '}
            {coherenceLabel(cluster.coherence)}
          </Text>
        </div>
        <Button
          size="compact-sm"
          variant="light"
          leftSection={<IconSparkles size={14} />}
          onClick={() => onRecommend(cluster)}
          style={{ flexShrink: 0 }}
        >
          More like this
        </Button>
      </Group>

      {cluster.distinctiveTags.length > 0 && (
        <Group gap={6} mb="sm">
          {cluster.distinctiveTags.map((tag) => (
            <Badge key={tag} variant="light" color="grape" size="sm">
              {tag}
            </Badge>
          ))}
        </Group>
      )}

      <Stack gap={6}>
        {cluster.examples.map((m: TasteMember) => (
          <Group key={m.seriesId} gap={8} wrap="nowrap">
            <SeriesThumb url={m.coverUrl} alt={m.title} />
            <Text size="sm" truncate style={{ flex: 1, minWidth: 0 }}>
              <SeriesLink id={m.seriesId} title={m.title} />
            </Text>
          </Group>
        ))}
      </Stack>

      {cluster.blindSpot && (
        <>
          <Divider my="sm" />
          <Group gap={6} wrap="nowrap" mb={4}>
            <IconTelescope size={14} style={{ color: 'var(--warn)', flexShrink: 0 }} />
            <Text size="xs" fw={600}>
              Next door, and you own none of it
            </Text>
          </Group>
          <Group gap={6} mb={4}>
            {cluster.blindSpot.tags.map((tag) => (
              <Badge key={tag} variant="outline" color="yellow" size="xs">
                {tag}
              </Badge>
            ))}
          </Group>
          <Text c="dimmed" size="xs">
            {cluster.blindSpot.examples
              .map((e) => (e.year ? `${e.title} (${e.year})` : e.title))
              .join(', ')}
          </Text>
        </>
      )}
    </Card>
  )
}

/** The three pace and abandonment lists, which all read the same way. */
function BehaviourList({
  icon: ListIcon,
  title,
  items,
  emptyText,
}: {
  icon: typeof IconClock
  title: string
  items: BehaviourSeries[]
  emptyText: string
}) {
  return (
    <Card padding="md" radius="lg" withBorder>
      <Group gap={8} mb="xs" wrap="nowrap">
        <ListIcon size={16} style={{ color: 'var(--brand)', flexShrink: 0 }} />
        <Text fw={650}>{title}</Text>
      </Group>
      {items.length === 0 ? (
        <Text c="dimmed" size="sm">
          {emptyText}
        </Text>
      ) : (
        <Stack gap={8}>
          {items.map((item) => (
            <Group key={item.seriesId} gap={8} wrap="nowrap">
              <SeriesThumb url={item.coverUrl} alt={item.title} />
              <Text size="sm" truncate style={{ flex: 1, minWidth: 0 }}>
                <SeriesLink id={item.seriesId} title={item.title} />
              </Text>
              <Text size="sm" fw={600} className="tnum" style={{ flexShrink: 0 }}>
                {item.value}
              </Text>
            </Group>
          ))}
        </Stack>
      )}
    </Card>
  )
}

function BehaviourSection({ behaviour }: { behaviour: ReadingBehaviour }) {
  const pace = behaviour.medianSecondsPerChapter
  return (
    <>
      <SimpleGrid cols={{ base: 2, md: 4 }} spacing="md">
        <StatTile
          label="You finish"
          value={behaviour.finishRate === null ? '-' : percent(behaviour.finishRate)}
          icon={IconChartPie}
        />
        <StatTile
          label="Typical chapter"
          value={
            pace === null ? '-' : pace >= 90 ? `${Math.round(pace / 60)} min` : `${Math.round(pace)} s`
          }
          icon={IconClock}
          accent="info"
        />
        <StatTile
          label="You bail around"
          value={
            behaviour.medianStopPoint === null ? '-' : `${percent(behaviour.medianStopPoint)} in`
          }
          icon={IconArrowsShuffle}
          accent="warn"
        />
        <StatTile
          label="Biggest day"
          value={behaviour.biggestDayCount === null ? '-' : `${behaviour.biggestDayCount} ch`}
          icon={IconCalendar}
          accent="ok"
        />
      </SimpleGrid>

      <Text c="dimmed" size="xs" mt={6}>
        {behaviour.seriesFinished} of {behaviour.seriesStarted} series read to the end of what you
        hold
        {behaviour.timedChapters > 0
          ? `. Pace is from ${behaviour.timedChapters.toLocaleString()} timed chapters; only the built-in reader records time.`
          : '. No chapter here carries a reading time, so there is no pace to report. Only the built-in reader records it.'}
      </Text>

      <SimpleGrid cols={{ base: 1, lg: 3 }} spacing="md" mt="md">
        <BehaviourList
          icon={IconClock}
          title="You slow down for"
          items={behaviour.savoured}
          emptyText="Not enough timed chapters yet."
        />
        <BehaviourList
          icon={IconSparkles}
          title="You tear through"
          items={behaviour.devoured}
          emptyText="Not enough timed chapters yet."
        />
        <BehaviourList
          icon={IconArrowsShuffle}
          title="You put down"
          items={behaviour.abandoned}
          emptyText="You finish what you start."
        />
      </SimpleGrid>
    </>
  )
}

function DriftSection({ insights }: { insights: TasteInsights }) {
  const data = insights.drift.map((d) => ({ bucket: d.bucket, similarity: d.similarityToStart }))

  return (
    <Card padding="md" radius="lg" withBorder>
      <Text c="dimmed" size="xs" mb="md">
        How close each quarter sat to where you started. Falling means you moved.
      </Text>
      <BarChart
        h={180}
        data={data}
        dataKey="bucket"
        series={[{ name: 'similarity', color: 'var(--brand)', label: 'Similarity to start' }]}
        valueFormatter={(v) => v.toFixed(2)}
        yAxisProps={{ domain: [0, 1] }}
        withTooltip
        gridAxis="y"
      />
      <Stack gap={8} mt="md">
        {insights.drift.map((point) => (
          <Group key={point.bucket} gap={8} wrap="nowrap">
            <Text size="sm" fw={600} className="tnum" style={{ width: 72, flexShrink: 0 }}>
              {point.bucket}
            </Text>
            <Group gap={4} style={{ flexShrink: 0 }}>
              {point.distinctiveTags.slice(0, 2).map((tag) => (
                <Badge key={tag} variant="light" color="grape" size="xs">
                  {tag}
                </Badge>
              ))}
            </Group>
            <Text c="dimmed" size="xs" truncate style={{ flex: 1, minWidth: 0 }}>
              {point.example ? point.example.title : ''}
            </Text>
            <Text c="dimmed" size="xs" className="tnum" style={{ flexShrink: 0 }}>
              {point.seriesCount}
            </Text>
          </Group>
        ))}
      </Stack>
    </Card>
  )
}

/** The over-index badge, or nothing when support is thin or the reader is simply proportional. */
function OverIndex({ facet }: { facet: TasteFacet }) {
  if (facet.overIndexShelf === null || facet.overIndexShelf < NOTEWORTHY) return null
  return (
    <Tooltip
      label={`Reached for ${ratio(facet.overIndexShelf)} more than owning it would predict, across ${facet.support} series`}
      multiline
      w={260}
    >
      <Badge variant="light" color="brand" size="sm" style={{ flexShrink: 0 }}>
        {ratio(facet.overIndexShelf)}
      </Badge>
    </Tooltip>
  )
}

/** The catalogue badge, which measures something different and has to say so. */
function AgainstCatalogue({ facet }: { facet: TasteFacet }) {
  if (facet.overIndexCatalogue === null || facet.overIndexCatalogue < NOTEWORTHY) return null
  return (
    <Tooltip
      label={`${ratio(facet.overIndexCatalogue)} more than the MangaBaka catalogue carries, weighted toward titles more people read`}
      multiline
      w={260}
    >
      <Badge variant="outline" color="gray" size="sm" style={{ flexShrink: 0 }}>
        cat {ratio(facet.overIndexCatalogue)}
      </Badge>
    </Tooltip>
  )
}

function CompositionCard({ title, facets }: { title: string; facets: TasteFacet[] }) {
  const data = facets.slice(0, 6).map((f, i) => ({
    name: f.name,
    value: f.share,
    color: SLICE_COLORS[i % SLICE_COLORS.length],
  }))
  const byName = new Map(facets.map((f) => [f.name, f]))

  return (
    <Card padding="md" radius="lg" withBorder>
      <Text fw={650} mb="md">
        {title}
      </Text>
      {data.length === 0 ? (
        <Text c="dimmed" size="sm">
          Nothing to show yet.
        </Text>
      ) : (
        <Group align="center" gap="xl" wrap="nowrap">
          <DonutChart data={data} size={140} thickness={20} withTooltip valueFormatter={percent} />
          <Stack gap={6} style={{ minWidth: 0, flex: 1 }}>
            {data.map((d) => (
              <Group key={d.name} gap={8} wrap="nowrap">
                <span
                  style={{
                    width: 10,
                    height: 10,
                    borderRadius: 3,
                    background: d.color,
                    flexShrink: 0,
                  }}
                />
                <Text size="sm" truncate style={{ flex: 1, minWidth: 0 }}>
                  {d.name}
                </Text>
                {byName.get(d.name) && <OverIndex facet={byName.get(d.name)!} />}
                <Text size="xs" c="dimmed" className="tnum" style={{ flexShrink: 0 }}>
                  {percent(d.value)}
                </Text>
              </Group>
            ))}
          </Stack>
        </Group>
      )}
    </Card>
  )
}

function CreatorsCard({ facets }: { facets: TasteFacet[] }) {
  return (
    <Card padding="md" radius="lg" withBorder>
      <Group gap={8} mb="xs" wrap="nowrap">
        <IconPencil size={16} style={{ color: 'var(--brand)', flexShrink: 0 }} />
        <Text fw={650}>Creators</Text>
      </Group>
      {facets.length === 0 ? (
        <Text c="dimmed" size="sm">
          No creator shows up often enough yet.
        </Text>
      ) : (
        <Stack gap={8}>
          {facets.slice(0, HEAD).map((f, i) => (
            <Group key={f.name} gap={8} wrap="nowrap">
              <Text c="dimmed" fw={700} size="sm" className="tnum" style={{ width: 18 }}>
                {i + 1}
              </Text>
              <Text size="sm" truncate style={{ flex: 1, minWidth: 0 }}>
                <Anchor component={Link} to={`/creator/${encodeURIComponent(f.name)}`} inherit>
                  {f.name}
                </Anchor>
              </Text>
              <Text size="xs" c="dimmed" className="tnum" style={{ flexShrink: 0 }}>
                {f.support} series
              </Text>
            </Group>
          ))}
        </Stack>
      )}
    </Card>
  )
}

function TagsCard({
  facets,
  onExplore,
}: {
  facets: TasteFacet[]
  onExplore: (tag: string) => void
}) {
  return (
    <Card padding="md" radius="lg" withBorder>
      <Group gap={8} mb="xs" wrap="nowrap">
        <IconTags size={16} style={{ color: 'var(--brand)', flexShrink: 0 }} />
        <Text fw={650}>Tags</Text>
      </Group>
      {facets.length === 0 ? (
        <Text c="dimmed" size="sm">
          No tags yet. They come from the catalogue, so a library the dump does not cover has none.
        </Text>
      ) : (
        <Stack gap={8}>
          {facets.slice(0, HEAD).map((f) => (
            <Group key={f.name} gap={8} wrap="nowrap">
              <Badge
                variant="light"
                color="grape"
                size="sm"
                style={{ cursor: 'pointer', flexShrink: 0 }}
                onClick={() => onExplore(f.name)}
              >
                {f.name}
              </Badge>
              <div style={{ flex: 1 }} />
              <OverIndex facet={f} />
              <AgainstCatalogue facet={f} />
              <Text size="xs" c="dimmed" className="tnum" style={{ flexShrink: 0 }}>
                {f.support}
              </Text>
            </Group>
          ))}
        </Stack>
      )}
    </Card>
  )
}

/**
 * The reader as the vectors see them.
 *
 * The sections lead with what only the embedding space can answer: which distinct things somebody
 * reads, which of their series is the odd one out, what sits next to them untouched, and how their
 * taste has moved. Genre and tag composition is a different question, already answered on the Stats
 * page, and sits at the bottom as reference rather than as the point.
 *
 * Private by construction: every endpoint behind it answers only for whoever asked.
 */
export function TasteTab() {
  const [view, setView] = useState<TasteView>('read')
  const navigate = useNavigate()
  const { data: insights, isLoading: insightsLoading } = useTasteInsights(view)
  const { data: behaviour, isLoading: behaviourLoading } = useReadingBehaviour()
  const { data: profile, isLoading: profileLoading, error } = useTasteProfile(view)

  const filters = useMemo(() => (profile ? buildFiltersFromProfile(profile) : {}), [profile])

  const apply = (payload = filters, seeds?: { id: number; title: string | null }[]) =>
    navigate('/discover/recommended', {
      state: { recommendationFilters: payload, seeds, source: 'taste-profile' },
    })

  /** Recommend from one group alone, which the single blended centroid cannot express. */
  const recommendCluster = (cluster: TasteCluster) =>
    apply(
      {},
      cluster.seedIds.map((id, i) => ({
        // Only the examples came back with titles; the rest ride as bare ids and the panel
        // labels them from the library once it loads.
        id,
        title: cluster.examples[i]?.title ?? null,
      })),
    )

  if (insightsLoading && behaviourLoading && profileLoading) {
    return (
      <Center py="xl">
        <Loader />
      </Center>
    )
  }

  if (error) {
    return (
      <Alert color="red" icon={<IconAlertCircle size={16} />} title="Could not read your profile">
        {String(error)}
      </Alert>
    )
  }

  const nothingAtAll =
    (!behaviour || behaviour.chaptersRead === 0) &&
    (!profile || profile.seriesCount === 0) &&
    (!insights || insights.clusters.length === 0)

  if (nothingAtAll) {
    return (
      <Alert color="gray" icon={<IconAlertCircle size={16} />} title="Nothing to profile yet">
        Read a few chapters and this fills in.
      </Alert>
    )
  }

  return (
    <Stack gap="md">
      <Group justify="space-between" align="center" wrap="wrap">
        <SegmentedControl
          value={view}
          onChange={(v) => setView(v as TasteView)}
          data={[
            { value: 'read', label: "What you've read" },
            { value: 'shelf', label: 'Everything you own' },
          ]}
        />
        <Text c="dimmed" size="sm">
          {profile
            ? view === 'read'
              ? `From ${profile.seriesCount} of your ${profile.libraryCount} series`
              : `From all ${profile.seriesCount} series, weighted by what you read`
            : ''}
        </Text>
      </Group>

      {behaviour && behaviour.chaptersRead > 0 && (
        <>
          <SectionHeader icon={IconClock} title="How you read" />
          <BehaviourSection behaviour={behaviour} />
        </>
      )}

      <SectionHeader
        icon={IconCompass}
        title="What you read, grouped"
        count={insights?.clusters.length ? insights.clusters.length : undefined}
      />
      {insights?.unavailable || insights?.clustersUnavailable ? (
        <Alert color="gray" icon={<IconAlertCircle size={16} />}>
          {insights.unavailable ?? insights.clustersUnavailable}
        </Alert>
      ) : (
        <>
          <Text c="dimmed" size="xs">
            Your library placed in the recommendation index and grouped by feel, not by genre. Each
            group is named by what separates it from your others.
          </Text>
          <SimpleGrid cols={{ base: 1, lg: 2 }} spacing="md">
            {insights?.clusters.map((cluster, i) => (
              <ClusterCard key={i} cluster={cluster} index={i} onRecommend={recommendCluster} />
            ))}
          </SimpleGrid>
          {insights?.oddOneOut && (
            <Card padding="md" radius="lg" withBorder>
              <Group gap={8} wrap="nowrap">
                <IconArrowsShuffle size={16} style={{ color: 'var(--warn)', flexShrink: 0 }} />
                <Text fw={650} style={{ flexShrink: 0 }}>
                  The odd one out
                </Text>
                <SeriesThumb url={insights.oddOneOut.coverUrl} alt={insights.oddOneOut.title} />
                <Text size="sm" truncate style={{ flex: 1, minWidth: 0 }}>
                  <SeriesLink id={insights.oddOneOut.seriesId} title={insights.oddOneOut.title} />
                </Text>
                <Text c="dimmed" size="xs" style={{ flexShrink: 0 }}>
                  least like anything else you read
                </Text>
              </Group>
            </Card>
          )}
        </>
      )}

      {insights && !insights.unavailable && (
        <>
          <SectionHeader icon={IconRoute} title="Where your taste has moved" />
          {insights.driftUnavailable ? (
            <Alert color="gray" icon={<IconAlertCircle size={16} />}>
              {insights.driftUnavailable}
            </Alert>
          ) : (
            <DriftSection insights={insights} />
          )}
        </>
      )}

      {profile && (
        <>
          <SectionHeader
            icon={IconChartPie}
            title="Composition"
            action={
              <Button
                leftSection={<IconFilter size={16} />}
                variant="subtle"
                size="compact-sm"
                disabled={!hasAnyFilter(filters)}
                onClick={() => apply()}
              >
                Recommend from this
              </Button>
            }
          />
          <Text c="dimmed" size="xs">
            The same counts the Stats page shows, kept here so the groups above have something to
            sit against.
          </Text>
          {!profile.catalogueBaselineAvailable && (
            <Text c="dimmed" size="xs">
              Comparisons against the wider catalogue need the embedding index. Until it is built,
              the badges compare against your own library only.
            </Text>
          )}
          <SimpleGrid cols={{ base: 1, lg: 2 }} spacing="md">
            <CompositionCard title="Genres" facets={profile.genres} />
            <CompositionCard title="Formats" facets={profile.types} />
            <CreatorsCard facets={profile.creators} />
            <TagsCard facets={profile.tags} onExplore={(tag) => apply({ tags: [tag] })} />
          </SimpleGrid>
        </>
      )}

      <Group gap="xs" mt="xs">
        <IconLock size={14} style={{ color: 'var(--mantine-color-dimmed)' }} />
        <Text c="dimmed" size="xs">
          Only you can see this. It is built from the same weights that pick your recommendations.
        </Text>
      </Group>
    </Stack>
  )
}
