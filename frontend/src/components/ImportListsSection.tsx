import { useState } from 'react'
import {
  Alert,
  Badge,
  Button,
  Chip,
  Group,
  NumberInput,
  Select,
  Stack,
  Switch,
  Text,
  Title,
} from '@mantine/core'
import { IconDownload, IconRefresh } from '@tabler/icons-react'
import { notifications } from '@mantine/notifications'
import { msg } from '@lingui/core/macro'
import { Trans, useLingui } from '@lingui/react/macro'
import type { MessageDescriptor } from '@lingui/core'
import { useNavigate } from 'react-router-dom'
import { Panel } from './ui/Panel'
import { TagChip } from './ui/TagChip'
import { ConfirmDialog } from './ui/ConfirmDialog'
import { relativeTime } from './ui/time'
import { useLabel } from '../i18n-context'
import { useAuth } from '../auth/AuthProvider'
import { MONITOR_OPTIONS } from './series/SeriesActionsMenu'
import {
  useIgnoreImportListSkip,
  useImportLists,
  useRetryImportListSkip,
  useRootFolders,
  useRunImportList,
  useSaveImportListPrefs,
} from '../api/hooks'
import type {
  ImportListSkipDto,
  ImportListStatus,
  ImportListTrackerDto,
  ImportListTrackerPrefs,
  ImportListsStatusDto,
} from '../api/types'

const onErrorToast = (err: unknown) =>
  notifications.show({ color: 'var(--danger)', message: String(err) })

/** Mirrors the backend's `ScrobbleStatus` values, in the order they read best. */
const STATUS_OPTIONS: { value: ImportListStatus; label: MessageDescriptor }[] = [
  { value: 'Reading', label: msg`Reading` },
  { value: 'PlanToRead', label: msg`Plan to read` },
  { value: 'Completed', label: msg`Completed` },
]

