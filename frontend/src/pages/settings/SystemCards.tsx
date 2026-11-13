import { useEffect, useState, useSyncExternalStore } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { getSkippedVersion, setSkippedVersion, subscribeSkippedVersion } from '../../lib/updateSkip'
import { Trans, useLingui } from '@lingui/react/macro'
import { plural, t as now } from '@lingui/core/macro'
import {
  ActionIcon,
  Button,
  Code,
  Collapse,
  FileButton,
  Group,
  Modal,
  Progress,
  ScrollArea,
  Stack,
  SimpleGrid,
  Switch,
  Table,
  Text,
  TextInput,
  UnstyledButton,
  VisuallyHidden,
  Box,
} from '@mantine/core'
import {
  IconChevronDown,
  IconChevronRight,
  IconDownload,
  IconTrash,
  IconUpload,
} from '../../lib/icons'
import { notifications } from '../../lib/toast'
import { ConfirmDialog } from '../../components/ui/ConfirmDialog'
import { ModalFooter, ModalHeading } from '../../components/ui/ModalShell'
import { Note } from '../../components/ui/Note'
import { FigureStrip } from '../../components/ui/FigureStrip'
import { TagChip } from '../../components/ui/TagChip'
import { relativeTime } from '../../components/ui/time'
import { SettingsSection } from './SettingsSection'
import {
  useBackups,
  useBackupSettings,
  useCreateBackup,
  useDeleteBackup,
  useRestoreBackup,
  useSaveBackupSettings,
  useUploadRestore,
  downloadBackup,
  useApplyUpdate,
  useCheckForUpdatesNow,
  useImageCache,
  useRebuildImageCache,
  useSaveUpdateSettings,
  useUpdateSettings,
  useUpdateStatus,
} from '../../api/hooks'
import { formatBytes, formatDateTime, formatNumber } from '../../format'
import { SettingsNumberInput } from '../../components/settings/SettingsNumberInput'
import { useHealthAction, useHealthData, type HealthOptions } from '../../api/health'
import { TimeAgo } from '../../components/ui/TimeAgo'
import { Callout } from '../../components/ui/Callout'

type RestoreTarget = { kind: 'existing'; name: string } | { kind: 'upload'; file: File }

