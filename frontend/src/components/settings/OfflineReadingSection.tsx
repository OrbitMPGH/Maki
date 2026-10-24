import { useEffect, useMemo, useState } from 'react'
import { ActionIcon, Alert, Anchor, Badge, Button, Group, Progress, Select, Stack, Text, Tooltip } from '@mantine/core'
import { IconDeviceMobileOff, IconRefresh, IconTrash } from '@tabler/icons-react'
import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { plural } from '@lingui/core/macro'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { api } from '../../api/client'
import type { ChapterDto, SeriesDto } from '../../api/types'
import { useAuth } from '../../auth/AuthProvider'
import { formatBytes } from '../../format'
import { useConnectivity } from '../../lib/connectivity'
import { iosNeedsInstall } from '../../lib/pwa/push'
import { pwaSupported } from '../../lib/pwa/serviceWorker'
import { estimate, isPersisted, requestPersistence, type StorageEstimate } from '../../lib/pwa/storage'
import { SettingsSection } from '../../pages/settings/SettingsSection'
import type { OfflineChapter } from '../../pwa/contract'
import {
  AUTO_KEEP_CHOICES,
  getAutoKeep,
  removeChaptersOffline,
  removeEverythingOffline,
  removeSeriesOffline,
  saveChaptersOffline,
  setAutoKeep,
  useOfflineChapters,
  type SaveProgress,
} from '../../pwa/offlineChapters'
import { queuedProgressCount, replayProgress } from '../../pwa/progressQueue'
import { chapterLabel } from '../series/chapterTable'
import { ConfirmDialog } from '../ui/ConfirmDialog'
import { SecureContextNote } from './SecureContextNote'

export function OfflineReadingSection() {
  return (
    <SettingsSection
      id="offline-reading"
      title={<Trans>Offline reading</Trans>}
      description={<Trans>Chapters saved on this device, to read without a connection.</Trans>}
    >
      {pwaSupported() ? <OfflineControls /> : <SecureContextNote />}
    </SettingsSection>
  )
}