function TrackerPanel({ tracker, listsEnabled }: { tracker: ImportListTrackerDto; listsEnabled: boolean }) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const { can } = useAuth()
  const canAdd = can('AddSeries')
  const { data: rootFolders } = useRootFolders()
  const savePrefs = useSaveImportListPrefs()
  const runList = useRunImportList()
  const [confirmingFull, setConfirmingFull] = useState(false)
  const [maxPerRunDraft, setMaxPerRunDraft] = useState<number | string>(tracker.prefs.maxPerRun)

  const { label, service, connected, prefs, lastRun } = tracker

  const save = (patch: Partial<ImportListTrackerPrefs>) =>
    savePrefs.mutate({ service, ...prefs, ...patch }, { onError: onErrorToast })

  const run = (full: boolean) =>
    runList.mutate(
      { service, full },
      {
        onSuccess: (result) => {
          if ('started' in result) {
            notifications.show({
              message: t`Sync started, you will get a notification when it finishes`,
            })
            return
          }
          const { added, requested, skipped, errors } = result
          notifications.show({
            message: t`${added} added, ${requested} requested, ${skipped} skipped, ${errors} errors`,
          })
        },
        onError: onErrorToast,
      },
    )

  if (!connected) {
    return (
      <Panel p="md">
        <Text fw={700}>{label}</Text>
        <Text size="sm" c="var(--ink-3)" mt={4}>
          <Trans>Connect {label} above to import from it.</Trans>
        </Text>
      </Panel>
    )
  }

  const runningPartial = runList.isPending && runList.variables?.service === service && !runList.variables?.full
  const runningFull = runList.isPending && runList.variables?.service === service && runList.variables?.full

  return (
    <Panel p="md">
      <Group justify="space-between" wrap="nowrap" align="flex-start">
        <Text fw={700}>{label}</Text>
        <Switch
          checked={prefs.enabled}
          onChange={(e) => save({ enabled: e.currentTarget.checked })}
          aria-label={t`Import from ${label}`}
        />
      </Group>

      <Stack gap={10} mt="sm">
        <div>
          <Text size="xs" c="var(--ink-3)" mb={4}>
            <Trans>Import entries with status</Trans>
          </Text>
          <Chip.Group
            multiple
            value={prefs.statuses}
            onChange={(value) => {
              // A tracker with nothing selected would import nothing at all; keep at least one.
              if (value.length === 0) return
              save({ statuses: value as ImportListStatus[] })
            }}
          >
            <Group gap="xs">
              {STATUS_OPTIONS.map((o) => (
                <Chip key={o.value} value={o.value} size="xs" variant="light">
                  {renderLabel(o.label)}
                </Chip>
              ))}
            </Group>
          </Chip.Group>
        </div>

        {canAdd ? (
          <>
            {rootFolders && rootFolders.length > 0 && (
              <Select
                label={t`Root folder`}
                placeholder={t`First folder you can see`}
                data={rootFolders.map((f) => ({ value: String(f.id), label: f.path }))}
                value={prefs.rootFolderId != null ? String(prefs.rootFolderId) : null}
                onChange={(value) => save({ rootFolderId: value ? Number(value) : null })}
                size="xs"
                clearable
              />
            )}

            <Switch
              label={t`Monitor for new chapters`}
              checked={prefs.monitored}
              onChange={(e) => save({ monitored: e.currentTarget.checked })}
              labelPosition="left"
              size="sm"
              styles={{ body: { justifyContent: 'space-between' } }}
            />

            <Select
              label={t`New chapters`}
              data={MONITOR_OPTIONS.map((o) => ({ value: o.value, label: renderLabel(o.label) }))}
              value={prefs.monitorNewItems}
              onChange={(value) => value && save({ monitorNewItems: value })}
              size="xs"
            />
          </>
        ) : (
          <Text size="xs" c="var(--ink-3)">
            <Trans>Entries are filed as requests for an admin to approve.</Trans>
          </Text>
        )}

        <NumberInput
          label={t`Max per run`}
          min={1}
          max={100}
          value={maxPerRunDraft}
          onChange={setMaxPerRunDraft}
          onBlur={() => {
            const clamped = Math.min(100, Math.max(1, Number(maxPerRunDraft) || 1))
            setMaxPerRunDraft(clamped)
            if (clamped !== prefs.maxPerRun) save({ maxPerRun: clamped })
          }}
          size="xs"
        />

        <Text size="xs" c="var(--ink-3)">
          {lastRun ? (
            <LastRunLine lastRun={lastRun} />
          ) : (
            <Trans>Never run.</Trans>
          )}
        </Text>

        <Group gap="xs" mt={4}>
          <Button
            size="compact-sm"
            variant="default"
            leftSection={<IconRefresh size={14} />}
            loading={runningPartial}
            disabled={!listsEnabled}
            onClick={() => run(false)}
          >
            <Trans>Run now</Trans>
          </Button>
          <Button
            size="compact-sm"
            variant="subtle"
            leftSection={<IconDownload size={14} />}
            loading={runningFull}
            disabled={!listsEnabled}
            onClick={() => setConfirmingFull(true)}
          >
            <Trans>Sync everything</Trans>
          </Button>
        </Group>
      </Stack>

      <ConfirmDialog
        opened={confirmingFull}
        onClose={() => setConfirmingFull(false)}
        title={<Trans>Sync everything from {label}?</Trans>}
        confirmLabel={<Trans>Sync everything</Trans>}
        loading={runningFull}
        onConfirm={() => {
          run(true)
          setConfirmingFull(false)
        }}
      >
        {canAdd ? (
          <Trans>
            This adds every matching entry from your {label} list at once, ignoring the per-run limit.
          </Trans>
        ) : (
          <Trans>
            This files a request for every matching entry from your {label} list, up to the per-run limit.
          </Trans>
        )}
      </ConfirmDialog>
    </Panel>
  )
}

function LastRunLine({ lastRun }: { lastRun: NonNullable<ImportListTrackerDto['lastRun']> }) {
  const { added, requested, skipped, errors } = lastRun
  const when = relativeTime(lastRun.at)
  return errors > 0 ? (
    <Trans>
      Last run {when}: {added} added, {requested} requested, {skipped} skipped, {errors} errors
    </Trans>
  ) : (
    <Trans>
      Last run {when}: {added} added, {requested} requested, {skipped} skipped
    </Trans>
  )
}