export function BackupSection() {
  const { t } = useLingui()
  const { data: backups } = useBackups()
  const { data: retentionSettings } = useBackupSettings()
  const create = useCreateBackup()
  const remove = useDeleteBackup()
  const restore = useRestoreBackup()
  const upload = useUploadRestore()
  const saveRetention = useSaveBackupSettings()

  const kindLabel = (kind: string) => (kind === 'auto' ? t`Automatic` : kind === 'manual' ? t`Manual` : kind === 'scheduled' ? t`Scheduled` : kind)

  const [retention, setRetention] = useState<number>(5)
  const [scheduled, setScheduled] = useState(false)
  const [target, setTarget] = useState<RestoreTarget | null>(null)
  const [deleting, setDeleting] = useState<string | null>(null)

  // Named, so the restore sentence extracts as `<0>{backupName}</0>` instead of an anonymous slot.
  const backupName =
    target?.kind === 'upload' ? target.file.name : target?.kind === 'existing' ? target.name : ''

  useEffect(() => {
    if (retentionSettings) {
      setRetention(retentionSettings.retention)
      setScheduled(retentionSettings.scheduled)
    }
  }, [retentionSettings])

  const retentionDirty =
    retentionSettings !== undefined &&
    (Number(retention) !== retentionSettings.retention || scheduled !== retentionSettings.scheduled)

  const restarting = () =>
    notifications.show({
      title: now`Restore staged`,
      message: now`Maki is restarting to apply it. Reload in a moment.`,
      color: 'var(--info)',
      autoClose: false,
    })

  const confirmRestore = () => {
    if (!target) return
    const onSuccess = () => {
      setTarget(null)
      restarting()
    }
    const onError = (e: Error) =>
      notifications.show({ title: now`Restore failed`, message: e.message, color: 'var(--danger)' })

    if (target.kind === 'existing') restore.mutate(target.name, { onSuccess, onError })
    else upload.mutate(target.file, { onSuccess, onError })
  }

  return (
    <SettingsSection
      id="backup"
      title={<Trans>Backup &amp; restore</Trans>}
      description={
        <Trans>
          The database and <Code>config.json</Code>, without manga files or covers.
        </Trans>
      }
      dirty={retentionDirty}
      saving={saveRetention.isPending}
      onDiscard={() => {
        if (!retentionSettings) return
        setRetention(retentionSettings.retention)
        setScheduled(retentionSettings.scheduled)
      }}
      onSave={() =>
        saveRetention.mutate(
          { retention: Number(retention), scheduled },
          { onSuccess: () => notifications.show({ message: now`Saved`, color: 'var(--ok)' }) },
        )
      }
    >
      <Stack>
        <Note>
          <Trans>
            Backups hold API keys and passwords in plain text. Treat a downloaded one like a
            password.
          </Trans>
        </Note>

        {backups && backups.length > 0 && (
          <Table.ScrollContainer minWidth={520}>
            <Table className="panel-table ops-table">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th><Trans>Created</Trans></Table.Th>
                  <Table.Th><Trans>Kind</Trans></Table.Th>
                  <Table.Th><Trans>Version</Trans></Table.Th>
                  <Table.Th><Trans>Size</Trans></Table.Th>
                  <Table.Th><VisuallyHidden><Trans>Actions</Trans></VisuallyHidden></Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {backups.map((b) => (
                  <Table.Tr key={b.name}>
                    <Table.Td className="tnum" style={{ whiteSpace: 'nowrap' }}><TimeAgo value={b.manifest.createdUtc} /></Table.Td>
                    <Table.Td>
                      <TagChip size="sm">{kindLabel(b.manifest.kind)}</TagChip>
                    </Table.Td>
                    <Table.Td className="tnum">
                      <Text size="xs" c="var(--ink-3)">
                        {b.manifest.appVersion}
                      </Text>
                    </Table.Td>
                    <Table.Td className="tnum">{formatBytes(b.sizeBytes)}</Table.Td>
                    <Table.Td>
                      <Group gap={4} justify="flex-end" wrap="nowrap">
                        <Button
                          size="xs"
                          variant="subtle"
                          color="var(--ink-hi)"
                          onClick={() => setTarget({ kind: 'existing', name: b.name })}
                        >
                          <Trans>Restore</Trans>
                        </Button>
                        <ActionIcon
                          variant="subtle"
                          color="var(--neutral)"
                          onClick={() => void downloadBackup(b.name)}
                          aria-label={t`Download backup`}
                        >
                          <IconDownload size={16} />
                        </ActionIcon>
                        <ActionIcon
                          variant="subtle"
                          color="var(--danger)"
                          onClick={() => setDeleting(b.name)}
                          aria-label={t`Delete backup`}
                        >
                          <IconTrash size={16} />
                        </ActionIcon>
                      </Group>
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        )}

        <Group>
          <Button
            onClick={() =>
              create.mutate(undefined, {
                onSuccess: () => notifications.show({ message: now`Backup created`, color: 'var(--ok)' }),
              })
            }
            loading={create.isPending}
          >
            <Trans>Back up now</Trans>
          </Button>
          <FileButton onChange={(f) => f && setTarget({ kind: 'upload', file: f })} accept=".zip">
            {(props) => (
              <Button {...props} variant="default" leftSection={<IconUpload size={16} />}>
                <Trans>Restore from file…</Trans>
              </Button>
            )}
          </FileButton>
        </Group>

        <Switch
          label={t`Back up on a schedule`}
          description={t`Follows the backup freshness set on the Health page.`}
          checked={scheduled}
          onChange={(e) => setScheduled(e.currentTarget.checked)}
        />

        <SettingsNumberInput
          label={t`Backups to keep`}
          description={t`Per kind: automatic, manual and scheduled.`}
          min={1}
          max={50}
          value={retention}
          onChange={setRetention}
        />
      </Stack>

      <ConfirmDialog
        opened={target !== null}
        onClose={() => setTarget(null)}
        title={t`Restore backup`}
        description={
          <Trans>
            This replaces your current library and settings with <b>{backupName}</b>, then restarts
            Maki. The current database is kept for 7 days as maki.db.pre-restore in the config folder,
            but putting it back is done by hand, so take a backup first if you want an easy way back.
            Docker and systemd bring Maki back up on their own; otherwise start it again yourself.
          </Trans>
        }
        confirmLabel={<Trans>Restore &amp; restart</Trans>}
        loading={restore.isPending || upload.isPending}
        onConfirm={confirmRestore}
      >
        <Trans>
          Accounts are restored too, exactly as they were in that backup. A password you changed or a
          single sign-on link you removed since then comes back, so redo those afterwards. API keys you
          revoked since the backup stay revoked.
        </Trans>
      </ConfirmDialog>

      <ConfirmDialog
        opened={deleting !== null}
        onClose={() => setDeleting(null)}
        title={t`Delete backup`}
        confirmLabel={<Trans>Delete backup</Trans>}
        loading={remove.isPending}
        onConfirm={() => deleting && remove.mutate(deleting, { onSuccess: () => setDeleting(null) })}
      >
        <Trans>
          <b>{deleting}</b> is removed from disk. This can't be undone.
        </Trans>
      </ConfirmDialog>
    </SettingsSection>
  )
}


/**
 * GitHub release bodies are markdown and the project ships no renderer, so this handles the three
 * shapes release notes actually use (headings, bullets, plain lines) and shows everything else
 * as written. Always text nodes, never HTML.
 */
function ReleaseNotes({ notes }: { notes: string }) {
  const [open, setOpen] = useState(false)
  const lines = notes.replace(/\r\n?/g, '\n').trim().split('\n')

  return (
    <Stack gap={6}>
      <UnstyledButton
        fz="sm"
        c="var(--brand-fg)"
        aria-expanded={open}
        onClick={() => setOpen((v) => !v)}
      >
        <Group gap={4} wrap="nowrap">
          {open ? <IconChevronDown size={16} /> : <IconChevronRight size={16} />}
          <Trans>What's new</Trans>
        </Group>
      </UnstyledButton>
      <Collapse expanded={open}>
        <ScrollArea.Autosize mah={280} type="auto" offsetScrollbars>
          <Stack gap={4}>
            {lines.map((line, i) => {
              const heading = /^\s*#{1,6}\s+(.*)$/.exec(line)
              if (heading) {
                return (
                  <Text key={i} size="sm" fw={600} mt={i === 0 ? 0 : 6}>
                    {heading[1]}
                  </Text>
                )
              }
              const bullet = /^\s*[-*]\s+(.*)$/.exec(line)
              if (bullet) {
                return (
                  <Box key={i} component="ul" m={0} pl="lg">
                    <Text component="li" size="sm" c="var(--ink-3)" style={{ whiteSpace: 'pre-wrap', minWidth: 0 }}>
                      {bullet[1]}
                    </Text>
                  </Box>
                )
              }
              if (!line.trim()) return null
              return (
                <Text key={i} size="sm" c="var(--ink-3)" style={{ whiteSpace: 'pre-wrap' }}>
                  {line}
                </Text>
              )
            })}
          </Stack>
        </ScrollArea.Autosize>
      </Collapse>
    </Stack>
  )
}

export function UpdatesSection() {
  const { t } = useLingui()
  const { data: settings } = useUpdateSettings()
  const save = useSaveUpdateSettings()
  const { data: status } = useUpdateStatus()
  const checkNow = useCheckForUpdatesNow()
  const latestVersion = status?.latestVersion
  const skippedVersion = useSyncExternalStore(subscribeSkippedVersion, getSkippedVersion)
  const isSkipped = !!status?.updateAvailable && !!latestVersion && skippedVersion === latestVersion
  const applyUpdate = useApplyUpdate()
  const checkedAtLabel = status?.checkedAt ? formatDateTime(status.checkedAt) : undefined
  const desktop = status?.runtime === 'desktop'
  const howToUpdate = status?.isDocker
    ? t`pull the new image and recreate the container`
    : t`pull the latest code and rebuild`

  return (
    <SettingsSection
      id="updates"
      title={<Trans>Updates</Trans>}
      description={
        desktop ? (
          <Trans>Maki downloads updates in the background and installs them when you restart it.</Trans>
        ) : (
          <Trans>Updating is manual: {howToUpdate}.</Trans>
        )
      }
    >
      <Stack gap="sm">
        <Switch
          label={t`Check for updates`}
          checked={settings?.checkForUpdates ?? true}
          onChange={(e) => save.mutate(e.currentTarget.checked)}
        />
        <Group justify="space-between">
          <Text size="sm" c="var(--ink-3)">
            {status?.isDevBuild ? (
              <Trans>Unofficial build, update checks are skipped.</Trans>
            ) : status?.updateAvailable ? (
              <Trans>Update available: {latestVersion}</Trans>
            ) : checkedAtLabel ? (
              <Trans>Up to date, last checked {checkedAtLabel}</Trans>
            ) : (
              <Trans>Not checked yet</Trans>
            )}
          </Text>
          <Button
            variant="default"
            size="xs"
            loading={checkNow.isPending}
            disabled={status?.isDevBuild}
            onClick={() =>
              checkNow.mutate(undefined, {
                onSuccess: (r) => {
                  const checkedVersion = r.latestVersion ?? ''
                  notifications.show({
                    message: r.updateAvailable
                      ? now`Maki ${checkedVersion} is available`
                      : now`Already up to date`,
                    color: r.updateAvailable ? 'var(--warn)' : 'var(--ok)',
                  })
                },
              })
            }
          >
            <Trans>Check now</Trans>
          </Button>
        </Group>
        {desktop && status?.updateAvailable && !status.isDevBuild && (
          <Group justify="space-between">
            <Text size="sm" c="var(--ink-3)">
              {status.updateReady ? (
                <Trans>The update is downloaded. Maki is offline for about a minute while it installs.</Trans>
              ) : (
                <Trans>Downloading the update in the background.</Trans>
              )}
            </Text>
            <Button
              size="xs"
              disabled={!status.updateReady}
              loading={applyUpdate.isPending}
              onClick={() =>
                applyUpdate.mutate(undefined, {
                  onSuccess: () =>
                    notifications.show({
                      message: now`Installing the update. This page reconnects when Maki is back.`,
                      color: 'var(--ok)',
                    }),
                })
              }
            >
              <Trans>Restart to update</Trans>
            </Button>
          </Group>
        )}
        {status?.updateAvailable && !status.isDevBuild && status.releaseNotes?.trim() && (
          <ReleaseNotes notes={status.releaseNotes} />
        )}
        {isSkipped && (
          <Group gap={6}>
            <Text size="xs" c="dimmed">
              <Trans>Skipped {latestVersion}</Trans>
            </Text>
            <UnstyledButton
              fz="xs"
              c="var(--brand-fg)"
              td="underline"
              onClick={() => setSkippedVersion(null)}
            >
              <Trans>Show again</Trans>
            </UnstyledButton>
          </Group>
        )}
      </Stack>
    </SettingsSection>
  )
}

/**
 * The images Maki keeps on disk, and the one button that rebuilds them.
 *
 * Two different kinds of file behind one card: reader page thumbnails, which any request
 * regenerates on demand and so are only ever deleted, and series posters, which nothing regenerates
 * on its own: a poster lost to a failed download stays missing until something re-fetches it.
 * "Rebuild missing" is therefore the useful button most of the time; the forced pass exists for
 * artwork that is stale rather than broken, and costs a provider lookup and a download per series.
 */
export function ImageCacheSection() {
  const { t } = useLingui()
  const [awaitingStart, setAwaitingStart] = useState(false)
  const { data } = useImageCache(awaitingStart)
  const rebuild = useRebuildImageCache()
  const [confirmForce, setConfirmForce] = useState(false)

  const status = data?.status
  const usage = data?.usage
  const running = status?.running ?? false
  const pct =
    running && status && status.total > 0
      ? Math.min(100, Math.round((status.processed / status.total) * 100))
      : null

  const coverFileCount = usage?.coverFiles ?? 0
  const thumbnailFileCount = usage?.thumbnailFiles ?? 0
  const seriesTotalCount = usage?.seriesTotal ?? 0
  const coverBytesLabel = usage ? formatBytes(usage.coverBytes) : undefined
  const coversMissingLabel = usage ? formatNumber(usage.coversMissing) : undefined
  const thumbnailBytesLabel = usage ? formatBytes(usage.thumbnailBytes) : undefined

  const lastError = status?.lastError
  const processedCount = status?.processed ?? 0
  const totalCount = status?.total ?? 0
  const finishedAtLabel = status?.finishedAt ? formatDateTime(status.finishedAt) : undefined
  const downloadedCount = status ? formatNumber(status.downloaded) : undefined
  const failedCount = status ? formatNumber(status.failed) : undefined
  const thumbnailsClearedCount = status ? formatNumber(status.thumbnailsCleared) : undefined

  // The job is claimed a moment after the trigger returns, and a small library can be done before
  // the next poll, so the hint is dropped either when the run becomes visible or on a timeout.
  useEffect(() => {
    if (!awaitingStart) return
    if (running) {
      setAwaitingStart(false)
      return
    }
    const timer = window.setTimeout(() => setAwaitingStart(false), 20_000)
    return () => window.clearTimeout(timer)
  }, [awaitingStart, running])

  const start = (force: boolean) =>
    rebuild.mutate(force, {
      onSuccess: (r) => {
        setAwaitingStart(r.started)
        notifications.show({
          message: r.started ? now`Rebuilding image cache` : (r.message ?? now`Already running`),
          color: r.started ? 'var(--ok)' : 'var(--warn)',
        })
      },
    })

  const lastRunValue = running
    ? t`Running`
    : lastError
      ? t`Failed`
      : status?.finishedAt
        ? relativeTime(status.finishedAt, 'narrow')
        : t`Not run yet`

  const lastRunHint = running ? (
    status?.phase === 'clearing' ? (
      <Trans>Clearing cached images...</Trans>
    ) : (
      <Trans>
        Rebuilding posters, {processedCount} of {totalCount}
      </Trans>
    )
  ) : lastError ? (
    lastError
  ) : finishedAtLabel ? (
    <span title={finishedAtLabel}>
      <Trans>
        {downloadedCount} posters downloaded, {failedCount} failed, {thumbnailsClearedCount} cached
        images cleared
      </Trans>
    </span>
  ) : undefined

  return (
    <SettingsSection
      id="image-cache"
      title={<Trans>Image cache</Trans>}
      description={<Trans>Posters and reader thumbnails Maki keeps on disk.</Trans>}
    >
      <FigureStrip
        loading={!usage}
        figures={[
          {
            label: t`Posters`,
            value: coverFileCount,
            hint:
              usage && usage.coversMissing > 0
                ? plural(seriesTotalCount, {
                    one: `${coverBytesLabel}, ${coversMissingLabel} of # series has no usable poster`,
                    other: `${coverBytesLabel}, ${coversMissingLabel} of # series have no usable poster`,
                  })
                : usage && <Trans>{coverBytesLabel}, every series has one</Trans>,
          },
          {
            label: t`Reader thumbnails`,
            value: thumbnailFileCount,
            hint: thumbnailBytesLabel,
          },
          {
            label: t`Last rebuild`,
            value: lastRunValue,
            tone: running ? 'info' : lastError ? 'danger' : undefined,
            zero: !running && !lastError && !status?.finishedAt,
            hint: lastRunHint,
          },
        ]}
      />

      {(running || pct !== null) && (
        <Progress
          mt="sm"
          value={pct ?? 100}
          animated={running}
          striped={running}
          color={status?.lastError ? 'var(--danger)' : undefined}
          aria-label={t`Cache progress`}
        />
      )}

      <Group gap="xs" mt="md">
        <Button
          variant="default"
          size="xs"
          loading={rebuild.isPending}
          disabled={running}
          onClick={() => start(false)}
        >
          <Trans>Rebuild missing</Trans>
        </Button>
        <Button
          variant="default"
          size="xs"
          disabled={running || rebuild.isPending}
          onClick={() => setConfirmForce(true)}
        >
          <Trans>Rebuild all</Trans>
        </Button>
      </Group>

      <Modal
        opened={confirmForce}
        onClose={() => setConfirmForce(false)}
        title={
          <ModalHeading
            title={t`Rebuild every poster`}
            description={plural(seriesTotalCount, {
              one: 'This re-downloads the poster for the # series, one metadata lookup and one image each. Use "Rebuild missing" instead if you are only fixing covers that fail to load.',
              other: 'This re-downloads the poster for all # series, one metadata lookup and one image each. On a large library it runs for several minutes. Use "Rebuild missing" instead if you are only fixing covers that fail to load.',
            })}
          />
        }
      >
        <Stack>
          <ModalFooter>
            <Button variant="default" onClick={() => setConfirmForce(false)}>
              <Trans>Cancel</Trans>
            </Button>
            <Button
              onClick={() => {
                setConfirmForce(false)
                start(true)
              }}
            >
              <Trans>Rebuild all</Trans>
            </Button>
          </ModalFooter>
        </Stack>
      </Modal>
    </SettingsSection>
  )
}

/**
 * When the health scans run and where the disk and backup checks draw their lines. The checks and
 * findings themselves live on the Health page; this is only how they are scheduled and judged.
 */
export function HealthSettingsSection() {
  const { t } = useLingui()
  const qc = useQueryClient()
  const { data } = useHealthData<HealthOptions>('/options')
  const save = useHealthAction()
  const [draft, setDraft] = useState<HealthOptions | null>(null)
  const value = draft ?? data
  const update = (patch: Partial<HealthOptions>) => value && setDraft({ ...value, ...patch })

  return (
    <SettingsSection
      id="health"
      title={<Trans>Library health</Trans>}
      dirty={draft !== null}
      saving={save.isPending}
      onDiscard={() => setDraft(null)}
      onSave={() =>
        value &&
        save.mutate(
          { path: '/options', method: 'PUT', body: value },
          {
            onSuccess: () => {
              qc.setQueryData(['health-workspace', '/options'], value)
              setDraft(null)
              notifications.show({ message: now`Saved`, color: 'var(--ok)' })
            },
          },
        )
      }
    >
      {save.error && (
        <Callout tone="danger" role="alert" mb="md">
          {save.error.message}
        </Callout>
      )}
      {value && (
        <Stack gap="lg">
          <Switch
            label={t`Analyze new files and run daily reconciliation`}
            checked={value.automaticScanning}
            onChange={(e) => update({ automaticScanning: e.currentTarget.checked })}
          />
          <SimpleGrid cols={{ base: 1, sm: 3 }}>
            <SettingsNumberInput
              label={t`Daily scan hour`}
              description={t`0 to 23.`}
              min={0}
              max={23}
              value={value.scanHour}
              onChange={(v) => update({ scanHour: v })}
            />
            <TextInput
              label={t`Timezone`}
              description={t`Empty uses the server's timezone.`}
              value={value.timeZone ?? ''}
              onChange={(e) => update({ timeZone: e.currentTarget.value || null })}
            />
            <SettingsNumberInput
              label={t`Pages decoded at once`}
              description={t`0 picks a number for this machine.`}
              min={0}
              max={32}
              value={value.scanWorkers}
              onChange={(v) => update({ scanWorkers: v })}
            />
          </SimpleGrid>
          <Stack gap="xs">
            <Text size="sm" fw={600}>
              <Trans>Thresholds</Trans>
            </Text>
            <SimpleGrid cols={{ base: 1, sm: 3 }}>
              {(['warningPercent', 'errorPercent', 'backupDays', 'warningGiB', 'errorGiB'] as const).map((key) => (
                <SettingsNumberInput
                  key={key}
                  label={
                    {
                      warningPercent: t`Low disk warning (%)`,
                      errorPercent: t`Low disk error (%)`,
                      warningGiB: t`Low disk warning (GiB)`,
                      errorGiB: t`Low disk error (GiB)`,
                      backupDays: t`Backup freshness (days)`,
                    }[key]
                  }
                  min={0}
                  value={value[key]}
                  onChange={(v) => update({ [key]: v })}
                />
              ))}
            </SimpleGrid>
          </Stack>
        </Stack>
      )}
    </SettingsSection>
  )
}
