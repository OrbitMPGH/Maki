import { useMemo, useState } from 'react'
import {
  Alert, Badge, Button, Card, Grid, Group, Loader, Paper, SimpleGrid, Stack, Text, Title, Tooltip,
} from '@mantine/core'
import {
  IconBooks, IconEyeOff, IconThumbUp, IconBook,
} from '@tabler/icons-react'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import type { AvoidanceLabel, FeedbackActivity } from '../../api/recommendationFeedback'
import { useFeedbackLab, useUndoFeedback } from '../../api/recommendationFeedback'
import { StatTile } from '../../components/ui/StatTile'
import { SeriesThumb } from '../stats/SeriesLink'
import { formatDate, formatTime } from '../../format'
import { ManageSignalsModal } from './ManageSignalsModal'

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
  const [manage, setManage] = useState(false)
  const [actionError, setActionError] = useState('')

  const recent = useMemo(() => (lab?.activity.items ?? []).slice(0, 5), [lab])
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
    <Card withBorder radius="lg" padding="lg">
      <Stack gap="md">
        <Group justify="space-between" align="flex-start" wrap="wrap">
          <div style={{ minWidth: 0 }}>
            <Title order={3}>
              <Trans>What shapes your recommendations</Trans>
            </Title>
            <Text size="sm" c="dimmed">
              <Trans>
                Your shelf, ratings and reading feed the ranking. Thumbs, hide and seen only touch
                one title each.
              </Trans>
            </Text>
          </div>
          <Button variant="outline" onClick={() => setManage(true)}>
            <Trans>Manage signals</Trans>
          </Button>
        </Group>

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

            <Grid gap="md">
              <Grid.Col span={{ base: 12, md: 7 }}>
                <Group justify="space-between" align="center" mb={4}>
                  <Text size="sm" fw={600}>
                    <Trans>Recent feedback</Trans>
                  </Text>
                  <Button size="xs" variant="subtle" onClick={() => setManage(true)}>
                    <Trans>Show all</Trans>
                  </Button>
                </Group>
                {recent.length === 0 && (
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
                </Stack>
              </Grid.Col>

              <Grid.Col span={{ base: 12, md: 5 }}>
                <Paper withBorder radius="md" p="md" h="100%">
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
                        <b>Thumbs down</b> and ratings of 4 or under stop a title steering your
                        recommendations. What they share shows up above once three or more agree.
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
              </Grid.Col>
            </Grid>
          </>
        )}
      </Stack>
      <ManageSignalsModal opened={manage} onClose={() => setManage(false)} />
    </Card>
  )
}

/**
 * What the titles a reader keeps out of their taste have in common, when enough of them agree.
 *
 * An observation about the profile, not a claim about the ranking: nothing here is fed back into
 * scoring. A chip appears at three supporting titles, which is the threshold that stops one thumbs
 * down claiming a genre.
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
        return (
          <Tooltip key={`${avoid.kind}:${avoid.label}`} label={titles} withArrow multiline w={260}>
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
