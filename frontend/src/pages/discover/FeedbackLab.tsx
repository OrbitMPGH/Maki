import { errorText } from '../../api/errorText'
import { useMemo, useState } from 'react'
import { randomUUID } from '../../lib/uuid'
import {
  ActionIcon, Alert, Badge, Button, Collapse, Group, Skeleton, Stack, Text, Tooltip,
} from '@mantine/core'
import {
  IconAdjustmentsHorizontal, IconArrowBackUp, IconBook, IconBookmark, IconChevronRight, IconClockPause, IconCompass,
  IconEyeCheck, IconEyeOff, IconInfoCircle, IconRestore, IconStarFilled, IconThumbDown, IconThumbUp,
} from '../../lib/icons'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import type { AvoidanceLabel, FeedbackActivity, FeedbackLabData } from '../../api/recommendationFeedback'
import { useFeedbackLab, useUndoFeedback } from '../../api/recommendationFeedback'
import { DiscoverTasteStrip } from '../../components/discover/DiscoverTasteStrip'
import { SectionHeader } from '../../components/ui/SectionHeader'
import { FigureStrip } from '../../components/ui/FigureStrip'
import { SeriesThumb } from '../stats/SeriesLink'
import { formatDate, formatDecimal, formatRecentDate, formatTime } from '../../format'
import { ManageSignalsModal } from './ManageSignalsModal'
import { AnimeSignalsSection } from './AnimeSignalsSection'
import { PanelTitle } from '../../components/ui/PanelTitle'
import { Panel } from '../../components/ui/Panel'
import { EmptyState } from '../../components/ui/EmptyState'
import { ErrorMessage } from '../../components/ErrorMessage'
import { SignalStatus, type SignalStatusInfo } from './SignalStatus'

/** Actions the undo endpoint can reverse: it replays the event's stored previous state. */
const UNDOABLE = ['hide', 'dismiss', 'mark-exposed', 'clear-suppression', 'clear-exposure']

/**
 * Mirrors `RecommendationFeedbackPolicy.AvoidRatingCeiling`: a rating at or below this pushes down
 * titles close to it, the same as a thumbs down. 5 stays neutral. Keep the "How signals work" copy
 * above and {@link useShelfPillInfo} reading the same number.
 */
const AVOID_RATING_CEILING = 4

/**
 * What the reader's own actions did to their recommendations, on the Taste tab.
 *
 * Summary and recent feedback only. Anything that changes one title is in the Manage signals modal,
 * because the two answer different questions and mixing them made the panel a control surface
 * nobody read.
 */
