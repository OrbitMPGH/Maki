import { useMemo, useState } from 'react'
import { Button, Checkbox, Group, Modal, Select, Stack, Text } from '@mantine/core'
import { IconRefresh, IconTrash } from '@tabler/icons-react'
import { notifications } from '@mantine/notifications'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { plural, t as now } from '@lingui/core/macro'
import {
  useFailedQueueGroups,
  useRemoveFailedQueue,
  useRetryFailedQueue,
} from '../api/hooks'
import { queueFailureKeyLabel } from '../api/queue'
import type { QueueFailedAction, QueueFailureGroupDto } from '../api/types'
import { useLabel } from '../i18n-context'
import { useSourceLabel } from '../sourceLabels'

type Mode = 'retry' | 'remove'

const TORRENT_SOURCE = 'torrent'

/** Retry all and Remove all for the Failed rows, with a way to narrow to one failure reason or source. */
export function FailedQueueActions() {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const sourceLabel = useSourceLabel()
  const groups = useFailedQueueGroups().data
  const retry = useRetryFailedQueue()
  const remove = useRemoveFailedQueue()

  const [mode, setMode] = useState<Mode | null>(null)
  const [reason, setReason] = useState<string | null>(null)
  const [source, setSource] = useState<string | null>(null)
  const [blockReleases, setBlockReleases] = useState(true)

  const all = useMemo(() => groups ?? [], [groups])
  const reasonKeys = useMemo(
    () => [...new Set(all.map((g) => g.reasonKey).filter((k): k is string => k !== null))],
    [all],
  )
  const sources = useMemo(() => [...new Set(all.map((g) => g.sourceName))], [all])

  const matches = (g: QueueFailureGroupDto, m: Mode | null) =>
    (reason === null || g.reasonKey === reason) &&
    (source === null || g.sourceName === source) &&
    // A torrent grab is not resubmitted by a retry, so it is not part of what one would act on.
    (m !== 'retry' || g.sourceName !== TORRENT_SOURCE)
  const matching = all.filter((g) => matches(g, mode))
  const matchingCount = matching.reduce((sum, g) => sum + g.count, 0)
  const includesTorrents = matching.some((g) => g.sourceName === TORRENT_SOURCE)

  const total = all.reduce((sum, g) => sum + g.count, 0)
  const retryable = all.some((g) => g.sourceName !== TORRENT_SOURCE)
  if (total === 0) return null

  // Reasons the chosen source has failed with, and the other way round, so the two lists never
  // offer a pair that matches nothing.
  const reasonOptions = reasonKeys
    .filter((k) => all.some((g) => g.reasonKey === k && (source === null || g.sourceName === source)))
    .map((k) => ({ value: k, label: queueFailureKeyLabel(k, renderLabel) }))
  const sourceOptions = sources
    .filter((s) => all.some((g) => g.sourceName === s && (reason === null || g.reasonKey === reason)))
    .map((s) => ({ value: s, label: sourceLabel(s) }))

  const filter = (): QueueFailedAction => ({
    reason: reason ?? undefined,
    source: source ?? undefined,
  })

  const close = () => {
    setMode(null)
    setReason(null)
    setSource(null)
    setBlockReleases(true)
  }

  const runRetry = (action: QueueFailedAction) =>
    retry.mutate(action, {
      onSuccess: ({ affected }) => {
        notifications.show({
          message:
            affected > 0
              ? plural(affected, { one: 'Retrying # failed download', other: 'Retrying # failed downloads' })
              : now`Nothing to retry`,
          color: affected > 0 ? 'var(--ok)' : undefined,
        })
        close()
      },
      onError: () => notifications.show({ message: now`Couldn't retry the failed downloads`, color: 'var(--danger)' }),
    })

  const openRetry = () => {
    // One reason on one source leaves nothing to choose between.
    if (reasonKeys.length <= 1 && sources.length <= 1) {
      runRetry({})
      return
    }
    setMode('retry')
  }

  const runRemove = () =>
    remove.mutate(
      { ...filter(), blockReleases: includesTorrents ? blockReleases : undefined },
      {
        onSuccess: ({ affected }) => {
          notifications.show({
            message: plural(affected, { one: 'Removed # failed download', other: 'Removed # failed downloads' }),
          })
          close()
        },
        onError: () => notifications.show({ message: now`Couldn't remove the failed downloads`, color: 'var(--danger)' }),
      },
    )

  const showPickers = reasonOptions.length > 1 || sourceOptions.length > 1 || reason !== null || source !== null

  return (
    <>
      <Group gap="xs">
        {retryable && (
          <Button variant="light" leftSection={<IconRefresh size={16} />} loading={retry.isPending} onClick={openRetry}>
            <Trans>Retry failed</Trans>
          </Button>
        )}
        <Button variant="light" color="var(--danger)" leftSection={<IconTrash size={16} />} onClick={() => setMode('remove')}>
          <Trans>Remove failed</Trans>
        </Button>
      </Group>

      <Modal
        opened={mode !== null}
        onClose={close}
        centered
        title={mode === 'retry' ? <Trans>Retry failed downloads</Trans> : <Trans>Remove failed downloads</Trans>}
      >
        <Stack gap="sm">
          {showPickers && (
            <>
              <Select
                label={t`Reason`}
                placeholder={t`All reasons`}
                data={reasonOptions}
                value={reason}
                onChange={setReason}
                clearable
                allowDeselect
                comboboxProps={{ withinPortal: true }}
              />
              <Select
                label={t`Source`}
                placeholder={t`All sources`}
                data={sourceOptions}
                value={source}
                onChange={setSource}
                clearable
                allowDeselect
                comboboxProps={{ withinPortal: true }}
              />
            </>
          )}
          <Text size="sm">
            {mode === 'retry' ? (
              <Plural
                value={matchingCount}
                one="# failed download will be queued again, ignoring the automatic retry limit."
                other="# failed downloads will be queued again, ignoring the automatic retry limit."
              />
            ) : (
              <Plural
                value={matchingCount}
                one="# failed download will be removed from the queue."
                other="# failed downloads will be removed from the queue."
              />
            )}
          </Text>
          {mode === 'remove' && includesTorrents && (
            <Checkbox
              checked={blockReleases}
              onChange={(e) => setBlockReleases(e.currentTarget.checked)}
              label={t`Don't grab removed torrent releases again`}
            />
          )}
          <Group justify="flex-end">
            <Button variant="default" onClick={close}>
              <Trans>Cancel</Trans>
            </Button>
            {mode === 'retry' ? (
              <Button loading={retry.isPending} disabled={matchingCount === 0} onClick={() => runRetry(filter())}>
                <Trans>Retry</Trans>
              </Button>
            ) : (
              <Button
                color="var(--danger-fill)"
                loading={remove.isPending}
                disabled={matchingCount === 0}
                onClick={runRemove}
              >
                <Trans>Remove</Trans>
              </Button>
            )}
          </Group>
        </Stack>
      </Modal>
    </>
  )
}
