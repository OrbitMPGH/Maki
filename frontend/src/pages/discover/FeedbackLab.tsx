import { useState } from 'react'
import { Alert, Badge, Button, Card, Group, Loader, SegmentedControl, Stack, Text, Title } from '@mantine/core'
import {
  useFeedbackActivity, useFeedbackLab, useFeedbackStates, useMutateFeedback,
  useMutateSignalOverride, useSignalOverrides,
} from '../../api/recommendationFeedback'

type View = 'feedback' | 'library' | 'manage'

export function FeedbackLab() {
  const [view, setView] = useState<View>('feedback')
  const [cursor, setCursor] = useState<number | undefined>()
  const [stateCursor, setStateCursor] = useState<number | undefined>()
  const { data: lab, isLoading, error } = useFeedbackLab()
  const { data: activity } = useFeedbackActivity(cursor)
  const { data: states } = useFeedbackStates(stateCursor)
  const { data: overrides } = useSignalOverrides()
  const feedback = useMutateFeedback()
  const signal = useMutateSignalOverride()
  const [actionError, setActionError] = useState('')
  const [change, setChange] = useState('')

  async function clear(id: number, action: 'clear-suppression' | 'clear-exposure', revision: number) {
    setActionError('')
    try {
      const result = await feedback.mutateAsync({ id, action, expectedRevision: revision, clientMutationId: crypto.randomUUID() })
      setChange(result.changed ? `${result.queueEffect === 'suppressed' ? 'This title is still excluded' : 'This title is eligible again'}. Taste unchanged.` : 'No change to this title.')
    } catch (cause) { setActionError(String(cause)) }
  }

  async function setIgnored(id: number, ignoreAsSeed: boolean) {
    setActionError('')
    const revision = overrides?.find((item) => item.mangaBakaId === id)?.revision ?? 0
    try {
      const result = await signal.mutateAsync({ id, ignoreAsSeed, expectedRevision: revision, clientMutationId: crypto.randomUUID() })
      setChange(result.changed ? ignoreAsSeed ? 'This work is excluded from taste signals now.' : 'This work can shape recommendations again.' : 'No change to this source.')
    } catch (cause) { setActionError(String(cause)) }
  }

  if (lab && !lab.capabilities.labUi) return null

  return (
    <Card withBorder radius="lg" padding="lg">
      <Stack gap="md">
        <div>
          <Title order={3}>Recommendation Feedback Lab</Title>
          <Text size="sm" c="dimmed">
            Shared shelf, your ratings, reading, and your own additions shape recommendations.
            Hide and read or seen actions affect only the selected title.
          </Text>
        </div>
        {isLoading && <Loader size="sm" />}
        {error && <Alert color="red">Could not load feedback: {String(error)}</Alert>}
        {actionError && <Alert color="red">{actionError} Refresh the page and try again.</Alert>}
        {change && <Alert color="blue" role="status">{change}</Alert>}
        {lab && (
          <>
            <Group gap="xs">
              <Badge variant="light">{lab.summary.visibleShelf} shared shelf</Badge>
              <Badge variant="light">{lab.summary.personalAdds} added by you</Badge>
              <Badge variant="light">{lab.summary.ratedSources} rated</Badge>
              <Badge variant="light">{lab.summary.readSources} read</Badge>
              <Badge variant="light">{lab.summary.hidden} hidden</Badge>
              <Badge variant="light">{lab.summary.dismissed} dismissed</Badge>
              <Badge variant="light">{lab.summary.exposed} read or seen</Badge>
            </Group>
            {lab.rankingMode === 'fallback' && (
              <Text size="xs" c="dimmed">Catalogue fallback is active. Personal add weights require semantic ranking and an enabled weighting setting.</Text>
            )}
            {lab.rankingMode === 'semantic' && !lab.capabilities.personalAddWeighting && (
              <Text size="xs" c="dimmed">Personal add weighting is disabled for this instance.</Text>
            )}
            {lab.dimensions.length > 0 && (
              <div>
                <Text size="sm" fw={600}>Evidence in your library</Text>
                <Group gap="xs" mt="xs">
                  {lab.dimensions.map((dimension) => (
                    <Badge key={`${dimension.kind}-${dimension.label}`} variant="outline" color="gray">
                      {dimension.label} · {dimension.evidenceCount} works
                    </Badge>
                  ))}
                </Group>
                <Text size="xs" c="dimmed" mt="xs">One or two works are limited evidence. Excluded sources stay in your reading history.</Text>
              </div>
            )}
          </>
        )}
        <SegmentedControl value={view} onChange={(value) => { setView(value as View); setCursor(undefined) }}
          data={[{ value: 'feedback', label: 'Feedback' }, { value: 'library', label: 'Library activity' }, { value: 'manage', label: 'Manage' }]} />

        {view === 'feedback' && (
          <Stack gap="xs">
            {activity?.items.length === 0 && <Text size="sm" c="dimmed">No feedback yet.</Text>}
            {activity?.items.map((item) => (
              <Group key={item.id} justify="space-between" wrap="wrap">
                <div>
                  <Text size="sm" fw={600}>{item.title ?? `Catalogue title ${item.mangaBakaId}`}</Text>
                  <Text size="xs" c="dimmed">{item.action === 'dismiss' && item.dismissedUntilUtc
                    ? `Dismissed until ${new Date(item.dismissedUntilUtc).toLocaleDateString()}`
                    : item.action.replaceAll('-', ' ')} · {new Date(item.occurredAtUtc).toLocaleString()}</Text>
                </div>
                <Badge color="gray" variant="light">{item.queueEffect}; {item.tasteEffect.toLowerCase()}</Badge>
              </Group>
            ))}
            {activity?.nextCursor && <Button variant="subtle" onClick={() => setCursor(activity.nextCursor!)}>Older activity</Button>}
          </Stack>
        )}

        {view === 'library' && (
          <Stack gap="xs">
            {lab?.sources.filter((source) => source.addedAtUtc).length === 0 &&
              <Text size="sm" c="dimmed">No attributable library additions yet. Older shared shelf entries are not assigned to you.</Text>}
            {lab?.sources.filter((source) => source.addedAtUtc).map((source) => (
              <Group key={source.mangaBakaId} justify="space-between" wrap="wrap">
                <Text size="sm">{source.title}</Text>
                <Text size="xs" c="dimmed">Added to shared library by you · {new Date(source.addedAtUtc!).toLocaleDateString()}</Text>
              </Group>
            ))}
          </Stack>
        )}

        {view === 'manage' && (
          <Stack gap="xs">
            {states?.items.length === 0 && overrides?.length === 0 &&
              <Text size="sm" c="dimmed">No titles hidden, dismissed, seen, or excluded.</Text>}
            {states?.items.map((state) => (
              <Group key={state.mangaBakaId} justify="space-between" wrap="wrap">
                <div>
                  <Text size="sm" fw={600}>{state.title ?? `Catalogue title ${state.mangaBakaId}`}</Text>
                  <Text size="xs" c="dimmed">
                    {state.suppression !== 'none' ? state.suppression : ''}
                    {state.dismissedUntilUtc ? ` until ${new Date(state.dismissedUntilUtc).toLocaleDateString()}` : ''}
                    {state.exposure.length > 0 ? ` · seen: ${state.exposure.join(', ')}` : ''}
                  </Text>
                </div>
                <Group gap="xs">
                  {state.suppression !== 'none' && <Button size="xs" variant="subtle" loading={feedback.isPending}
                    onClick={() => void clear(state.mangaBakaId, 'clear-suppression', state.revision)}>Restore</Button>}
                  {state.exposure.length > 0 && <Button size="xs" variant="subtle" loading={feedback.isPending}
                    onClick={() => void clear(state.mangaBakaId, 'clear-exposure', state.revision)}>Clear seen</Button>}
                </Group>
              </Group>
            ))}
            {lab?.sources.map((source) => {
              const ignored = overrides?.some((item) => item.mangaBakaId === source.mangaBakaId)
              return <Group key={`source-${source.mangaBakaId}`} justify="space-between" wrap="wrap">
                <div>
                  <Text size="sm">{source.title}</Text>
                  <Text size="xs" c="dimmed">{ignored ? 'Excluded from recommendations' : 'Used as a taste signal'}</Text>
                </div>
                <Button size="xs" variant="subtle" loading={signal.isPending}
                  onClick={() => void setIgnored(source.mangaBakaId, !ignored)}>
                  {ignored ? 'Use as signal' : 'Stop using as signal'}
                </Button>
              </Group>
            })}
            {states?.nextCursor && <Button variant="subtle" onClick={() => setStateCursor(states.nextCursor!)}>More managed titles</Button>}
          </Stack>
        )}
      </Stack>
    </Card>
  )
}