export function SignalsCard() {
  const { t } = useLingui()
  const phrase = usePhrase()
  const untitled = (id: number) => t`Catalogue title ${id}`
  const { data: lab, isLoading, error } = useFeedbackLab()
  const undo = useUndoFeedback()
  const [manage, setManage] = useState<'titles' | 'anime' | null>(null)
  // The modal fetches its own data on mount, so it is only mounted once someone has opened it.
  const [manageOpened, setManageOpened] = useState(false)
  if (manage !== null && !manageOpened) setManageOpened(true)
  const [actionError, setActionError] = useState('')
  const [howItWorks, setHowItWorks] = useState(false)

  // Back-to-back events for one title on one day share a row, so "dismissed, then liked" reads as
  // one story instead of the same title twice.
  const recent = useMemo(() => {
    const groups: FeedbackActivity[][] = []
    for (const item of lab?.activity.items ?? []) {
      const last = groups.at(-1)
      if (last && last[0].mangaBakaId === item.mangaBakaId && dayOf(last[0]) === dayOf(item)) last.push(item)
      else if (groups.length < 5) groups.push([item])
      else break
    }
    return groups
  }, [lab])
  // A reader who has never thumbed anything still has a shelf: pad the card with those signals
  // so it isn't empty, ranked rated > read > added and never duplicating a real feedback event.
  const padded = useMemo(() => {
    const need = 5 - recent.length
    if (need <= 0) return []
    const seen = new Set(recent.map((group) => group[0].mangaBakaId))
    const sources = lab?.sources ?? []
    const chosen: (typeof sources[number] & { kind: 'rated' | 'read' | 'added' })[] = []

    const rated = sources
      .filter((s) => !s.excluded && s.rating != null && !seen.has(s.mangaBakaId))
      .sort((a, b) => (b.rating ?? 0) - (a.rating ?? 0))
    for (const s of rated) {
      if (chosen.length >= need) break
      chosen.push({ ...s, kind: 'rated' })
      seen.add(s.mangaBakaId)
    }

    const read = sources.filter((s) => !s.excluded && s.isRead && !seen.has(s.mangaBakaId))
    for (const s of read) {
      if (chosen.length >= need) break
      chosen.push({ ...s, kind: 'read' })
      seen.add(s.mangaBakaId)
    }

    const added = sources
      .filter((s) => !s.excluded && s.addedAtUtc != null && !seen.has(s.mangaBakaId))
      .sort((a, b) => new Date(b.addedAtUtc ?? 0).getTime() - new Date(a.addedAtUtc ?? 0).getTime())
    for (const s of added) {
      if (chosen.length >= need) break
      chosen.push({ ...s, kind: 'added' })
      seen.add(s.mangaBakaId)
    }

    return chosen
  }, [lab, recent])
  // Only the newest event for a title can be undone; the endpoint rejects a stale revision anyway,
  // so showing the button on an older row would only ever produce an error.
  const newestPerTitle = useMemo(() => {
    const seen = new Set<number>()
    const ids = new Set<number>()
    for (const item of lab?.activity.items ?? []) {
      if (seen.has(item.mangaBakaId)) continue
      seen.add(item.mangaBakaId)
      ids.add(item.id)
    }
    return ids
  }, [lab])

  if (lab && !lab.capabilities.labUi) return null

  const summary = lab?.summary
  const suppressed = (summary?.hidden ?? 0) + (summary?.dismissed ?? 0)
  // Hoisted: Lingui only names a placeholder after a plain identifier, so a member expression
  // inline would extract as {0} and tell a translator nothing.
  const excluded = summary?.excluded ?? 0
  const rated = summary?.ratedSources ?? 0
  const adds = summary?.personalAdds ?? 0
  const incognito = summary?.incognito ?? 0
  const exposed = summary?.exposed ?? 0
  const pushingDown = summary?.pushingDown ?? 0

  async function undoItem(item: FeedbackActivity) {
    setActionError('')
    try {
      await undo.mutateAsync({
        eventId: item.id,
        expectedRevision: item.stateRevision,
        clientMutationId: randomUUID(),
      })
    } catch (cause) {
      setActionError(errorText(cause))
    }
  }

  const statusOf = useEventStatus()
  const shelfInfo = useShelfInfo()

  return (
    <>
      <SectionHeader
        icon={IconCompass}
        title={t`What shapes your recommendations`}
        chevron
        action={
          <Group gap={4} wrap="nowrap">
            <ActionIcon
              variant="subtle"
              color="var(--ink-3)"
              aria-label={t`How signals work`}
              aria-expanded={howItWorks}
              onClick={() => setHowItWorks((v) => !v)}
            >
              <IconInfoCircle size={16} />
            </ActionIcon>
            <Button visibleFrom="xs" variant="subtle" size="compact-sm" onClick={() => setManage('titles')}>
              <Trans>Manage signals</Trans>
            </Button>
            <ActionIcon
              hiddenFrom="xs"
              variant="subtle"
              color="var(--ink-3)"
              aria-label={t`Manage signals`}
              onClick={() => setManage('titles')}
            >
              <IconAdjustmentsHorizontal size={16} />
            </ActionIcon>
          </Group>
        }
      />
      <Stack gap="md">
        <Text size="sm" c="var(--ink-3)">
          <Trans>
            Your shelf, ratings, reading and thumbs decide what gets recommended. Hide, dismiss and
            seen elsewhere only take that one title out.
          </Trans>
        </Text>
        <DiscoverTasteStrip />

        <Collapse expanded={howItWorks}>
          <Panel p="md">
            <Stack gap="sm">
              <PanelTitle icon={IconInfoCircle}>
                <Trans>How signals work</Trans>
              </PanelTitle>
              <Text size="sm" c="var(--ink-3)">
                <Trans>
                  <b>Thumbs up</b> counts toward your taste. Similar titles rank higher.
                </Trans>
              </Text>
              <Text size="sm" c="var(--ink-3)">
                <Trans>
                  <b>Thumbs down</b> and ratings of 4 or under push down titles that are
                  closer to what you rejected than to what you kept. What they share shows up
                  above once three or more agree.
                </Trans>
              </Text>
              <Text size="sm" c="var(--ink-3)">
                <Trans>
                  <b>Hide and dismiss</b> only affect that one title.
                </Trans>
              </Text>
              <Text size="sm" c="var(--ink-3)">
                <Trans>
                  <b>Seen elsewhere</b> stops a title being recommended without changing taste.
                </Trans>
              </Text>
              <Text size="sm" c="var(--ink-3)">
                <Trans>
                  <b>Excluded from taste</b> keeps a shelf title out of the ranking. Reading
                  history is untouched.
                </Trans>
              </Text>
            </Stack>
          </Panel>
        </Collapse>

        {error && (
          <Alert color="var(--danger)" variant="light" title={t`Could not load your signals`}>
            <ErrorMessage error={error} />
          </Alert>
        )}
        {actionError && (
          <Alert color="var(--danger)" variant="light">
            <Trans>{actionError} Refresh the page and try again.</Trans>
          </Alert>
        )}

        {isLoading && <SignalsSkeleton />}

        {lab && summary && (
          <>
            <FigureStrip
              className="taste-figures"
              figures={[
                {
                  label: t`Titles on the shelf`,
                  value: summary.visibleShelf,
                  hint: t`${adds} added by you`,
                  tip:
                    incognito > 0
                      ? t`${adds} added by you, ${excluded} excluded from taste, ${incognito} incognito not counted`
                      : t`${adds} added by you, ${excluded} excluded from taste`,
                },
                { label: t`Read`, value: summary.readSources, hint: t`${rated} rated` },
                {
                  label: t`Thumbs up / down`,
                  hint: t`${pushingDown} pushing similar titles down`,
                  zero: summary.liked + summary.disliked === 0,
                  value: (
                    <>
                      <Text span inherit c="var(--ok)" className="taste-thumb">
                        <IconThumbUp size="0.85em" aria-hidden />
                        {summary.liked}
                      </Text>
                      <Text span inherit c="var(--ink-3)" mx={8}>/</Text>
                      <Text span inherit c="var(--danger)" className="taste-thumb">
                        <IconThumbDown size="0.85em" aria-hidden />
                        {summary.disliked}
                      </Text>
                    </>
                  ),
                },
                { label: t`Hidden or dismissed`, value: suppressed, hint: t`${exposed} read or watched elsewhere` },
              ]}
            />

            {lab.avoids.length > 0 && <AvoidRow avoids={lab.avoids} />}

            {lab.rankingMode === 'fallback' && (
              <Text size="sm" c="var(--ink-3)">
                <Trans>
                  Catalogue fallback is active. Personal add weights need semantic ranking.
                </Trans>
              </Text>
            )}
            {lab.rankingMode === 'semantic' && !lab.capabilities.personalAddWeighting && (
              <Text size="sm" c="var(--ink-3)">
                <Trans>Personal add weighting is disabled for this instance.</Trans>
              </Text>
            )}

            <Panel className="signals-feed">
              <Group justify="space-between" align="center" wrap="nowrap" className="signals-feed-head">
                <PanelTitle>
                  <Trans>Recent feedback</Trans>
                </PanelTitle>
                <Button
                  variant="subtle"
                  size="compact-sm"
                  color="var(--ink-2)"
                  rightSection={<IconChevronRight size={16} />}
                  onClick={() => setManage('titles')}
                >
                  <Trans>Show more</Trans>
                </Button>
              </Group>
              {recent.length === 0 && padded.length === 0 && (
                <EmptyState
                  compact
                  mood="asleep"
                  title={t`No feedback yet`}
                  description={t`Thumbs, hide or dismiss a recommendation and it shows up here.`}
                />
              )}
              {recent.map(([item, ...earlier], index) => (
                <div key={item.id} className="signals-feed-group">
                  {dayOf(item) !== dayOf(recent[index - 1]?.[0]) && (
                    <Text className="signals-feed-day">
                      <DayLabel value={item.occurredAtUtc} />
                    </Text>
                  )}
                  <div className="signals-feed-row">
                    <SeriesThumb url={item.coverUrl} alt="" large />
                    <div className="signals-feed-main">
                      <Text className="signals-feed-title" truncate>
                        {item.title ?? untitled(item.mangaBakaId)}
                      </Text>
                      {[item, ...earlier].map((event) => (
                        <div key={event.id} className="signals-feed-line">
                          <SignalStatus {...statusOf(event)} />
                          <span className="signals-feed-phrase">{phrase(event)}</span>
                          <span className="signals-feed-time tnum">{formatTime(event.occurredAtUtc)}</span>
                        </div>
                      ))}
                    </div>
                    {newestPerTitle.has(item.id) && UNDOABLE.includes(item.action) && !dismissalEnded(item) && (
                      <Button
                        size="compact-sm" variant="subtle" color="var(--ink-2)"
                        leftSection={<IconArrowBackUp size={16} />}
                        className="signals-feed-undo"
                        loading={undo.isPending && undo.variables?.eventId === item.id}
                        onClick={() => void undoItem(item)}
                      >
                        <Trans>Undo</Trans>
                      </Button>
                    )}
                  </div>
                </div>
              ))}
              {padded.length > 0 && (
                <div className="signals-feed-group">
                  <Text className="signals-feed-day">
                    <Trans>From your shelf</Trans>
                  </Text>
                  {padded.map((item) => {
                    const info = shelfInfo(item)
                    return (
                      <div key={item.mangaBakaId} className="signals-feed-row">
                        <SeriesThumb url={item.coverUrl} alt="" large />
                        <div className="signals-feed-main">
                          <Text className="signals-feed-title" truncate>
                            {item.title ?? untitled(item.mangaBakaId)}
                          </Text>
                          <div className="signals-feed-line">
                            <SignalStatus {...info.status} />
                            <span className="signals-feed-phrase">{info.phrase}</span>
                          </div>
                        </div>
                      </div>
                    )
                  })}
                </div>
              )}
            </Panel>

            {lab.capabilities.animeSignals && (
              <AnimeSignalsSection onOpenList={() => setManage('anime')} />
            )}
          </>
        )}
      </Stack>
      {manageOpened && (
        <ManageSignalsModal
          opened={manage !== null} initialTab={manage ?? 'titles'} onClose={() => setManage(null)}
        />
      )}
    </>
  )
}

