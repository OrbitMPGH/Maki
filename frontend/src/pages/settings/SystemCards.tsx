import { useEffect, useState, useSyncExternalStore } from 'react'
import { getSkippedVersion, setSkippedVersion, subscribeSkippedVersion } from '../../lib/updateSkip'
import { Trans, useLingui } from '@lingui/react/macro'
import { t as now } from '@lingui/core/macro'
import {
  ActionIcon,
  Alert,
  Badge,
  Button,
  Code,
  FileButton,
  Group,
  Modal,
  Progress,
  Stack,
  Switch,
  Table,
  Text,
  UnstyledButton,
} from '@mantine/core'
import { IconAlertTriangle, IconDownload, IconTrash, IconUpload } from '@tabler/icons-react'
import { notifications } from '@mantine/notifications'
import { ConfirmDialog } from '../../components/ui/ConfirmDialog'
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
  useCheckForUpdatesNow,
  useImageCache,
  useRebuildImageCache,
  useSaveUpdateSettings,
  useUpdateSettings,
  useUpdateStatus,
} from '../../api/hooks'
import { formatBytes, formatDateTime, formatNumber } from '../../format'
import { SettingsNumberInput } from '../../components/settings/SettingsNumberInput'

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

  const kindLabel = (kind: string) => (kind === 'auto' ? t`Automatic` : kind === 'manual' ? t`Manual` : kind)

  const [retention, setRetention] = useState<number>(5)
  const [target, setTarget] = useState<RestoreTarget | null>(null)
  const [deleting, setDeleting] = useState<string | null>(null)

  // Named, so the restore sentence extracts as `<0>{backupName}</0>` instead of an anonymous slot.
  const backupName =
    target?.kind === 'upload' ? target.file.name : target?.kind === 'existing' ? target.name : ''

  useEffect(() => {
    if (retentionSettings) setRetention(retentionSettings.retention)
  }, [retentionSettings])

  const retentionDirty =
    retentionSettings !== undefined && Number(retention) !== retentionSettings.retention

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
          A zip of the database and <Code>config.json</Code>: every series record, reading history
          and setting, but not the manga files, the MangaBaka copy or covers. One is taken
          automatically before an upgrade migration. There is no schedule, so take one yourself
          before big changes.
        </Trans>
      }
      dirty={retentionDirty}
      saving={saveRetention.isPending}
      onDiscard={() => retentionSettings && setRetention(retentionSettings.retention)}
      onSave={() =>
        saveRetention.mutate(
          { retention: Number(retention) },
          { onSuccess: () => notifications.show({ message: now`Saved`, color: 'var(--ok)' }) },
        )
      }
    >
      <Alert color="var(--warn)" icon={<IconAlertTriangle size={16} />} mb="md" variant="light">
        <Trans>
          Backups hold API keys and passwords in plain text. Treat a downloaded one like a
          password.
        </Trans>
      </Alert>

      <Stack>
        {backups && backups.length > 0 && (
          <Table.ScrollContainer minWidth={520}>
            <Table className="panel-table ops-table">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th><Trans>Created</Trans></Table.Th>
                  <Table.Th><Trans>Kind</Trans></Table.Th>
                  <Table.Th><Trans>Version</Trans></Table.Th>
                  <Table.Th><Trans>Size</Trans></Table.Th>
                  <Table.Th />
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {backups.map((b) => (
                  <Table.Tr key={b.name}>
                    <Table.Td style={{ whiteSpace: 'nowrap' }}>{formatDateTime(b.manifest.createdUtc)}</Table.Td>
                    <Table.Td>
                      <Badge size="sm" variant="light" color={b.manifest.kind === 'auto' ? 'var(--neutral)' : 'var(--info)'}>
                        {kindLabel(b.manifest.kind)}
                      </Badge>
                    </Table.Td>
                    <Table.Td>
                      <Text size="xs" c="var(--ink-3)">
                        {b.manifest.appVersion}
                      </Text>
                    </Table.Td>
                    <Table.Td>{formatBytes(b.sizeBytes)}</Table.Td>
                    <Table.Td>
                      <Group gap="xs" justify="flex-end" wrap="nowrap">
                        <Button
                          size="xs"
                          variant="light"
                          onClick={() => setTarget({ kind: 'existing', name: b.name })}
                        >
                          <Trans>Restore</Trans>
                        </Button>
                        <ActionIcon
                          variant="subtle"
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

        <SettingsNumberInput
          label={t`Backups to keep`}
          description={t`Per kind: the newest N automatic and the newest N manual backups stay. Older ones are removed when a new backup is taken.`}
          min={1}
          max={50}
          value={retention}
          onChange={setRetention}
          w={220}
        />
      </Stack>

      <Modal opened={target !== null} onClose={() => setTarget(null)} title={t`Restore backup`} centered>
        <Stack>
          <Text size="sm">
            <Trans>
              This replaces your current library and settings with <b>{backupName}</b>, then restarts
              Maki. The current data is not kept, so take a backup first if you want a way back.
              Docker and systemd bring Maki back up on their own; otherwise start it again yourself.
            </Trans>
          </Text>
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setTarget(null)}>
              <Trans>Cancel</Trans>
            </Button>
            <Button color="var(--danger-fill)" loading={restore.isPending || upload.isPending} onClick={confirmRestore}>
              <Trans>Restore &amp; restart</Trans>
            </Button>
          </Group>
        </Stack>
      </Modal>

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


export function UpdatesSection() {
  const { t } = useLingui()
  const { data: settings } = useUpdateSettings()
  const save = useSaveUpdateSettings()
  const { data: status } = useUpdateStatus()
  const checkNow = useCheckForUpdatesNow()
  const latestVersion = status?.latestVersion
  const skippedVersion = useSyncExternalStore(subscribeSkippedVersion, getSkippedVersion)
  const isSkipped = !!status?.updateAvailable && !!latestVersion && skippedVersion === latestVersion
  const checkedAtLabel = status?.checkedAt ? formatDateTime(status.checkedAt) : undefined
  const howToUpdate = status?.isDocker
    ? t`pull the new image and recreate the container`
    : t`pull the latest code and rebuild`

  return (
    <SettingsSection
      id="updates"
      title={<Trans>Updates</Trans>}
      description={
        <Trans>
          Checks GitHub daily for a new release and shows a card in the sidebar and a notification
          when there is one. Updating is manual: {howToUpdate}.
        </Trans>
      }
    >
      <Stack gap="sm">
        <Switch
          label={t`Check for updates`}
          description={t`Once a day. Check now works either way.`}
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

  const coverFilesLabel = usage ? formatNumber(usage.coverFiles) : undefined
  const coverBytesLabel = usage ? formatBytes(usage.coverBytes) : undefined
  const coversMissingLabel = usage ? formatNumber(usage.coversMissing) : undefined
  const seriesTotalLabel = usage ? formatNumber(usage.seriesTotal) : undefined
  const thumbnailFilesLabel = usage ? formatNumber(usage.thumbnailFiles) : undefined
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
      onError: (e) => notifications.show({ message: String(e), color: 'var(--danger)' }),
    })

  return (
    <SettingsSection
      id="image-cache"
      title={<Trans>Image cache</Trans>}
      description={
        <Trans>
          Posters, reader thumbnails and the page samples used to compare sources. Both buttons
          clear thumbnails and samples and remove posters of deleted series. Rebuild missing then
          re-downloads posters that are missing or broken; Rebuild all re-downloads every poster.
        </Trans>
      }
    >
      {usage && (
        <Stack gap={4} mb="md">
          <Text size="sm" c="var(--ink-3)">
            {usage.coversMissing > 0 ? (
              <Trans>
                Posters: {coverFilesLabel} files, {coverBytesLabel} - {coversMissingLabel} of{' '}
                {seriesTotalLabel} series have no usable poster
              </Trans>
            ) : (
              <Trans>
                Posters: {coverFilesLabel} files, {coverBytesLabel} - every series has one
              </Trans>
            )}
          </Text>
          <Text size="sm" c="var(--ink-3)">
            <Trans>
              Reader thumbnails: {thumbnailFilesLabel} files, {thumbnailBytesLabel}
            </Trans>
          </Text>
        </Stack>
      )}

      {(running || pct !== null) && (
        <Progress
          mb="sm"
          value={pct ?? 100}
          animated={running}
          striped={running}
          color={status?.lastError ? 'var(--danger)' : 'brand'}
        />
      )}

      <Group justify="space-between">
        <Text size="sm">
          {running ? (
            status?.phase === 'clearing' ? (
              <Trans>Clearing cached images...</Trans>
            ) : (
              <Trans>
                Rebuilding posters, {processedCount} of {totalCount}
              </Trans>
            )
          ) : lastError ? (
            <Trans>Last run failed: {lastError}</Trans>
          ) : finishedAtLabel ? (
            <Trans>
              Last run {finishedAtLabel}: {downloadedCount} posters downloaded, {failedCount}{' '}
              failed, {thumbnailsClearedCount} cached images cleared
            </Trans>
          ) : (
            <Trans>Not run yet</Trans>
          )}
        </Text>
        <Group gap="xs">
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
      </Group>

      <Modal
        opened={confirmForce}
        onClose={() => setConfirmForce(false)}
        title={t`Rebuild every poster`}
        centered
      >
        <Stack>
          <Text size="sm">
            <Trans>
              This re-downloads the poster for all {seriesTotalLabel} series, one metadata lookup
              and one image each. On a large library it runs for several minutes. Use &quot;Rebuild
              missing&quot; instead if you are only fixing covers that fail to load.
            </Trans>
          </Text>
          <Group justify="flex-end">
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
          </Group>
        </Stack>
      </Modal>
    </SettingsSection>
  )
}