function SkipRow({ skip, trackerLabel }: { skip: ImportListSkipDto; trackerLabel: string }) {
  const navigate = useNavigate()
  const retry = useRetryImportListSkip()
  const ignore = useIgnoreImportListSkip()
  const { id, title, createdAt, reason } = skip
  const isIgnored = reason === 'Ignored'

  return (
    <Panel p="sm">
      <Group justify="space-between" wrap="nowrap" gap="xs">
        <Group gap="xs" wrap="nowrap" style={{ minWidth: 0 }}>
          <TagChip size="sm">{trackerLabel}</TagChip>
          <Text size="sm" fw={600} lineClamp={1} style={{ minWidth: 0 }}>
            {title}
          </Text>
          {isIgnored && (
            <Badge size="sm" variant="light" color="var(--ink-3)">
              <Trans>Ignored</Trans>
            </Badge>
          )}
        </Group>
        <Text size="xs" c="var(--ink-3)" style={{ flexShrink: 0 }}>
          {relativeTime(createdAt)}
        </Text>
      </Group>
      <Group gap="xs" mt="xs">
        <Button size="compact-xs" variant="light" onClick={() => navigate(`/add?q=${encodeURIComponent(title)}`)}>
          <Trans>Search</Trans>
        </Button>
        {isIgnored ? (
          <Button
            size="compact-xs"
            variant="default"
            loading={retry.isPending && retry.variables === id}
            onClick={() => retry.mutate(id, { onError: onErrorToast })}
          >
            <Trans>Un-ignore</Trans>
          </Button>
        ) : (
          <>
            <Button
              size="compact-xs"
              variant="default"
              loading={retry.isPending && retry.variables === id}
              onClick={() => retry.mutate(id, { onError: onErrorToast })}
            >
              <Trans>Retry</Trans>
            </Button>
            <Button
              size="compact-xs"
              variant="subtle"
              loading={ignore.isPending && ignore.variables === id}
              onClick={() => ignore.mutate(id, { onError: onErrorToast })}
            >
              <Trans>Ignore</Trans>
            </Button>
          </>
        )}
      </Group>
    </Panel>
  )
}

/** Tracker label for a skip row, falling back to the raw service key if the tracker list hasn't loaded it. */
function trackerLabelFor(data: ImportListsStatusDto, service: string): string {
  return data.trackers.find((tr) => tr.service === service)?.label ?? service
}

/**
 * Import-list card for the Scrobble page: per-tracker prefs (statuses, root folder, monitor mode,
 * per-run cap), a manual run, an unbounded "sync everything", and the unmatched/ignored-skip review
 * queue. Sits below the tracker connection cards, since a tracker has to be connected before any of
 * this does anything.
 */
export function ImportListsSection() {
  const { data } = useImportLists()
  const review = data?.skipped.filter((s) => s.reason === 'Unmatched' || s.reason === 'Ignored') ?? []

  if (!data) return null

  return (
    <>
      <Title order={4} mb="sm">
        <Trans>Import lists</Trans>
      </Title>
      {data.enabled === false && (
        <Alert color="var(--ink-3)" mb="md">
          <Trans>Import lists are turned off for this instance.</Trans>
        </Alert>
      )}
      <Stack gap="sm" mb="lg">
        {data.trackers.map((tr) => (
          <TrackerPanel key={tr.service} tracker={tr} listsEnabled={data.enabled !== false} />
        ))}
      </Stack>

      <Group gap="xs" mb="sm">
        <Title order={4}>
          <Trans>Not matched or ignored</Trans>
        </Title>
        {review.length > 0 && (
          <Badge variant="light" color="var(--warn)">
            {review.length}
          </Badge>
        )}
      </Group>
      {review.length > 0 ? (
        <Stack gap="sm" mb="lg">
          {review.map((s) => (
            <SkipRow key={s.id} skip={s} trackerLabel={trackerLabelFor(data, s.service)} />
          ))}
        </Stack>
      ) : (
        <Text size="sm" c="var(--ink-3)" mb="lg">
          <Trans>Nothing unmatched.</Trans>
        </Text>
      )}
    </>
  )
}