/** The figures and feed with their labels in place, numbers and rows blanked until the lab answers. */
function SignalsSkeleton() {
  const { t } = useLingui()
  return (
    <>
      <FigureStrip
        className="taste-figures"
        loading
        figures={[
          { label: t`Titles on the shelf`, value: 0 },
          { label: t`Read`, value: 0 },
          { label: t`Thumbs up / down`, value: 0 },
          { label: t`Hidden or dismissed`, value: 0 },
        ]}
      />
      <Panel className="signals-feed" aria-hidden>
        <PanelTitle>
          <Trans>Recent feedback</Trans>
        </PanelTitle>
        {Array.from({ length: 4 }, (_, i) => (
          <div key={i} className="signals-feed-row">
            <Skeleton className="stats-thumb stats-thumb-lg" />
            <div className="signals-feed-main">
              <Skeleton h={14} w={`${50 + (i % 3) * 12}%`} />
              <Skeleton h={12} w="40%" mt={8} />
            </div>
          </div>
        ))}
      </Panel>
    </>
  )
}

/**
 * What the titles a reader keeps out of their taste have in common, when enough of them agree.
 *
 * An observation about the profile, not a claim about the ranking: nothing here is fed back into
 * scoring. A chip appears at three supporting titles, which is the threshold that stops one thumbs
 * down claiming a genre, and only when the facet covers far more of the pushed-down set than of the
 * shelf. The tooltip shows both counts so the claim can be checked rather than taken.
 */
