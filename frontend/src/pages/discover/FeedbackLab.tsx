import { errorText } from '../../api/errorText'
import { useMemo, useState } from 'react'
import { randomUUID } from '../../lib/uuid'
import {
  ActionIcon, Alert, Badge, Button, Card, Collapse, Group, Loader, Paper, Stack, Text, Tooltip,
} from '@mantine/core'
import {
  IconAdjustmentsHorizontal, IconArrowBackUp, IconChevronRight, IconCompass, IconHelpCircle, IconThumbDown, IconThumbUp,
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
import { MetaList } from '../../components/ui/MetaList'
import { PanelTitle } from '../../components/ui/PanelTitle'

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
  const loadError = error ? errorText(error) : ''
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

  return (
    <>
      <SectionHeader
        icon={IconCompass}
        title={t`What shapes your recommendations`}
        action={
          <Group gap="xs" wrap="nowrap">
            <Button visibleFrom="xs" variant="default" size="xs" aria-expanded={howItWorks} onClick={() => setHowItWorks((v) => !v)}>
              <Trans>How signals work</Trans>
            </Button>
            <Button visibleFrom="xs" variant="default" size="xs" onClick={() => setManage('titles')}>
              <Trans>Manage signals</Trans>
            </Button>
            {/* A phone keeps both on the title row as icons; as words they dropped to a line of their own. */}
            <ActionIcon
              hiddenFrom="xs"
              variant="default"
              size="lg"
              aria-label={t`How signals work`}
              aria-expanded={howItWorks}
              onClick={() => setHowItWorks((v) => !v)}
            >
              <IconHelpCircle size={18} />
            </ActionIcon>
            <ActionIcon
              hiddenFrom="xs"
              variant="default"
              size="lg"
              aria-label={t`Manage signals`}
              onClick={() => setManage('titles')}
            >
              <IconAdjustmentsHorizontal size={18} />
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
          <Paper withBorder radius="md" p="md">
            <Stack gap="sm">
              <Text size="sm" fw={600}>
                <Trans>How signals work</Trans>
              </Text>
              <Text size="xs" c="var(--ink-3)">
                <Trans>
                  <b>Thumbs up</b> counts toward your taste. Similar titles rank higher.
                </Trans>
              </Text>
              <Text size="xs" c="var(--ink-3)">
                <Trans>
                  <b>Thumbs down</b> and ratings of 4 or under push down titles that are
                  closer to what you rejected than to what you kept. What they share shows up
                  above once three or more agree.
                </Trans>
              </Text>
              <Text size="xs" c="var(--ink-3)">
                <Trans>
                  <b>Hide and dismiss</b> only affect that one title.
                </Trans>
              </Text>
              <Text size="xs" c="var(--ink-3)">
                <Trans>
                  <b>Seen elsewhere</b> stops a title being recommended without changing taste.
                </Trans>
              </Text>
              <Text size="xs" c="var(--ink-3)">
                <Trans>
                  <b>Excluded from taste</b> keeps a shelf title out of the ranking. Reading
                  history is untouched.
                </Trans>
              </Text>
            </Stack>
          </Paper>
        </Collapse>

        {isLoading && <Loader size="sm" />}
        {error && (
          <Alert color="var(--danger)">
            <Trans>Could not load your signals: {loadError}</Trans>
          </Alert>
        )}
        {actionError && (
          <Alert color="var(--danger)">
            <Trans>{actionError} Refresh the page and try again.</Trans>
          </Alert>
        )}

        {lab && summary && (
          <>
            <FigureStrip
              className="taste-figures"
              figures={[
                {
                  label: t`Titles on the shelf`,
                  value: summary.visibleShelf,
                  hint:
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
              <Text size="xs" c="var(--ink-3)">
                <Trans>
                  Catalogue fallback is active. Personal add weights need semantic ranking.
                </Trans>
              </Text>
            )}
            {lab.rankingMode === 'semantic' && !lab.capabilities.personalAddWeighting && (
              <Text size="xs" c="var(--ink-3)">
                <Trans>Personal add weighting is disabled for this instance.</Trans>
              </Text>
            )}

            <Card withBorder radius="lg" padding="md">
              <Group justify="space-between" align="center" mb={4}>
                <PanelTitle>
                  <Trans>Recent feedback</Trans>
                </PanelTitle>
                <Button
                  variant="subtle"
                  size="xs"
                  rightSection={<IconChevronRight size={16} />}
                  onClick={() => setManage('titles')}
                >
                  <Trans>Show more</Trans>
                </Button>
              </Group>
              {recent.length === 0 && padded.length === 0 && (
                <Text size="sm" c="var(--ink-3)">
                  <Trans>
                    Thumbs, hide or dismiss a recommendation and it shows up here.
                  </Trans>
                </Text>
              )}
              <Stack gap={2}>
                {recent.map(([item, ...earlier], index) => (
                  <div key={item.id}>
                    {dayOf(item) !== dayOf(recent[index - 1]?.[0]) && (
                      <Text size="xs" fw={600} c="var(--ink-3)" mt={index === 0 ? 0 : 10}>
                        <DayLabel value={item.occurredAtUtc} />
                      </Text>
                    )}
                    <Group gap="sm" wrap="nowrap" py={6}>
                      <SeriesThumb url={item.coverUrl} alt={item.title ?? ''} />
                      <div style={{ flex: 1, minWidth: 0 }}>
                        <Text size="sm" fw={500} truncate>
                          {item.title ?? untitled(item.mangaBakaId)}
                        </Text>
                        {[item, ...earlier].map((event) => (
                          // Wraps rather than truncating: on a phone the pill's date was cut mid-way.
                          <Group key={event.id} gap={6} wrap="wrap" mt={2}>
                            <ActionPill item={event} />
                            {/* The list form, not inline: a wrapped time takes its separator to the
                                line start, where the list clips it, instead of leaving it alone. */}
                            <Text component="div" size="xs" c="var(--ink-3)">
                              <MetaList items={[phrase(event), formatTime(event.occurredAtUtc)]} />
                            </Text>
                          </Group>
                        ))}
                      </div>
                      {newestPerTitle.has(item.id) && UNDOABLE.includes(item.action) && !dismissalEnded(item) && (
                        <Button
                          size="xs" variant="default"
                          leftSection={<IconArrowBackUp size={16} />}
                          style={{ flexShrink: 0 }}
                          loading={undo.isPending && undo.variables?.eventId === item.id}
                          onClick={() => void undoItem(item)}
                        >
                          <Trans>Undo</Trans>
                        </Button>
                      )}
                    </Group>
                  </div>
                ))}
                {padded.length > 0 && (
                  <Text size="xs" fw={600} c="var(--ink-3)" mt={recent.length === 0 ? 0 : 10}>
                    <Trans>From your shelf</Trans>
                  </Text>
                )}
                {padded.map((item) => (
                  <Group key={item.mangaBakaId} gap="sm" wrap="nowrap" py={6}>
                    <SeriesThumb url={item.coverUrl} alt={item.title ?? ''} />
                    <div style={{ flex: 1, minWidth: 0 }}>
                      <Text size="sm" fw={500} truncate>
                        {item.title ?? untitled(item.mangaBakaId)}
                      </Text>
                      <Group gap={6} wrap="nowrap" mt={2}>
                        <ShelfPill item={item} />
                        <ShelfPhrase item={item} />
                      </Group>
                    </div>
                  </Group>
                ))}
              </Stack>
            </Card>

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

/** The coloured badge for one feedback action. Covers every action the service writes. */
function ActionPill({ item }: { item: FeedbackActivity }) {
  const { t } = useLingui()
  const [label, color] = ((): [string, string] => {
    switch (item.action) {
      case 'like': return [t`Liked`, 'var(--ok)']
      case 'dislike': return [t`Disliked`, 'var(--danger)']
      case 'hide': return [t`Hidden`, 'var(--neutral)']
      case 'dismiss': {
        const until = item.dismissedUntilUtc ? formatRecentDate(item.dismissedUntilUtc) : ''
        if (dismissalEnded(item)) return [t`Dismissed`, 'var(--neutral)']
        return [until ? t`Dismissed until ${until}` : t`Dismissed`, 'var(--neutral)']
      }
      case 'mark-exposed': return [t`Seen elsewhere`, 'var(--info)']
      case 'clear-suppression': return [t`Restored`, 'var(--neutral)']
      case 'clear-exposure': return [t`Seen cleared`, 'var(--neutral)']
      case 'clear-sentiment': return [t`Rating cleared`, 'var(--neutral)']
      case 'undo': return [t`Undone`, 'var(--neutral)']
      default: return [item.action.replaceAll('-', ' '), 'var(--neutral)']
    }
  })()
  const icon =
    item.action === 'like' ? <IconThumbUp size={12} /> : item.action === 'dislike' ? <IconThumbDown size={12} /> : undefined
  return (
    <Badge size="sm" variant="light" color={color} leftSection={icon} style={{ flexShrink: 0 }}>
      {label}
    </Badge>
  )
}

type ShelfPadItem = FeedbackLabData['sources'][number] & { kind: 'rated' | 'read' | 'added' }

function formatRating(rating: number): string {
  return formatDecimal(rating)
}

/** Pill + tertiary label for a shelf-derived row padding the recent-feedback card. */
function useShelfPillInfo(item: ShelfPadItem) {
  const { t } = useLingui()
  if (item.kind === 'rated' && item.rating != null) {
    const label = t`★ ${formatRating(item.rating)} rated`
    if (item.rating > 5) return { label, color: 'var(--ok)', phrase: t`counts toward your taste` }
    if (item.rating <= AVOID_RATING_CEILING) {
      return { label, color: 'var(--danger)', phrase: t`pushes down titles close to it` }
    }
    return { label, color: 'var(--neutral)', phrase: t`neutral, seeds at shelf weight` }
  }
  if (item.kind === 'read') return { label: t`Read`, color: 'var(--brand)', phrase: t`weighted by how far you got` }
  return { label: t`Added by you`, color: 'var(--brand)', phrase: t`counts a little more than the rest` }
}

function ShelfPill({ item }: { item: ShelfPadItem }) {
  const { label, color } = useShelfPillInfo(item)
  return <Badge size="sm" variant="light" color={color} style={{ flexShrink: 0 }}>{label}</Badge>
}

function ShelfPhrase({ item }: { item: ShelfPadItem }) {
  const { phrase } = useShelfPillInfo(item)
  return <Text size="xs" c="var(--ink-3)" truncate>{phrase}</Text>
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
