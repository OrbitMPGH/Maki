import { useMemo, useState } from 'react'
import {
  Alert, Badge, Button, Card, Collapse, Group, Loader, Paper, SimpleGrid, Stack, Text, Tooltip,
} from '@mantine/core'
import {
  IconBooks, IconEyeOff, IconThumbUp, IconBook, IconSparkles,
} from '@tabler/icons-react'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import type { AvoidanceLabel, FeedbackActivity, FeedbackLabData } from '../../api/recommendationFeedback'
import { useFeedbackLab, useUndoFeedback } from '../../api/recommendationFeedback'
import { SectionHeader } from '../../components/ui/SectionHeader'
import { StatTile } from '../../components/ui/StatTile'
import { SeriesThumb } from '../stats/SeriesLink'
import { formatDate, formatTime } from '../../format'
import { ManageSignalsModal } from './ManageSignalsModal'
import { AnimeSignalsSection } from './AnimeSignalsSection'

/** Actions the undo endpoint can reverse: it replays the event's stored previous state. */
const UNDOABLE = ['hide', 'dismiss', 'mark-exposed', 'clear-suppression', 'clear-exposure']

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
  const loadError = error ? String(error) : ''
  const [manage, setManage] = useState<'titles' | 'anime' | null>(null)
  const [actionError, setActionError] = useState('')
  const [howItWorks, setHowItWorks] = useState(false)

  const recent = useMemo(() => (lab?.activity.items ?? []).slice(0, 5), [lab])
  // A reader who has never thumbed anything still has a shelf: pad the card with those signals
  // so it isn't empty, ranked rated > read > added and never duplicating a real feedback event.
  const padded = useMemo(() => {
    const need = 5 - recent.length
    if (need <= 0) return []
    const seen = new Set(recent.map((item) => item.mangaBakaId))
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
  const exposed = summary?.exposed ?? 0
  const pushingDown = summary?.pushingDown ?? 0

  async function undoItem(item: FeedbackActivity) {
    setActionError('')
    try {
      await undo.mutateAsync({
        eventId: item.id,
        expectedRevision: item.stateRevision,
        clientMutationId: crypto.randomUUID(),
      })
    } catch (cause) {
      setActionError(String(cause))
    }
  }

  return (
    <>
      <SectionHeader
        icon={IconSparkles}
        title={t`What shapes your recommendations`}
        action={
          <Group gap="xs">
            <Button variant="subtle" size="compact-sm" onClick={() => setHowItWorks((v) => !v)}>
              <Trans>How signals work</Trans>
            </Button>
            <Button variant="outline" size="compact-sm" onClick={() => setManage('titles')}>
              <Trans>Manage signals</Trans>
            </Button>
          </Group>
        }
      />
      <Stack gap="md">
        <Text size="sm" c="dimmed">
          <Trans>
            Your shelf, ratings and reading feed the ranking. Thumbs, hide and seen only touch
            one title each.
          </Trans>
        </Text>

        <Collapse expanded={howItWorks}>
          <Paper withBorder radius="md" p="md">
            <Stack gap="sm">
              <Text size="sm" fw={600}>
                <Trans>How signals work</Trans>
              </Text>
              <Text size="xs" c="dimmed">
                <Trans>
                  <b>Thumbs up</b> counts toward your taste. Similar titles rank higher.
                </Trans>
              </Text>
              <Text size="xs" c="dimmed">
                <Trans>
                  <b>Thumbs down</b> and ratings of 4 or under push down titles that are
                  closer to what you rejected than to what you kept. What they share shows up
                  above once three or more agree.
                </Trans>
              </Text>
              <Text size="xs" c="dimmed">
                <Trans>
                  <b>Hide and dismiss</b> only affect that one title.
                </Trans>
              </Text>
              <Text size="xs" c="dimmed">
                <Trans>
                  <b>Seen elsewhere</b> stops a title being recommended without changing taste.
                </Trans>
              </Text>
              <Text size="xs" c="dimmed">
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
          <Alert color="red">
            <Trans>Could not load your signals: {loadError}</Trans>
          </Alert>
        )}
        {actionError && (
          <Alert color="red">
            <Trans>{actionError} Refresh the page and try again.</Trans>
          </Alert>
        )}

        {lab && summary && (
          <>
            <SimpleGrid cols={{ base: 2, md: 4 }} spacing="sm">
              <StatTile
                icon={IconBooks}
                label={t`titles on the shelf`}
                value={summary.visibleShelf}
                hint={t`${adds} added by you, ${excluded} excluded from taste`}
              />
              <StatTile
                icon={IconBook}
                accent="info"
                label={t`read`}
                value={summary.readSources}
                hint={t`${rated} rated`}
              />
              <StatTile
                icon={IconThumbUp}
                accent="ok"
                label={t`thumbs up / down`}
                hint={t`${pushingDown} kept out of your taste`}
                value={
                  <>
                    <Text span inherit c="teal">{summary.liked}</Text>
                    <Text span inherit c="dimmed" mx={8}>/</Text>
                    <Text span inherit c="red">{summary.disliked}</Text>
                  </>
                }
              />
              <StatTile
                icon={IconEyeOff}
                accent="warn"
                label={t`hidden or dismissed`}
                value={suppressed}
                hint={t`${exposed} seen elsewhere`}
              />
            </SimpleGrid>

            {lab.avoids.length > 0 && <AvoidRow avoids={lab.avoids} />}

            {lab.rankingMode === 'fallback' && (
              <Text size="xs" c="dimmed">
                <Trans>
                  Catalogue fallback is active. Personal add weights need semantic ranking.
                </Trans>
              </Text>
            )}
            {lab.rankingMode === 'semantic' && !lab.capabilities.personalAddWeighting && (
              <Text size="xs" c="dimmed">
                <Trans>Personal add weighting is disabled for this instance.</Trans>
              </Text>
            )}

            <Card withBorder radius="lg" padding="md">
              <Group justify="space-between" align="center" mb={4}>
                <Text size="sm" fw={600}>
                  <Trans>Recent feedback</Trans>
                </Text>
                <Button size="xs" variant="subtle" onClick={() => setManage('titles')}>
                  <Trans>Show all</Trans>
                </Button>
              </Group>
              {recent.length === 0 && padded.length === 0 && (
                <Text size="sm" c="dimmed">
                  <Trans>
                    Thumbs, hide or dismiss a recommendation and it shows up here.
                  </Trans>
                </Text>
              )}
              <Stack gap={2}>
                {recent.map((item, index) => (
                  <div key={item.id}>
                    {dayOf(item) !== dayOf(recent[index - 1]) && (
                      <Text size="xs" fw={600} c="dimmed" tt="uppercase" mt={index === 0 ? 0 : 10}>
                        <DayLabel value={item.occurredAtUtc} />
                      </Text>
                    )}
                    <Group gap="sm" wrap="nowrap" py={6}>
                      <SeriesThumb url={item.coverUrl} alt={item.title ?? ''} />
                      <div style={{ flex: 1, minWidth: 0 }}>
                        <Text size="sm" fw={500} truncate>
                          {item.title ?? untitled(item.mangaBakaId)}
                        </Text>
                        <Group gap={6} wrap="nowrap" mt={2}>
                          <ActionPill item={item} />
                          <Text size="xs" c="dimmed" truncate>
                            {[phrase(item.action), formatTime(item.occurredAtUtc)]
                              .filter(Boolean).join(' · ')}
                          </Text>
                        </Group>
                      </div>
                      {newestPerTitle.has(item.id) && UNDOABLE.includes(item.action) && (
                        <Button
                          size="xs" variant="subtle" loading={undo.isPending}
                          onClick={() => void undoItem(item)}
                        >
                          <Trans>Undo</Trans>
                        </Button>
                      )}
                    </Group>
                  </div>
                ))}
                {padded.length > 0 && (
                  <Text size="xs" fw={600} c="dimmed" tt="uppercase" mt={recent.length === 0 ? 0 : 10}>
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
      <ManageSignalsModal
        opened={manage !== null} initialTab={manage ?? 'titles'} onClose={() => setManage(null)}
      />
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
            <Badge size="sm" variant="light" color="red">
              {avoid.label} · <Plural value={count} one="# title" other="# titles" />
            </Badge>
          </Tooltip>
        )
      })}
    </Group>
  )
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
      case 'like': return [t`👍 Liked`, 'teal']
      case 'dislike': return [t`👎 Disliked`, 'red']
      case 'hide': return [t`Hidden`, 'yellow']
      case 'dismiss': {
        const until = item.dismissedUntilUtc ? formatDate(item.dismissedUntilUtc) : ''
        return [until ? t`Dismissed until ${until}` : t`Dismissed`, 'yellow']
      }
      case 'mark-exposed': return [t`Seen elsewhere`, 'blue']
      case 'clear-suppression': return [t`Restored`, 'gray']
      case 'clear-exposure': return [t`Seen cleared`, 'gray']
      case 'clear-sentiment': return [t`Rating cleared`, 'gray']
      case 'undo': return [t`Undone`, 'gray']
      default: return [item.action.replaceAll('-', ' '), 'gray']
    }
  })()
  return <Badge size="sm" variant="light" color={color} style={{ flexShrink: 0 }}>{label}</Badge>
}