function AvoidRow({ avoids }: { avoids: AvoidanceLabel[] }) {
  return (
    <Group gap={6} wrap="wrap" align="center">
      <Text size="sm" fw={600}>
        <Trans>You seem to avoid</Trans>
      </Text>
      {avoids.map((avoid) => {
        const titles = avoid.examples.map((example) => example.title).join(', ')
        const count = avoid.support
        const { support, positiveSupport, shelfCount } = avoid
        const pushedDown = Math.round(support / Math.max(avoid.share, 1e-6))
        return (
          <Tooltip
            key={`${avoid.kind}:${avoid.label}`}
            label={
              <>
                <Text size="xs">
                  <Trans>
                    {support} of {pushedDown} pushed down, {positiveSupport} of {shelfCount} on your
                    shelf
                  </Trans>
                </Text>
                <Text size="xs">{titles}</Text>
              </>
            }
            withArrow
            multiline
            w={260}
          >
            <Badge size="sm" variant="light" color="var(--danger)">
              {avoid.label} (<Plural value={count} one="# title" other="# titles" />)
            </Badge>
          </Tooltip>
        )
      })}
    </Group>
  )
}

/** A dismissal past its date: the title is back in the running, so the row must not read as active. */
function dismissalEnded(item: FeedbackActivity): boolean {
  return item.action === 'dismiss' && item.dismissedUntilUtc != null && new Date(item.dismissedUntilUtc).getTime() <= Date.now()
}

