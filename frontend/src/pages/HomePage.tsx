import { Fragment, useMemo, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { Box, Button, Group, Title, VisuallyHidden } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { msg } from '@lingui/core/macro'
import {
  IconBook,
  IconBookmarks,
  IconDeviceTv,
  IconFlame,
  IconFolderDown,
  IconLayoutDashboard,
  IconPlayerPlay,
  IconPlus,
  IconSparkles,
} from '@tabler/icons-react'
import { useHomeFromAnime } from '../api/animeResume'
import { useAuth } from '../auth/AuthProvider'
import {
  HOME_HERO_DEFAULTS,
  HOME_SECTION_OPTIONS,
  useDiscover,
  useProgressSummary,
  useHomeReading,
  useHomeRecentlyAdded,
  useMetadataSettings,
  useReadingHeatmap,
  useRecommendations,
  useRootFolders,
  useSeries,
  useSeriesIdLookup,
  useUiSettings,
  isRailKey,
  railIdOf,
  type HomeLayoutKey,
  type HomeReadingItem,
  type HomeSection,
  type HomeSectionKey,
  type RecommendationItem,
} from '../api/hooks'
import { useCustomRails } from '../api/customRails'
import { AddRailButton, CustomRailSection } from '../components/rails/CustomRailSection'
import { PageLayoutEditor, PageLayoutEditorLoading } from '../components/layout/PageLayoutEditor'
import { useLayoutEditMode } from '../components/layout/useLayoutEditMode'
import { reconcileLayout, sectionVisible } from '../components/layout/pageLayout'
import { HOME_LAYOUT_CONFIG, HOME_SECTION_DEFS } from '../components/home/homeSectionDefs'
import { useReadTracking } from '../api/reader'
import { DiscoverDetailModal } from '../components/discover/DiscoverDetailModal'
import { FollowingRail } from '../components/discover/FollowingRail'
import { ContinueHero, type HeroQueue } from '../components/home/ContinueHero'
import { ContinueRail } from '../components/home/ContinueRail'
import type { ReadingRailKind } from '../components/home/ReadingCardMenu'
import { DiscoverZone, type HomeSeparation } from '../components/home/HomeZones'
import { AnimeResumeExpandModal, AnimeResumeRail } from '../components/home/AnimeResumeRail'
import { ReadingWeeks, ReadingWeeksPlaceholder, rollingWeeks } from '../components/home/ReadingWeeks'
import { RecentlyAddedRail } from '../components/home/RecentlyAddedRail'
import { DiscoverRailRow, EngineRailRow } from '../components/ui/DiscoverRail'
import { EmptyState } from '../components/ui/EmptyState'
import { PageHeader } from '../components/ui/PageHeader'
import { RailSkeleton } from '../components/ui/RailSkeleton'
import { SectionHeader, SectionTagContext } from '../components/ui/SectionHeader'
import { SurfaceFrame } from '../components/ui/SurfaceFrame'

/** How many catalogue picks each borrowed Discover rail shows before "Find more". */
const RAIL_SIZE = 20

function FindMore() {
  return (
    <Button
      component={Link}
      to="/discover"
      variant="subtle"
      size="compact-sm"
    >
      <Trans>Find more</Trans>
    </Button>
  )
}

/** Sections that only ever show series already in the library. Everything else draws on the catalogue. */
const LIBRARY_SECTIONS: HomeSectionKey[] = ['continue', 'jumpback', 'recent']

export default function HomePage({ separation = 'none' }: { separation?: HomeSeparation }) {
  const { t } = useLingui()
  const { data: series, isLoading: seriesLoading, isError: seriesFailed, refetch: refetchSeries } = useSeries()
  const { data: metadata } = useMetadataSettings()
  const { data: rootFolders } = useRootFolders()
  const { data: ui, isLoading: uiLoading, isError: uiFailed } = useUiSettings()
  const readTracking = useReadTracking()
  const { editing, enter: enterEditing, exit: exitEditing } = useLayoutEditMode()
  const { can } = useAuth()

  // Opposite default to the nav's in App.tsx on purpose: there, assuming "available" while the
  // settings load stops the Discover tab flickering in and out. Here it would fire two requests
  // that 400 on an install with no local MangaBaka database, and both surface as error toasts.
  const discoverAvailable = Boolean(metadata?.useLocalDb && metadata?.dumpPresent)
  const hasLibrary = (series?.length ?? 0) > 0

  const { data: homeRails, isError: railsFailed } = useCustomRails('home')

  // Default to the shipping order while the setting loads, so the page doesn't reflow once it
  // arrives. `on` is what every query below gates on: a section the user turned off must not
  // cost a request, which is most of the point of being able to turn one off. Nothing fetches
  // while the layout is being edited either: the editor shows compact cards, not the sections.
  const layout = useMemo(
    () =>
      ui?.homeLayout.sections ??
      (reconcileLayout([], HOME_LAYOUT_CONFIG, (homeRails ?? []).map((r) => r.id)) as HomeSection[]),
    [ui, homeRails],
  )
  const sectionOf = (key: HomeSectionKey) => layout.find((s) => s.key === key)
  // Before `ui` has loaded, `layout` is the reconciled fallback, which enables every canonical
  // section (see `reconcileLayout`). A disabled rail must not fetch just because the setting that
  // disables it has not arrived yet.
  const on = (key: HomeSectionKey) => !editing && Boolean(ui) && (sectionOf(key)?.enabled ?? false)
  const heroOn = (key: HomeSectionKey) => sectionOf(key)?.hero ?? HOME_HERO_DEFAULTS[key] ?? false
  const optionOn = (key: HomeSectionKey, option: string) =>
    sectionOf(key)?.options?.[option] ?? HOME_SECTION_OPTIONS[key]?.[option] ?? false
  const queueOf = (key: HomeSectionKey): HeroQueue => (optionOn(key, 'nextUpBeside') ? 'beside' : 'tray')
  // The glance is Continue reading's own switch, so it costs no request while that section is off.
  const glanceOn = on('continue') && optionOn('continue', 'glance')

  const needsReading = on('continue') || on('jumpback')
  const needsDiscover = discoverAvailable && hasLibrary

  const {
    data: reading,
    isLoading: readingLoading,
    isError: readingFailed,
    refetch: refetchReading,
  } = useHomeReading(undefined, needsReading)
  const { data: recent, isLoading: recentLoading } = useHomeRecentlyAdded(undefined, on('recent'))
  const { data: fromAnime, isLoading: fromAnimeLoading } = useHomeFromAnime(on('fromanime'))
  const { data: rails, isLoading: railsLoading } = useDiscover(0, needsDiscover && on('popular'))
  // An empty request object is deliberate: it hits the same server-side cache slot as Discover's
  // default Recommended tab, so this rail can never thrash that shared pool with different seeds.
  const recommendations = useRecommendations({}, needsDiscover && on('recommended'))
  // Fetched in the layout editor too, which says why the glance would be hidden.
  const { data: progress, isLoading: progressLoading } = useProgressSummary(undefined, glanceOn || editing)
  const { data: heatmap, isLoading: heatmapLoading } = useReadingHeatmap(undefined, glanceOn && Boolean(progress?.enabled))

  const seriesIdFor = useSeriesIdLookup()
  const [detailItem, setDetailItem] = useState<RecommendationItem | null>(null)
  const [fromAnimeExpanded, setFromAnimeExpanded] = useState(false)
  const navigate = useNavigate()

  const continueReading = reading?.continueReading ?? []
  const jumpBackIn = reading?.jumpBackIn ?? []

  const popular = rails?.find((r) => r.key === 'popular')?.items ?? []
  const youMightLike = recommendations.data?.pages[0]?.similar?.slice(0, RAIL_SIZE) ?? []

  // Home has no visible title row: the sidebar says where you are and the hero says what you are
  // reading. The h1 stays for screen readers. Editing the layout is a mode with its own exit, so
  // that one keeps a visible title.
  const homeTitle = (
    <VisuallyHidden>
      <Title order={1}>{t`Home`}</Title>
    </VisuallyHidden>
  )
  const editingHeader = <PageHeader title={t`Home`} description={t`Arrange what Home shows.`} />

  if (editing) {
    return (
      <SurfaceFrame width="full" pageStyle="editorial">
        {editingHeader}
        {ui && homeRails ? (
          <PageLayoutEditor
            page="home"
            placement="home"
            registry={HOME_SECTION_DEFS}
            config={HOME_LAYOUT_CONFIG}
            initial={ui.homeLayout.sections}
            rails={homeRails}
            railLimit={RAIL_SIZE}
            unavailable={(key) =>
              !discoverAvailable &&
              (key === 'recommended' || key === 'popular' || key === 'following' ||
                (isRailKey(key) && homeRails.find((r) => r.id === railIdOf(key))?.spec.source !== 'library'))
                ? msg`Needs the local MangaBaka database`
                : null
            }
            optionHint={(key, option) =>
              key === 'continue' && option === 'glance'
                ? !readTracking
                  ? msg`Needs read tracking: use the reader or connect Kavita.`
                  : progress && !progress.enabled
                    ? msg`Hidden while progression is switched off.`
                    : null
                : null
            }
            onExit={exitEditing}
          />
        ) : (
          <PageLayoutEditorLoading failed={uiFailed || railsFailed} onExit={exitEditing} />
        )}
      </SurfaceFrame>
    )
  }

  if (!seriesLoading && seriesFailed) {
    return (
      <SurfaceFrame width="full" pageStyle="editorial">
        {homeTitle}
        <EmptyState
          title={t`Couldn't load your library`}
          actionLabel={t`Retry`}
          onAction={() => void refetchSeries()}
        />
      </SurfaceFrame>
    )
  }

  if (!seriesLoading && !hasLibrary) {
    return (
      <SurfaceFrame width="full" pageStyle="editorial">
        {homeTitle}
        <EmptyState
          art="shelf"
          title={t`Nothing in your library yet`}
          description={t`Add a series and Maki will start tracking chapters for it. This page fills up as you read and download.`}
          actionLabel={can('AddSeries') ? t`Add series` : t`Request series`}
          actionTo="/add"
          actionIcon={<IconPlus size={16} />}
          secondaryActionLabel={can('ImportLibrary') ? t`Import a folder` : undefined}
          secondaryActionTo="/import"
          secondaryActionIcon={<IconFolderDown size={16} />}
        />
      </SurfaceFrame>
    )
  }

  // Keep a skeleton up rather than let the per-section fallbacks (`StartReadingPrompt` and the
  // like) flash empty while `ui` is still loading and every gated query above is held off by `on`.
  if (uiLoading) {
    return (
      <SurfaceFrame width="full" pageStyle="editorial">
        {homeTitle}
        <RailSkeleton title />
        <RailSkeleton title />
      </SurfaceFrame>
    )
  }

  const visible = layout.filter(sectionVisible)

  // The glance belongs to Continue reading: in its hero when that draws one, a strip at its slot
  // otherwise. Without read tracking every day would read as missed, so it waits for that.
  const glanceHosted = heroOn('continue') && continueReading.length > 0
  const glanceLoading = glanceOn && readTracking && (progressLoading || (progress?.enabled === true && heatmapLoading))
  // A brand-new reader would get a card of zeros, so the glance waits for the first day read.
  const glanceNode =
    glanceOn && readTracking && progress?.enabled && heatmap && rollingWeeks(heatmap).daysRead > 0 ? (
      <ReadingWeeks days={heatmap} summary={progress} hosted={glanceHosted} />
    ) : glanceLoading ? (
      <ReadingWeeksPlaceholder hosted={glanceHosted} />
    ) : null

  // One node per section key. Rendered in the user's order below; a section with nothing to show
  // yields null and takes up no space, exactly as when it is switched off.
  const sections: Record<HomeSectionKey, React.ReactNode> = {
    continue: readingLoading ? (
      <RailSkeleton />
    ) : readingFailed ? (
      <ReadingErrorPrompt onRetry={() => void refetchReading()} />
    ) : continueReading.length > 0 ? (
      <>
        {/* Without a hero to ride in, the glance is a strip above the rail. */}
        {!heroOn('continue') && glanceNode}
        {!heroOn('continue') && (
          <SectionHeader icon={IconPlayerPlay} title={t`Continue reading`} count={continueReading.length} />
        )}
        <ReadingSection
          items={continueReading}
          rail="continue"
          hero={heroOn('continue')}
          glance={heroOn('continue') ? glanceNode : undefined}
          queue={queueOf('continue')}
        />
      </>
    ) : (
      <>
        {glanceNode}
        {/* Only nudge when there is genuinely nothing to resume *and* nothing to jump back into,
            otherwise a user mid-way through their library gets told to start reading. */}
        {jumpBackIn.length === 0 && <StartReadingPrompt tracking={readTracking} />}
      </>
    ),

    recent: recentLoading ? (
      <RailSkeleton />
    ) : (
      recent && recent.length > 0 && (
        <>
          <SectionHeader icon={IconBookmarks} title={t`Recently added`} count={recent.length} />
          <RecentlyAddedRail items={recent} />
        </>
      )
    ),

    jumpback: readingLoading ? (
      <RailSkeleton />
    ) : readingFailed ? (
      on('continue') ? null : <ReadingErrorPrompt onRetry={() => void refetchReading()} />
    ) : (
      jumpBackIn.length > 0 && (
        <>
          {!heroOn('jumpback') && (
            <SectionHeader icon={IconBook} title={t`Jump back in`} count={jumpBackIn.length} />
          )}
          <ReadingSection
            items={jumpBackIn}
            rail="jumpback"
            hero={heroOn('jumpback')}
            queue={queueOf('jumpback')}
          />
        </>
      )
    ),

    fromanime: fromAnimeLoading ? (
      <RailSkeleton />
    ) : (
      fromAnime && fromAnime.items.length > 0 && (
        <>
          <SectionHeader
            icon={IconDeviceTv}
            title={t`Continue from the anime`}
            count={fromAnime.total}
            action={
              <Button variant="subtle" size="xs" onClick={() => setFromAnimeExpanded(true)}>
                <Trans>Show more</Trans>
              </Button>
            }
            chevron
          />
          <AnimeResumeRail items={fromAnime.items} onOpen={setDetailItem} />
          <AnimeResumeExpandModal
            opened={fromAnimeExpanded}
            onClose={() => setFromAnimeExpanded(false)}
            onOpen={setDetailItem}
          />
        </>
      )
    ),

    following: (
      <FollowingRail
        enabled={needsDiscover && on('following')}
        limit={RAIL_SIZE}
        seriesIdFor={seriesIdFor}
        onOpen={setDetailItem}
        onShowMore={(rail) => navigate('/discover', { state: { expandFollowing: rail } })}
      />
    ),

    recommended: recommendations.isLoading ? (
      <RailSkeleton />
    ) : (
      youMightLike.length > 0 && (
        <>
          <SectionHeader icon={IconSparkles} title={t`You might like`} action={<FindMore />} chevron />
          <EngineRailRow items={youMightLike} seriesIdFor={seriesIdFor} onOpen={setDetailItem} />
        </>
      )
    ),

    popular: railsLoading ? (
      <RailSkeleton />
    ) : (
      popular.length > 0 && (
        <>
          <SectionHeader icon={IconFlame} title={t`Currently popular`} action={<FindMore />} chevron />
          <DiscoverRailRow
            items={popular.slice(0, RAIL_SIZE)}
            seriesIdFor={seriesIdFor}
            onOpen={setDetailItem}
          />
        </>
      )
    ),
  }


  // A custom rail's key only names it; the rail itself comes from the rails list. One whose source
  // is the catalogue waits on the local database like the borrowed Discover rails above.
  const renderSection = (key: HomeLayoutKey) => {
    if (!isRailKey(key)) return sections[key]
    const rail = homeRails?.find((r) => r.id === railIdOf(key))
    if (!rail || (rail.spec.source !== 'library' && !discoverAvailable)) return null
    return <CustomRailSection rail={rail} limit={RAIL_SIZE} onOpen={setDetailItem} />
  }

  const inLibraryZone = (key: HomeLayoutKey) =>
    isRailKey(key)
      ? homeRails?.find((r) => r.id === railIdOf(key))?.spec.source === 'library'
      : LIBRARY_SECTIONS.includes(key)

  const rendered = visible.map((s) => ({ key: s.key, node: renderSection(s.key), library: inLibraryZone(s.key) }))
  const plain = (r: (typeof rendered)[number]) => <Fragment key={r.key}>{r.node}</Fragment>
  const catalogue = rendered.filter((r) => !r.library)

  const body =
    separation === 'tagged' ? (
      rendered.map((r) =>
        r.library ? (
          plain(r)
        ) : (
          <SectionTagContext.Provider key={r.key} value={t`Not in your library`}>
            {r.node}
          </SectionTagContext.Provider>
        ),
      )
    ) : separation === 'break' || separation === 'well' ? (
      <>
        {rendered.filter((r) => r.library).map(plain)}
        {catalogue.some((r) => r.node) && (
          <DiscoverZone variant={separation} discoverLink={discoverAvailable}>
            {catalogue.map(plain)}
          </DiscoverZone>
        )}
      </>
    ) : (
      rendered.map(plain)
    )

  return (
    <SurfaceFrame width="full" pageStyle="editorial">
      {homeTitle}
      {visible.length === 0 ? (
        <EmptyState
          title={t`Every section is switched off`}
          description={t`Home has nothing to show. Turn some sections back on in the layout editor, or switch Home off entirely in Settings.`}
          actionLabel={t`Edit layout`}
          onAction={enterEditing}
        />
      ) : (
        body
      )}

      <Group justify="center" gap="xs" mt="xl">
        <AddRailButton placement="home" />
        <Button
          variant="subtle"
          color="gray"
          leftSection={<IconLayoutDashboard size={16} />}
          onClick={enterEditing}
        >
          <Trans>Edit layout</Trans>
        </Button>
      </Group>

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

/** A reading list as a hero with a Next up strip under it, or as a plain rail. */
function ReadingSection({
  items,
  rail,
  hero,
  glance,
  queue,
}: {
  items: HomeReadingItem[]
  rail: ReadingRailKind
  hero: boolean
  glance?: React.ReactNode
  queue: HeroQueue
}) {
  return hero ? (
    <ContinueHero items={items} rail={rail} glance={glance} queue={queue} />
  ) : (
    <ContinueRail items={items} rail={rail} />
  )
}

/** Shown in place of the reading rails when `/home/reading` itself failed, rather than the start-reading nudge. */
function ReadingErrorPrompt({ onRetry }: { onRetry: () => void }) {
  const { t } = useLingui()
  return (
    <Box mt="xl">
      <EmptyState title={t`Couldn't load your reading progress`} actionLabel={t`Retry`} onAction={onRetry} />
    </Box>
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