function OfflineControls() {
  const { t } = useLingui()
  const { me } = useAuth()
  const connectivity = useConnectivity()
  const { records, progress } = useOfflineChapters()
  const [usage, setUsage] = useState<StorageEstimate | null>(null)
  const [persisted, setPersisted] = useState<boolean | null>(null)
  const [keep, setKeep] = useState<number | null>(null)
  const [confirmOpen, setConfirmOpen] = useState(false)
  const [unsent, setUnsent] = useState(0)
  const [removing, setRemoving] = useState(false)
  const userId = me?.id ?? null

  // Re-measured whenever a chapter lands or goes, not on every page of a download.
  useEffect(() => {
    void estimate().then(setUsage)
    void isPersisted().then(setPersisted)
  }, [records])

  useEffect(() => {
    if (userId !== null) void getAutoKeep(userId).then(setKeep)
  }, [userId])

  const { data: series } = useQuery({
    queryKey: ['series'],
    queryFn: () => api<SeriesDto[]>('/series'),
    enabled: records.size > 0,
    meta: { silent: true },
  })
  const titles = useMemo(() => new Map((series ?? []).map((s) => [s.id, s.displayTitle])), [series])

  const groups = useMemo(() => {
    const bySeries = new Map<number, OfflineChapter[]>()
    for (const record of records.values()) {
      const list = bySeries.get(record.seriesId) ?? []
      list.push(record)
      bySeries.set(record.seriesId, list)
    }
    return [...bySeries.entries()].sort(([a], [b]) =>
      (titles.get(a) ?? '').localeCompare(titles.get(b) ?? ''),
    )
  }, [records, titles])

  const keepOptions = AUTO_KEEP_CHOICES.map((count) => ({
    value: String(count),
    label:
      count === 0
        ? t`Off`
        : plural(count, { one: 'The next unread chapter', other: 'The next # unread chapters' }),
  }))

  async function openRemoveAll() {
    if (userId === null) return
    if (connectivity === 'online') await replayProgress()
    setUnsent(await queuedProgressCount(userId))
    setConfirmOpen(true)
  }

  async function removeAll() {
    setRemoving(true)
    try {
      await removeEverythingOffline()
    } finally {
      setRemoving(false)
      setConfirmOpen(false)
    }
  }

  const used = usage ? formatBytes(usage.usage) : null
  const quota = usage ? formatBytes(usage.quota) : null

  return (
    <Stack gap="lg">
      {iosNeedsInstall() && (
        <Alert color="var(--info)" variant="light">
          <Trans>
            Safari clears what a website saved after seven days without a visit. Add Maki to your home
            screen and save chapters from there to keep them.
          </Trans>
        </Alert>
      )}

      <Stack gap={6}>
        <Text fw={500} size="sm">
          <Trans>Storage on this device</Trans>
        </Text>
        {usage && usage.quota > 0 && (
          <Progress
            value={Math.min(100, (usage.usage / usage.quota) * 100)}
            size="sm"
            radius="xl"
            aria-label={t`Storage used`}
          />
        )}
        {used && quota && (
          <Text size="xs" c="var(--ink-3)" className="tnum">
            <Trans>
              {used} of {quota} used
            </Trans>
          </Text>
        )}
        <Group gap="xs" wrap="wrap">
          <Text size="xs" c="var(--ink-3)">
            {persisted ? (
              <Trans>The browser keeps saved chapters even when space runs low.</Trans>
            ) : (
              <Trans>The browser may clear saved chapters when the device runs low on space.</Trans>
            )}
          </Text>
          {persisted === false && (
            <Button
              size="compact-xs"
              variant="subtle"
              onClick={() => void requestPersistence().then(setPersisted)}
            >
              <Trans>Ask to keep them</Trans>
            </Button>
          )}
        </Group>
      </Stack>

      <Select
        label={t`Keep upcoming chapters`}
        description={t`For each series you are reading, saves the next unread chapters while online and removes them once read. Chapters you save yourself stay until you remove them.`}
        data={keepOptions}
        value={keep === null ? null : String(keep)}
        disabled={keep === null}
        allowDeselect={false}
        onChange={(value) => {
          const count = Number(value ?? 0)
          setKeep(count)
          void setAutoKeep(count)
        }}
        comboboxProps={{ withinPortal: true }}
      />

      <Stack gap="xs">
        <Text fw={500} size="sm">
          <Trans>Saved chapters</Trans>
        </Text>
        {groups.length === 0 ? (
          <Text size="sm" c="var(--ink-3)">
            <Trans>Nothing is saved on this device yet. Use Save for offline on a chapter or a series.</Trans>
          </Text>
        ) : (
          groups.map(([seriesId, list]) => (
            <SavedSeriesGroup
              key={seriesId}
              seriesId={seriesId}
              title={titles.get(seriesId) ?? null}
              records={list}
              progress={progress}
            />
          ))
        )}
      </Stack>

      <Stack gap="xs">
        <Text size="xs" c="var(--ink-3)">
          <Trans>
            Signing out keeps saved chapters on this device. They come back when you sign in again.
          </Trans>
        </Text>
        <Group>
          <Button
            variant="light"
            color="var(--danger)"
            leftSection={<IconDeviceMobileOff size={16} />}
            onClick={() => void openRemoveAll()}
          >
            <Trans>Remove everything from this device</Trans>
          </Button>
        </Group>
      </Stack>

      <ConfirmDialog
        opened={confirmOpen}
        onClose={() => setConfirmOpen(false)}
        title={<Trans>Remove everything from this device?</Trans>}
        confirmLabel={<Trans>Remove everything</Trans>}
        loading={removing}
        onConfirm={() => void removeAll()}
      >
        <Stack gap="xs">
          <Text size="sm">
            <Trans>
              Saved chapters, the copies of pages kept for browsing offline and this device's
              settings for offline reading all go. Nothing changes on the server.
            </Trans>
          </Text>
          {unsent > 0 && (
            <Text size="sm" c="var(--danger)">
              <Plural
                value={unsent}
                one="# reading position has not reached the server yet and will be lost."
                other="# reading positions have not reached the server yet and will be lost."
              />
            </Text>
          )}
        </Stack>
      </ConfirmDialog>
    </Stack>
  )
}