function dayOf(item?: FeedbackActivity): string {
  return item ? new Date(item.occurredAtUtc).toDateString() : ''
}

function DayLabel({ value }: { value: string }) {
  const day = new Date(value).toDateString()
  const today = new Date()
  const yesterday = new Date(today.getTime() - 86_400_000)
  if (day === today.toDateString()) return <Trans>Today</Trans>
  if (day === yesterday.toDateString()) return <Trans>Yesterday</Trans>
  return <>{formatDate(value)}</>
}

/** Icon, word and colour for one feedback action. Covers every action the service writes. */
function useEventStatus() {
  const { t } = useLingui()
  return (item: FeedbackActivity): SignalStatusInfo => {
    const quiet = 'var(--ink-2)'
    switch (item.action) {
      case 'like': return { icon: IconThumbUp, label: t`Liked`, color: 'var(--ok)' }
      case 'dislike': return { icon: IconThumbDown, label: t`Disliked`, color: 'var(--danger)' }
      case 'hide': return { icon: IconEyeOff, label: t`Hidden`, color: quiet }
      case 'dismiss': {
        const until = item.dismissedUntilUtc ? formatRecentDate(item.dismissedUntilUtc) : ''
        const label = dismissalEnded(item) || !until ? t`Dismissed` : t`Dismissed until ${until}`
        return { icon: IconClockPause, label, color: quiet }
      }
      case 'mark-exposed': return { icon: IconEyeCheck, label: t`Seen elsewhere`, color: 'var(--info)' }
      case 'clear-suppression': return { icon: IconRestore, label: t`Restored`, color: quiet }
      case 'clear-exposure': return { icon: IconRestore, label: t`Seen cleared`, color: quiet }
      case 'clear-sentiment': return { icon: IconRestore, label: t`Rating cleared`, color: quiet }
      case 'undo': return { icon: IconArrowBackUp, label: t`Undone`, color: quiet }
      default: return { icon: IconInfoCircle, label: item.action.replaceAll('-', ' '), color: quiet }
    }
  }
}

type ShelfPadItem = FeedbackLabData['sources'][number] & { kind: 'rated' | 'read' | 'added' }

/** Status and phrase for a shelf-derived row padding the recent-feedback card. */
function useShelfInfo() {
  const { t } = useLingui()
  return (item: ShelfPadItem): { status: SignalStatusInfo; phrase: string } => {
    if (item.kind === 'rated' && item.rating != null) {
      const score = formatDecimal(item.rating)
      const status = { icon: IconStarFilled, label: t`Rated ${score}`, color: 'var(--ink-2)', iconColor: 'var(--rating)' }
      if (item.rating > 5) return { status, phrase: t`counts toward your taste` }
      if (item.rating <= AVOID_RATING_CEILING) return { status, phrase: t`pushes down titles close to it` }
      return { status, phrase: t`neutral, seeds at shelf weight` }
    }
    if (item.kind === 'read') {
      return { status: { icon: IconBook, label: t`Read`, color: 'var(--brand-fg)' }, phrase: t`weighted by how far you got` }
    }
    return {
      status: { icon: IconBookmark, label: t`Added by you`, color: 'var(--brand-fg)' },
      phrase: t`counts a little more than the rest`,
    }
  }
}

/** The short "what it did" phrase beside an action's badge. */
function usePhrase() {
  const { t } = useLingui()
  return (item: FeedbackActivity): string => {
    if (dismissalEnded(item)) {
      const until = formatRecentDate(item.dismissedUntilUtc!)
      // Reads on from the "Dismissed" pill; the row's own date heading is when it was dismissed.
      return t`until ${until}, now back in recommendations`
    }
    switch (item.action) {
      case 'like': return t`counts toward your taste`
      case 'dislike': return t`no longer steers your taste`
      case 'hide':
      case 'dismiss': return t`this title only`
      case 'mark-exposed': return t`won't be recommended again`
      case 'clear-suppression': return t`back in the running`
      default: return ''
    }
  }
}
