import { useMemo, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import {
  Alert,
  Anchor,
  Badge,
  Button,
  Card,
  Center,
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
} from '@tabler/icons-react'
import { Trans, useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import type {
  BehaviourSeries,
  ReadingBehaviour,
  TasteFacet,
  TasteGroup,
  TasteInsights,
  TasteMember,
  TasteView,
  RecommendationItem,
} from '../../api/hooks'
import {
  useReadingBehaviour,
  useRootFolders,
  useSeriesIdLookup,
  useTasteInsights,
  useTasteProfile,
} from '../../api/hooks'
import { DiscoverDetailModal } from '../../components/discover/DiscoverDetailModal'
import { DiscoverRailRow } from '../../components/ui/DiscoverRail'
import { SectionHeader } from '../../components/ui/SectionHeader'
import { StatTile } from '../../components/ui/StatTile'
import { SeriesLink, SeriesThumb } from '../stats/SeriesLink'
import { buildFiltersFromProfile, hasAnyFilter } from './tasteFilters'
import { SignalsCard } from './FeedbackLab'
import { formatNumber, formatReadingTime } from '../../format'
import { GENRE_LABELS, TYPE_LABELS } from '../../components/CatalogueFilters'
import { useLabel } from '../../i18n-context'

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

function GroupCard({
  group,
  onOpen,
  seriesIdFor,
}: {
  group: TasteGroup
  onOpen: (item: RecommendationItem) => void
  seriesIdFor: (item: RecommendationItem) => number | null
}) {
  const { t } = useLingui()
  const { size, share, coherence } = group
  const shareLabel = percent(share)
  // Coherence in words. The raw cosine means nothing to a reader, and the useful distinction is
  // only ever three-way: this group is one thing, a theme, or a loose pile.
  //
  // The bands sit higher than they did for the old k-means groups. A group defined by a tag its
  // members all carry starts out tight - the simulated library's twelve run 0.74 to 0.87 - so the
  // old 0.72 floor called every one of them "very tight" and said nothing.
  const coherenceWord = coherence >= 0.84 ? t`very tight` : coherence >= 0.76 ? t`consistent` : t`loose`
  const summary = plural(size, {
    one: `# series, ${shareLabel} of this view, ${coherenceWord}`,
    other: `# series, ${shareLabel} of this view, ${coherenceWord}`,
  })

  return (
    <Card padding="md" radius="lg" withBorder>
      <Group justify="space-between" align="center" wrap="wrap" gap="xs" mb="xs">
        <Text fw={650} style={{ minWidth: 0 }}>
          {group.label}
        </Text>
        <Text c="dimmed" size="xs" style={{ flexShrink: 0 }}>
          {summary}
        </Text>
      </Group>

      <Group gap="sm" wrap="wrap" mb={group.picks.length > 0 ? 'sm' : 0}>
        {group.examples.map((m: TasteMember) => (
          <Group key={m.seriesId} gap={6} wrap="nowrap" style={{ maxWidth: 220 }}>
            <SeriesThumb url={m.coverUrl} alt={m.title} />
            <Text size="sm" truncate style={{ minWidth: 0 }}>
              <SeriesLink id={m.seriesId} title={m.title} />
            </Text>
          </Group>
        ))}
      </Group>

      {group.picks.length > 0 && (
        <>
          <Text size="xs" fw={600} mb={4}>
            <Trans>More of this</Trans>
          </Text>
          <DiscoverRailRow items={group.picks} seriesIdFor={seriesIdFor} onOpen={onOpen} />
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
  const { t } = useLingui()
  const pace = behaviour.medianSecondsPerChapter
  const { seriesFinished, seriesStarted, timedChapters, biggestDayCount } = behaviour
  const stopPercent = behaviour.medianStopPoint === null ? null : percent(behaviour.medianStopPoint)
  const formattedTimed = formatNumber(timedChapters)

  const readSummary = plural(seriesStarted, {
    one: `${seriesFinished} of # series read to the end of what you hold.`,
    other: `${seriesFinished} of # series read to the end of what you hold.`,
  })
  const paceSummary =
    timedChapters > 0
      ? plural(timedChapters, {
          one: `Pace is from ${formattedTimed} timed chapter; only the built-in reader records time.`,
          other: `Pace is from ${formattedTimed} timed chapters; only the built-in reader records time.`,
        })
      : null

  return (
    <>
      <SimpleGrid cols={{ base: 2, md: 4 }} spacing="md">
        <StatTile
          label={t`You finish`}
          value={behaviour.finishRate === null ? '-' : percent(behaviour.finishRate)}
          icon={IconChartPie}
        />
        <StatTile
          label={t`Typical chapter`}
          value={pace === null ? '-' : formatReadingTime(pace)}
          icon={IconClock}
          accent="info"
        />
        <StatTile
          label={t`You bail around`}
          value={stopPercent === null ? '-' : t`${stopPercent} in`}
          icon={IconArrowsShuffle}
          accent="warn"
        />
        <StatTile
          label={t`Biggest day`}
          value={
            biggestDayCount === null
              ? '-'
              : plural(biggestDayCount, { one: '# chapter', other: '# chapters' })
          }
          icon={IconCalendar}
          accent="ok"
        />
      </SimpleGrid>

      <Text c="dimmed" size="xs" mt={6}>
        {readSummary}{' '}
        {paceSummary ?? (
          <>
            <Trans>No chapter here carries a reading time, so there is no pace to report.</Trans>{' '}
            <Trans>Only the built-in reader records it.</Trans>
          </>
        )}
      </Text>

      <SimpleGrid cols={{ base: 1, lg: 3 }} spacing="md" mt="md">
        <BehaviourList
          icon={IconClock}
          title={t`You slow down for`}
          items={behaviour.savoured}
          emptyText={t`Not enough timed chapters yet.`}
        />
        <BehaviourList
          icon={IconSparkles}
          title={t`You tear through`}
          items={behaviour.devoured}
          emptyText={t`Not enough timed chapters yet.`}
        />
        <BehaviourList
          icon={IconArrowsShuffle}
          title={t`You put down`}
          items={behaviour.abandoned}
          emptyText={t`You finish what you start.`}
        />
      </SimpleGrid>
    </>
  )
}

function DriftSection({ insights }: { insights: TasteInsights }) {
  const { t } = useLingui()
  const data = insights.drift.map((d) => ({ bucket: d.bucket, similarity: d.similarityToStart }))

  return (
    <Card padding="md" radius="lg" withBorder>
      <Text c="dimmed" size="xs" mb="md">
        <Trans>How close each quarter sat to where you started.</Trans>{' '}
        <Trans>Falling means you moved.</Trans>
      </Text>
      <BarChart
        h={180}
        data={data}
        dataKey="bucket"
        series={[{ name: 'similarity', color: 'var(--brand)', label: t`Similarity to start` }]}
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
  const { t } = useLingui()
  if (facet.overIndexShelf === null || facet.overIndexShelf < NOTEWORTHY) return null
  const { overIndexShelf, support } = facet
  const ratioValue = ratio(overIndexShelf)
  const seriesPhrase = plural(support, { one: '# series', other: '# series' })
  const label = t`Reached for ${ratioValue} more than owning it would predict, across ${seriesPhrase}`
  return (
    <Tooltip label={label} multiline w={260}>
      <Badge variant="light" color="brand" size="sm" style={{ flexShrink: 0 }}>
        {ratioValue}
      </Badge>
    </Tooltip>
  )
}

/** The catalogue badge, which measures something different and has to say so. */
function AgainstCatalogue({ facet }: { facet: TasteFacet }) {
  const { t } = useLingui()
  if (facet.overIndexCatalogue === null || facet.overIndexCatalogue < NOTEWORTHY) return null
  const ratioValue = ratio(facet.overIndexCatalogue)
  const label = t`${ratioValue} more than the MangaBaka catalogue carries, weighted toward titles more people read`
  return (
    <Tooltip label={label} multiline w={260}>
      <Badge variant="outline" color="gray" size="sm" style={{ flexShrink: 0 }}>
        <Trans>cat {ratioValue}</Trans>
      </Badge>
    </Tooltip>
  )
}

function CompositionCard({
  title,
  facets,
  labels,
}: {
  title: string
  facets: TasteFacet[]
  /** Genre and format facet names are wire values matched against `GENRE_LABELS`/`TYPE_LABELS`; a
   * creator or tag facet has no such table and is rendered as-is. */
  labels?: Record<string, MessageDescriptor>
}) {
  const renderLabel = useLabel()
  const data = facets.slice(0, 6).map((f, i) => ({
    name: f.name,
    label: labels ? renderLabel(labels[f.name] ?? f.name) : f.name,
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
          <Trans>Nothing to show yet.</Trans>
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
                  {d.label}
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
        <Text fw={650}>
          <Trans>Creators</Trans>
        </Text>
      </Group>
      {facets.length === 0 ? (
        <Text c="dimmed" size="sm">
          <Trans>No creator shows up often enough yet.</Trans>
        </Text>
      ) : (
        <Stack gap={8}>
          {facets.slice(0, HEAD).map((f, i) => {
            const { name, support } = f
            const supportLabel = plural(support, { one: '# series', other: '# series' })
            return (
              <Group key={name} gap={8} wrap="nowrap">
                <Text c="dimmed" fw={700} size="sm" className="tnum" style={{ width: 18 }}>
                  {i + 1}
                </Text>
                <Text size="sm" truncate style={{ flex: 1, minWidth: 0 }}>
                  <Anchor component={Link} to={`/creator/${encodeURIComponent(name)}`} inherit>
                    {name}
                  </Anchor>
                </Text>
                <Text size="xs" c="dimmed" className="tnum" style={{ flexShrink: 0 }}>
                  {supportLabel}
                </Text>
              </Group>
            )
          })}
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
        <Text fw={650}>
          <Trans>Tags</Trans>
        </Text>
      </Group>
      {facets.length === 0 ? (
        <Text c="dimmed" size="sm">
          <Trans>No tags yet.</Trans>{' '}
          <Trans>They come from the catalogue, so a library the dump does not cover has none.</Trans>
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
  const { t } = useLingui()
  const [view, setView] = useState<TasteView>('read')
  const navigate = useNavigate()
  const { data: rootFolders } = useRootFolders()
  const seriesIdFor = useSeriesIdLookup()
  const [detailItem, setDetailItem] = useState<RecommendationItem | null>(null)
  const { data: insights, isLoading: insightsLoading } = useTasteInsights(view)
  const { data: behaviour, isLoading: behaviourLoading } = useReadingBehaviour()
  const { data: profile, isLoading: profileLoading, error } = useTasteProfile(view)

  const filters = useMemo(() => (profile ? buildFiltersFromProfile(profile) : {}), [profile])

  const apply = (payload = filters, seeds?: { id: number; title: string | null }[]) =>
    navigate('/discover/recommended', {
      state: { recommendationFilters: payload, seeds, source: 'taste-profile' },
    })

  if (insightsLoading && behaviourLoading && profileLoading) {
    return (
      <Center py="xl">
        <Loader />
      </Center>
    )
  }

  if (error) {
    return (
      <Alert color="red" icon={<IconAlertCircle size={16} />} title={t`Could not read your profile`}>
        {String(error)}
      </Alert>
    )
  }

  const nothingAtAll =
    (!behaviour || behaviour.chaptersRead === 0) &&
    (!profile || profile.seriesCount === 0) &&
    (!insights || insights.groups.length === 0)

  if (nothingAtAll) {
    return (
      <Stack gap="md">
      <Alert color="gray" icon={<IconAlertCircle size={16} />} title={t`Nothing to profile yet`}>
        <Trans>Read a few chapters and this fills in.</Trans>
      </Alert>
      <SignalsCard />
      </Stack>
    )
  }

  // Hoisted, or Lingui numbers it and, worse, gives it a different number in each plural branch.
  const readSeriesCount = profile?.seriesCount ?? 0
  const summaryText = profile
    ? view === 'read'
      ? plural(profile.libraryCount, {
          one: `From ${readSeriesCount} of your # series`,
          other: `From ${readSeriesCount} of your # series`,
        })
      : plural(profile.seriesCount, {
          one: 'From all # series, weighted by what you read',
          other: 'From all # series, weighted by what you read',
        })
    : ''

  return (
    <Stack gap="md">
      <Group justify="space-between" align="center" wrap="wrap">
        <SegmentedControl
          value={view}
          onChange={(v) => setView(v as TasteView)}
          data={[
            { value: 'read', label: t`What you've read` },
            { value: 'shelf', label: t`Everything you own` },
          ]}
        />
        <Text c="dimmed" size="sm">
          {summaryText}
        </Text>
      </Group>

      <SignalsCard />

      {behaviour && behaviour.chaptersRead > 0 && (
        <>
          <SectionHeader icon={IconClock} title={t`How you read`} />
          <BehaviourSection behaviour={behaviour} />
        </>
      )}

      <SectionHeader
        icon={IconCompass}
        title={t`What you read, grouped`}
        count={insights?.groups.length ? insights.groups.length : undefined}
      />
      {insights?.unavailable || insights?.groupsUnavailable ? (
        <Alert color="gray" icon={<IconAlertCircle size={16} />}>
          {insights.unavailable ?? insights.groupsUnavailable}
        </Alert>
      ) : (
        <>
          <Text c="dimmed" size="xs">
            <Trans>
              The specific things that keep coming back in your library, each with more of the same
              beside it.
            </Trans>{' '}
            <Trans>Groups overlap on purpose, so one series can belong to several of them.</Trans>
          </Text>
          <Stack gap="md">
            {insights?.groups.map((group) => (
              <GroupCard
                key={group.label}
                group={group}
                onOpen={setDetailItem}
                seriesIdFor={seriesIdFor}
              />
            ))}
          </Stack>
          {insights?.oddOneOut && (
            <Card padding="md" radius="lg" withBorder>
              <Group gap={8} wrap="nowrap">
                <IconArrowsShuffle size={16} style={{ color: 'var(--warn)', flexShrink: 0 }} />
                <Text fw={650} style={{ flexShrink: 0 }}>
                  <Trans>The odd one out</Trans>
                </Text>
                <SeriesThumb url={insights.oddOneOut.coverUrl} alt={insights.oddOneOut.title} />
                <Text size="sm" truncate style={{ flex: 1, minWidth: 0 }}>
                  <SeriesLink id={insights.oddOneOut.seriesId} title={insights.oddOneOut.title} />
                </Text>
                <Text c="dimmed" size="xs" style={{ flexShrink: 0 }}>
                  <Trans>least like anything else you read</Trans>
                </Text>
              </Group>
            </Card>
          )}
        </>
      )}

      {insights && !insights.unavailable && (
        <>
          <SectionHeader icon={IconRoute} title={t`Where your taste has moved`} />
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
            title={t`Composition`}
            action={
              <Button
                leftSection={<IconFilter size={16} />}
                variant="subtle"
                size="compact-sm"
                disabled={!hasAnyFilter(filters)}
                onClick={() => apply()}
              >
                <Trans>Recommend from this</Trans>
              </Button>
            }
          />
          <Text c="dimmed" size="xs">
            <Trans>
              The same counts the Stats page shows, kept here so the groups above have something to
              sit against.
            </Trans>
          </Text>
          {!profile.catalogueBaselineAvailable && (
            <Text c="dimmed" size="xs">
              <Trans>
                Comparisons against the wider catalogue need the embedding index.
              </Trans>{' '}
              <Trans>Until it is built, the badges compare against your own library only.</Trans>
            </Text>
          )}
          {profile.catalogueBaselineAvailable &&
            profile.catalogueBaselineSource === 'popularity' && (
              <Text c="dimmed" size="xs">
                <Trans>
                  The catalogue badges weight titles by popularity rank, standing in for how many
                  people actually read them.
                </Trans>{' '}
                <Trans>Reader counts replace it once that data is installed.</Trans>
              </Text>
            )}
          <SimpleGrid cols={{ base: 1, lg: 2 }} spacing="md">
            <CompositionCard title={t`Genres`} facets={profile.genres} labels={GENRE_LABELS} />
            <CompositionCard title={t`Formats`} facets={profile.types} labels={TYPE_LABELS} />
            <CreatorsCard facets={profile.creators} />
            <TagsCard facets={profile.tags} onExplore={(tag) => apply({ tags: [tag] })} />
          </SimpleGrid>
        </>
      )}

      <Group gap="xs" mt="xs">
        <IconLock size={14} style={{ color: 'var(--mantine-color-dimmed)' }} />
        <Text c="dimmed" size="xs">
          <Trans>Only you can see this.</Trans>{' '}
          <Trans>Reading history stays visible when a source is excluded from recommendations.</Trans>
        </Text>
      </Group>

      <DiscoverDetailModal
        item={detailItem}
        feedbackContext={{ surface: 'taste' }}
        inLibrarySeriesId={detailItem ? seriesIdFor(detailItem) : null}
        rootFolders={rootFolders}
        onClose={() => setDetailItem(null)}
      />
    </Stack>
  )
}