function SavedSeriesGroup({
  seriesId,
  title,
  records,
  progress,
}: {
  seriesId: number
  title: string | null
  records: OfflineChapter[]
  progress: ReadonlyMap<number, SaveProgress>
}) {
  const { t, i18n } = useLingui()
  const { data: chapters } = useQuery({
    queryKey: ['chapters', seriesId],
    queryFn: () => api<ChapterDto[]>(`/chapter?seriesId=${seriesId}`),
    meta: { silent: true },
  })
  const order = useMemo(() => {
    const map = new Map<number, { index: number; label: string }>()
    ;(chapters ?? []).forEach((c, index) => map.set(c.id, { index, label: chapterLabel(c) }))
    return map
  }, [chapters, i18n.locale])
  const sorted = [...records].sort(
    (a, b) => (order.get(a.chapterId)?.index ?? a.chapterId) - (order.get(b.chapterId)?.index ?? b.chapterId),
  )
  const bytes = records.reduce((sum, r) => sum + r.bytes, 0)
  const size = formatBytes(bytes)
  const seriesTitle = title ?? t`Series ${seriesId}`

  return (
    <Stack gap={4}>
      <Group justify="space-between" wrap="nowrap" gap="xs">
        <Group gap="xs" wrap="nowrap" style={{ minWidth: 0 }}>
          <Anchor component={Link} to={`/series/${seriesId}`} size="sm" fw={600} truncate>
            {seriesTitle}
          </Anchor>
          <Text size="xs" c="var(--ink-3)" className="tnum" style={{ flexShrink: 0 }}>
            {size}
          </Text>
        </Group>
        <Tooltip label={t`Remove this series from the device`} withArrow>
          <ActionIcon
            variant="subtle"
            color="var(--danger)"
            onClick={() => void removeSeriesOffline(seriesId)}
            aria-label={t`Remove ${seriesTitle} from this device`}
          >
            <IconTrash size={16} />
          </ActionIcon>
        </Tooltip>
      </Group>
      {sorted.map((record) => {
        const label = order.get(record.chapterId)?.label ?? t`Chapter ${record.chapterId}`
        const saving = progress.get(record.chapterId)
        return (
          <Group key={record.chapterId} justify="space-between" wrap="nowrap" gap="xs" pl="sm">
            <Group gap="xs" wrap="nowrap" style={{ minWidth: 0 }}>
              <Text size="sm" truncate className="tnum">
                {label}
              </Text>
              {record.reason === 'auto' && (
                <Tooltip label={t`Saved by Keep upcoming chapters, removed once read`} withArrow>
                  <Badge size="xs" variant="light" color="var(--neutral)">
                    <Trans>Automatic</Trans>
                  </Badge>
                </Tooltip>
              )}
            </Group>
            <Group gap={6} wrap="nowrap" style={{ flexShrink: 0 }}>
              <OfflineStateText record={record} saving={saving} />
              {record.state === 'error' && (
                <Tooltip label={t`Try again`} withArrow>
                  <ActionIcon
                    variant="subtle"
                    color="var(--neutral)"
                    onClick={() => void saveChaptersOffline([{ chapterId: record.chapterId, seriesId }])}
                    aria-label={t`Save ${label} again`}
                  >
                    <IconRefresh size={16} />
                  </ActionIcon>
                </Tooltip>
              )}
              <ActionIcon
                variant="subtle"
                color="var(--neutral)"
                onClick={() => void removeChaptersOffline([record.chapterId])}
                aria-label={t`Remove ${label} from this device`}
              >
                <IconTrash size={15} />
              </ActionIcon>
            </Group>
          </Group>
        )
      })}
    </Stack>
  )
}

function OfflineStateText({ record, saving }: { record: OfflineChapter; saving: SaveProgress | undefined }) {
  if (record.state === 'error') {
    return (
      <Text size="xs" c="var(--danger)">
        <Trans>Not saved</Trans>
      </Text>
    )
  }
  if (record.state === 'saving' || record.state === 'stale') {
    const done = saving?.done ?? 0
    const total = saving?.total ?? record.pageCount
    return (
      <Text size="xs" c="var(--info)" className="tnum">
        {total > 0 ? (
          <Trans>
            Saving {done}/{total}
          </Trans>
        ) : (
          <Trans>Waiting</Trans>
        )}
      </Text>
    )
  }
  const size = formatBytes(record.bytes)
  return (
    <Text size="xs" c="var(--ink-3)" className="tnum">
      {size}
    </Text>
  )
}