type ShelfPadItem = FeedbackLabData['sources'][number] & { kind: 'rated' | 'read' | 'added' }

function formatRating(rating: number): string {
  return Number.isInteger(rating) ? String(rating) : rating.toFixed(1)
}

/** Pill + dimmed label for a shelf-derived row padding the recent-feedback card. */
function useShelfPillInfo(item: ShelfPadItem) {
  const { t } = useLingui()
  if (item.kind === 'rated' && item.rating != null) {
    const label = t`★ ${formatRating(item.rating)} rated`
    if (item.rating >= 4.5) return { label, color: 'teal', phrase: t`counts toward your taste` }
    if (item.rating <= 2) return { label, color: 'red', phrase: t`pushes down titles close to it` }
    return { label, color: 'gray', phrase: t`neutral, seeds at shelf weight` }
  }
  if (item.kind === 'read') return { label: t`Read`, color: 'blue', phrase: t`weighted by how far you got` }
  return { label: t`Added by you`, color: 'grape', phrase: t`counts a little more than the rest` }
}

function ShelfPill({ item }: { item: ShelfPadItem }) {
  const { label, color } = useShelfPillInfo(item)
  return <Badge size="sm" variant="light" color={color} style={{ flexShrink: 0 }}>{label}</Badge>
}

function ShelfPhrase({ item }: { item: ShelfPadItem }) {
  const { phrase } = useShelfPillInfo(item)
  return <Text size="xs" c="dimmed" truncate>{phrase}</Text>
}

/** The short "what it did" phrase beside an action's badge. */
function usePhrase() {
  const { t } = useLingui()
  return (action: string): string => {
    switch (action) {
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
