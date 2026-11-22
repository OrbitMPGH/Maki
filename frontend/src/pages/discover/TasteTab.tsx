import { errorText } from '../../api/errorText'
import { useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { Group, Skeleton, Stack, Text } from '@mantine/core'
import { IconAlertCircle, IconBooks, IconClock, IconCompass, IconLock, IconRoute, IconTarget } from '../../lib/icons'
import { Trans, useLingui } from '@lingui/react/macro'
import type { RecommendationFilters, RecommendationItem, TasteGroup, TasteView } from '../../api/hooks'
import {
  useReadingBehaviour,
  useRootFolders,
  useSeriesIdLookup,
  useTasteInsights,
  useTasteProfile,
} from '../../api/hooks'
import { DiscoverDetailModal } from '../../components/discover/DiscoverDetailModal'
import { sectionSource } from '../../api/shortlist'
import { SectionHeader } from '../../components/ui/SectionHeader'
import { Panel } from '../../components/ui/Panel'
import { buildFiltersFromProfile, hasAnyFilter } from './tasteFilters'
import { SignalsCard } from './FeedbackLab'
import { Callout } from '../../components/ui/Callout'
import { TastePortrait } from './taste/TastePortrait'
import { TasteThemes } from './taste/TasteThemes'
import { StatsLink, TasteHabits } from './taste/TasteHabits'
import { TasteDrift } from './taste/TasteDrift'
import { TasteReach } from './taste/TasteReach'
import { TasteShelf } from './taste/TasteShelf'

function TasteSkeleton() {
  return (
    <Stack gap="md" aria-hidden inert>
      <Panel p="xl" className="taste-portrait">
        <div className="taste-portrait-main">
          <Skeleton h={26} w={200} />
          <Stack gap={10} my="md">
            <Skeleton h={44} w="70%" />
            <Skeleton h={44} w="55%" />
            <Skeleton h={44} w="62%" />
          </Stack>
          <Group gap={10}>
            {[110, 96, 120, 88, 104].map((w) => (
              <Skeleton key={w} h={30} w={w} radius="md" />
            ))}
          </Group>
          <Group gap="sm" mt="md">
            <Skeleton h={36} w={180} />
            <Skeleton h={36} w={150} />
          </Group>
        </div>
        <div className="taste-portrait-collage">
          {Array.from({ length: 8 }, (_, i) => (
            <Skeleton key={i} radius="var(--radius-cover)" style={{ aspectRatio: '2 / 3' }} />
          ))}
        </div>
      </Panel>
    </Stack>
  )
}

/**
 * The reader as the vectors see them, leading somewhere.
 *
 * Opens on a portrait (the reader's biggest themes, their covers, and the two ways onward), then the
 * themes themselves with what sits next to them, how the reader reads, how their taste has moved and
 * what they reach for more than a baseline predicts. The plain counts live on the Stats page; the
 * signals that steer the picks close the tab.
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

  const apply = (payload: RecommendationFilters = filters, seeds?: { id: number; title: string | null }[]) =>
    navigate('/discover/recommended', {
      state: { recommendationFilters: payload, seeds, source: 'taste-profile' },
    })

  const recommendGroup = (group: TasteGroup) =>
    apply(
      {},
      group.seedIds.map((id) => ({
        id,
        title: group.examples.find((m) => m.mangaBakaId === id)?.title ?? null,
      })),
    )

  if (insightsLoading && behaviourLoading && profileLoading) {
    return <TasteSkeleton />
  }

  if (error) {
    return (
      <Callout tone="danger" role="alert" icon={IconAlertCircle} title={t`Could not read your profile`}>
        {errorText(error)}
      </Callout>
    )
  }

  const nothingAtAll =
    (!behaviour || behaviour.chaptersRead === 0) &&
    (!profile || profile.seriesCount === 0) &&
    (!insights || insights.groups.length === 0)

  if (nothingAtAll) {
    return (
      <Stack gap="md" className="discover-tab-body">
        <Callout tone="neutral" role="alert" icon={IconAlertCircle} title={t`Nothing to profile yet`}>
          <Trans>Read a few chapters and this fills in.</Trans>
        </Callout>
        <SignalsCard />
      </Stack>
    )
  }

  const groupsNote = insights?.unavailable ?? insights?.groupsUnavailable

  return (
    <Stack gap="md" className="discover-tab-body taste-tab">
      <TastePortrait
        view={view}
        onView={setView}
        insights={insights}
        profile={profile}
        canRecommend={hasAnyFilter(filters)}
        onRecommend={() => apply()}
      />

      <section className="taste-section">
        <SectionHeader
          icon={IconCompass}
          title={t`Recurring themes`}
          count={insights?.groups.length ? insights.groups.length : undefined}
        />
        {groupsNote ? (
          <Callout tone="neutral" role="alert" icon={IconAlertCircle}>
            {groupsNote}
          </Callout>
        ) : (
          insights &&
          insights.groups.length > 0 && (
            <TasteThemes
              groups={insights.groups}
              covered={insights.covered}
              oddOneOut={insights.oddOneOut}
              sideLanes={insights.sideLanes ?? []}
              onOpen={setDetailItem}
              seriesIdFor={seriesIdFor}
              onRecommend={recommendGroup}
              onRecommendSeeds={(seeds) => apply({}, seeds)}
            />
          )
        )}
      </section>

      {insights?.shelfPicks && insights.shelfPicks.length > 0 && (
        <section className="taste-section">
          <SectionHeader icon={IconBooks} title={t`From your own shelf`} />
          <TasteShelf picks={insights.shelfPicks} />
        </section>
      )}

      {behaviour && behaviour.chaptersRead > 0 && (
        <section className="taste-section">
          <SectionHeader
            icon={IconClock}
            title={t`How you read`}
            chevron
            action={
              <StatsLink section="habits">
                <Trans>Habits in Stats</Trans>
              </StatsLink>
            }
          />
          <TasteHabits behaviour={behaviour} onFilters={(payload) => apply(payload)} />
        </section>
      )}

      {insights && !insights.unavailable && (
        <section className="taste-section">
          <SectionHeader icon={IconRoute} title={t`Where your taste has moved`} />
          {insights.driftUnavailable ? (
            <Callout tone="neutral" role="alert" icon={IconAlertCircle}>
              {insights.driftUnavailable}
            </Callout>
          ) : (
            <TasteDrift drift={insights.drift} />
          )}
        </section>
      )}

      {profile && profile.seriesCount > 0 && (
        <section className="taste-section">
          <SectionHeader
            icon={IconTarget}
            title={t`What you reach for`}
            chevron
            action={
              <StatsLink section="taste">
                <Trans>Genre and tag counts in Stats</Trans>
              </StatsLink>
            }
          />
          <TasteReach profile={profile} onFind={(payload) => apply(payload)} />
        </section>
      )}

      <section className="taste-section">
        <SignalsCard layout="feed" />
      </section>

      <Group gap="xs" mt="xs" wrap="nowrap">
        <IconLock size={16} style={{ color: 'var(--ink-3)', flexShrink: 0 }} />
        <Text c="var(--ink-3)" size="xs">
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
        savedFrom={sectionSource('taste')}
      />
    </Stack>
  )
}
