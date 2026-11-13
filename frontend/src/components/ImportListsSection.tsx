import { useState } from 'react'
import {
  Badge,
  Button,
  Chip,
  Group,
  NumberInput,
  Select,
  Stack,
  Switch,
  Text,
} from '@mantine/core'
import { IconDownload, IconRefresh } from '../lib/icons'
import { notifications } from '../lib/toast'
import { msg } from '@lingui/core/macro'
import { Trans, useLingui } from '@lingui/react/macro'
import type { MessageDescriptor } from '@lingui/core'
import { useNavigate } from 'react-router-dom'
import { SettingsRow, SettingsRows, SettingsRowsBlock } from './settings/SettingsRow'
import { TagChip } from './ui/TagChip'
import { ConfirmDialog } from './ui/ConfirmDialog'
import { Note } from './ui/Note'
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

/** Mirrors the backend's `ScrobbleStatus` values, in the order they read best. */
const STATUS_OPTIONS: { value: ImportListStatus; label: MessageDescriptor }[] = [
  { value: 'Reading', label: msg`Reading` },
  { value: 'PlanToRead', label: msg`Plan to read (to your shortlist)` },
  { value: 'Completed', label: msg`Completed` },
]

function TrackerRow({
  tracker,
  listsEnabled,
  opened,
  onToggle,
}: {
  tracker: ImportListTrackerDto
  listsEnabled: boolean
  opened: boolean
  onToggle: () => void
}) {
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
    savePrefs.mutate({ service, patch })

  const run = (full: boolean) =>
    runList.mutate(
      { service, full },
      {
        onSuccess: () => {
          notifications.show({
            message: t`Sync started, you will get a notification when it finishes`,
          })
        },
      },
    )

  if (!connected) {
    return (
      <SettingsRow
        title={label}
        status={{ tone: 'neutral', label: t`Not connected` }}
        summary={<Trans>Connect {label} on the Scrobble page to import from it.</Trans>}
      />
    )
  }

  const runningPartial = runList.isPending && runList.variables?.service === service && !runList.variables?.full
  const runningFull = runList.isPending && runList.variables?.service === service && runList.variables?.full

  return (
    <SettingsRow
      title={label}
      status={prefs.enabled ? { tone: 'ok', label: t`On` } : { tone: 'neutral', label: t`Off` }}
      summary={lastRun ? <LastRunLine lastRun={lastRun} /> : <Trans>Never run.</Trans>}
      opened={opened}
      onToggle={onToggle}
    >
      <Switch
        label={t`Import from ${label}`}
        checked={prefs.enabled}
        onChange={(e) => save({ enabled: e.currentTarget.checked })}
      />

      <Stack gap={10}>
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
                placeholder={t`Picked by the root folder rules`}
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
              description={t`All: every chapter. Smart: only the next few ahead of your reading. Main only: skips specials.`}
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

        <Group gap="xs" mt={4}>
          <Button
            size="compact-sm"
            variant="default"
            leftSection={<IconRefresh size={16} />}
            loading={runningPartial}
            disabled={!listsEnabled}
            onClick={() => run(false)}
          >
            <Trans>Run now</Trans>
          </Button>
          <Button
            size="compact-sm"
            variant="subtle"
            leftSection={<IconDownload size={16} />}
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
    </SettingsRow>
  )
}

function LastRunLine({ lastRun }: { lastRun: NonNullable<ImportListTrackerDto['lastRun']> }) {
  const { added, requested, shortlisted, skipped, errors } = lastRun
  const when = relativeTime(lastRun.at)
  if (lastRun.dumpUnavailable) {
    return <Trans>Last run {when}: skipped, the local MangaBaka database is not downloaded yet</Trans>
  }
  return (
    <>
      {errors > 0 ? (
        <Trans>
          Last run {when}: {added} added, {requested} requested, {shortlisted} shortlisted, {skipped} skipped, {errors} errors
        </Trans>
      ) : (
        <Trans>
          Last run {when}: {added} added, {requested} requested, {shortlisted} shortlisted, {skipped} skipped
        </Trans>
      )}
      {lastRun.shortlistFull && (
        <>
          {' '}
          <Trans>Your shortlist is full.</Trans>
        </>
      )}
    </>
  )
}

function SkipRow({ skip, trackerLabel }: { skip: ImportListSkipDto; trackerLabel: string }) {
  const { t } = useLingui()
  const navigate = useNavigate()
  const retry = useRetryImportListSkip()
  const ignore = useIgnoreImportListSkip()
  const { id, title, createdAt, reason } = skip
  const isIgnored = reason === 'Ignored'

  return (
    <SettingsRow
      title={title}
      chips={<TagChip size="sm">{trackerLabel}</TagChip>}
      status={isIgnored ? { tone: 'neutral', label: t`Ignored` } : null}
      summary={relativeTime(createdAt)}
      aside={
      <Group gap="xs" wrap="nowrap">
        <Button size="compact-xs" variant="light" onClick={() => navigate(`/add?q=${encodeURIComponent(title)}`)}>
          <Trans>Search</Trans>
        </Button>
        {isIgnored ? (
          <Button
            size="compact-xs"
            variant="default"
            loading={retry.isPending && retry.variables === id}
            onClick={() => retry.mutate(id)}
          >
            <Trans>Un-ignore</Trans>
          </Button>
        ) : (
          <>
            <Button
              size="compact-xs"
              variant="default"
              loading={retry.isPending && retry.variables === id}
              onClick={() => retry.mutate(id)}
            >
              <Trans>Retry</Trans>
            </Button>
            <Button
              size="compact-xs"
              variant="subtle"
              loading={ignore.isPending && ignore.variables === id}
              onClick={() => ignore.mutate(id)}
            >
              <Trans>Ignore</Trans>
            </Button>
          </>
        )}
      </Group>
      }
    />
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
  const [open, setOpen] = useState<string | null>(null)
  const review = data?.skipped.filter((s) => s.reason === 'Unmatched' || s.reason === 'Ignored') ?? []

  if (!data) return null

  return (
    <>
      {data.enabled === false && (
        <SettingsRowsBlock>
          <Note tone="info">
            <Trans>Import lists are turned off for this instance.</Trans>
          </Note>
        </SettingsRowsBlock>
      )}
      <SettingsRows>
        {data.trackers.map((tr) => (
          <TrackerRow
            key={tr.service}
            tracker={tr}
            listsEnabled={data.enabled !== false}
            opened={open === tr.service}
            onToggle={() => setOpen((current) => (current === tr.service ? null : tr.service))}
          />
        ))}
      </SettingsRows>

      <SettingsRowsBlock>
        <Group gap="xs">
          <Text fw={600} size="sm">
            <Trans>Not matched or ignored</Trans>
          </Text>
          {review.length > 0 && (
            <Badge variant="light" color="var(--warn)">
              {review.length}
            </Badge>
          )}
        </Group>
        {review.length === 0 && (
          <Text size="sm" c="var(--ink-3)" mt={4}>
            <Trans>Nothing unmatched.</Trans>
          </Text>
        )}
      </SettingsRowsBlock>
      {review.length > 0 && (
        <SettingsRows>
          {review.map((s) => (
            <SkipRow key={s.id} skip={s} trackerLabel={trackerLabelFor(data, s.service)} />
          ))}
        </SettingsRows>
      )}
    </>
  )
}
