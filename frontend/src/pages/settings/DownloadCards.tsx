import { useEffect, useReducer, useState } from 'react'
import { ApiError } from '../../api/client'
import { Trans, Plural, useLingui } from '@lingui/react/macro'
import { t as now } from '@lingui/core/macro'
import { Button, Checkbox, Group, MultiSelect, NumberInput, Select, Stack, Switch, Text } from '@mantine/core'
import { IconAdjustments } from '@tabler/icons-react'
import { notifications } from '@mantine/notifications'
import { SettingsSection } from './SettingsSection'
import { PriorityList } from '../../components/PriorityList'
import { SourceIcon, baseLanguage } from '../../sourceIcons'
import { ManageSourcesModal } from './ManageSourcesModal'
import {
  useUpgradeProfiles,
  useRunUpgradeScan,
  useSaveUpgradeSettings,
  useUpgradeSettings,
} from '../../api/upgrades'
import { savedToast, useSliceSync } from './sharedRecord'
import {
  useConnectionSettings,
  useDownloadSettings,
  useProwlarrIndexers,
  useProwlarrOptions,
  useReadFileCleanupSettings,
  useSaveDownloadSettings,
  useSaveProwlarrOptions,
  useSaveReadFileCleanupSettings,
  useSaveSourceLanguages,
  type SourceOrderMode,
  useSourceLanguages,
  useSourcePriority,
  useSources,
} from '../../api/hooks'
import { ConnectionSettingsCard } from '../../components/ConnectionSettingsCard'
import { SettingsHelp } from '../../components/settings/SettingsHelp'
import { languageName } from '../../api/titles'

export function SourceLanguageSection() {
  const { t } = useLingui()
  const { data: languages } = useSourceLanguages()
  const save = useSaveSourceLanguages()
  const [order, setOrder] = useState<string[] | null>(null)
  const [disabled, setDisabled] = useState<string[] | null>(null)

  useEffect(() => {
    if (languages) {
      setOrder(languages.order)
      setDisabled(languages.disabled)
    }
  }, [languages])

  const key = (list: string[]) => [...list].sort().join(',')
  const dirty =
    order !== null &&
    disabled !== null &&
    languages !== undefined &&
    (order.join(',') !== languages.order.join(',') || key(disabled) !== key(languages.disabled))
  const noneEnabled = order !== null && disabled !== null && order.every((c) => disabled.includes(c))

  return (
    <SettingsSection
      id="languages"
      title={<Trans>Languages</Trans>}
      description={
        <Trans>
          Languages to download, most preferred first. Auto-match tries sources that publish your
          top language first and skips sources that publish none of these. New auto-matched
          sources get these languages; existing mappings are never changed. Drag to reorder.
        </Trans>
      }
      dirty={dirty}
      saving={save.isPending}
      saveDisabled={noneEnabled}
      onDiscard={() => {
        if (!languages) return
        setOrder(languages.order)
        setDisabled(languages.disabled)
      }}
      onSave={() =>
        order &&
        disabled &&
        save.mutate(
          { order, disabled, available: languages?.available ?? [] },
          { onSuccess: () => notifications.show({ message: now`Saved`, color: 'var(--ok)' }) },
        )
      }
    >
      {order && disabled && (
        <PriorityList
          items={order}
          disabled={disabled}
          onChange={(nextOrder, nextDisabled) => {
            setOrder(nextOrder)
            setDisabled(nextDisabled)
          }}
          renderLabel={(code) => languageName(code) ?? code}
          toggleLabel={(code) => {
            const name = languageName(code) ?? code
            return t`Enable ${name}`
          }}
        />
      )}
      {noneEnabled && (
        <Text size="sm" c="var(--danger)" mt="md">
          <Trans>At least one language must stay enabled.</Trans>
        </Text>
      )}
    </SettingsSection>
  )
}

// The server accepts 0 (no limit) or 10 to 1440, so 1 to 9 snaps up to 10.
const MIN_ITEM_TIMEOUT_MINUTES = 10
const clampItemTimeout = (minutes: number) =>
  minutes > 0 && minutes < MIN_ITEM_TIMEOUT_MINUTES ? MIN_ITEM_TIMEOUT_MINUTES : minutes

export function SourcePrioritySection() {
  const { t } = useLingui()
  const { data: sources } = useSources()
  const { data: priority } = useSourcePriority()
  const [managing, setManaging] = useState(false)
  const { data: settings } = useDownloadSettings()
  const save = useSaveDownloadSettings()
  const [sourceOrder, setSourceOrder] = useState<SourceOrderMode>('manual')
  const [scoutOnMatch, setScoutOnMatch] = useState(false)
  const [discarded, discard] = useReducer((n: number) => n + 1, 0)

  useSliceSync(
    settings && { sourceOrder: settings.sourceOrder, scoutOnMatch: settings.scoutOnMatch },
    (slice) => {
      setSourceOrder(slice.sourceOrder)
      setScoutOnMatch(slice.scoutOnMatch)
    },
    discarded,
  )

  const dirty =
    settings !== undefined && (sourceOrder !== settings.sourceOrder || scoutOnMatch !== settings.scoutOnMatch)

  const byName = new Map((sources ?? []).map((s) => [s.name, s]))
  const enabled = priority
    ? priority.order.filter((name) => !priority.disabled.includes(name))
    : (sources ?? []).filter((s) => s.enabled).map((s) => s.name)
  const enabledCount = enabled.length
  const total = sources?.length ?? priority?.order.length ?? 0
  const languageCount = new Set(
    enabled.flatMap((name) => (byName.get(name)?.supportedLanguages ?? []).map(baseLanguage)),
  ).size
  const more = enabledCount - 5

  return (
    <SettingsSection
      id="sources"
      title={<Trans>Sources</Trans>}
      description={
        <Trans>
          Which sites Maki downloads from, and which it tries first. Language ranking above comes
          first. Applies to new auto-matches and manual Auto-match runs; other series keep their
          order. Switching a source off pauses it for every series without touching their own
          toggles.
        </Trans>
      }
      actions={
        <Button size="xs" leftSection={<IconAdjustments size={14} />} onClick={() => setManaging(true)}>
          <Trans>Manage sources</Trans>
        </Button>
      }
      dirty={dirty}
      saving={save.isPending}
      onDiscard={discard}
      onSave={() => save.mutate({ sourceOrder, scoutOnMatch }, { onSuccess: savedToast })}
    >
      <Select
        label={t`Pick a source by`}
        description={t`Which source a chapter is downloaded from first when several have it. Best quality ranks sources by the series' quality profile and what their recent chapters measured, and uses the priority you set to break ties. A series can override this on its Sources tab.`}
        data={[
          { value: 'manual', label: t`Your priority order` },
          { value: 'quality', label: t`Best measured quality` },
        ]}
        value={sourceOrder}
        onChange={(value) => value && setSourceOrder(value as SourceOrderMode)}
        allowDeselect={false}
        mb="sm"
      />
      <Switch
        label={t`Measure sources when a series is added`}
        description={t`Once a new series is matched, sample a few pages from three chapters on each linked source, so its first downloads already come from the best one. Downloads a little from every source for every series you add.`}
        checked={scoutOnMatch}
        onChange={(e) => setScoutOnMatch(e.currentTarget.checked)}
        mb="lg"
      />
      {(sources || priority) && (
        <Group gap={14} wrap="wrap">
          {enabledCount > 0 && (
            <div className="source-stack">
              {enabled.slice(0, 5).map((name) => (
                <SourceIcon key={name} name={name} label={byName.get(name)?.displayName} size={28} />
              ))}
              {more > 0 && <span className="source-stack-more">+{more}</span>}
            </div>
          )}
          <Text size="sm" c="var(--ink-2)">
            <Trans>
              <b>{enabledCount}</b> of <Plural value={total} one="# source" other="# sources" /> enabled
            </Trans>
          </Text>
          <span className="source-summary-sep" />
          <Text size="sm" c="var(--ink-2)">
            <Plural value={languageCount} one="Covers # language" other="Covers # languages" />
          </Text>
        </Group>
      )}
      <ManageSourcesModal opened={managing} onClose={() => setManaging(false)} />
    </SettingsSection>
  )
}

export function DownloadQueueSection() {
  const { t } = useLingui()
  const { data: settings } = useDownloadSettings()
  const save = useSaveDownloadSettings()
  const [concurrentChapters, setConcurrentChapters] = useState<number | string>(2)
  // One number on screen, two fields on the wire: 0 means retry is off, and turning it off keeps the
  // stored cap so switching it back on doesn't forget what it was.
  const [retryAttempts, setRetryAttempts] = useState<number | string>(5)
  const [itemTimeoutMinutes, setItemTimeoutMinutes] = useState<number | string>(120)
  const [bulkHoldThreshold, setBulkHoldThreshold] = useState<number | string>(5)
  const [discarded, discard] = useReducer((n: number) => n + 1, 0)

  useSliceSync(
    settings && {
      concurrentChapters: settings.concurrentChapters,
      retryAttempts: settings.retryEnabled ? settings.retryMaxAttempts : 0,
      itemTimeoutMinutes: settings.itemTimeoutMinutes,
      bulkHoldThreshold: settings.bulkHoldThreshold,
    },
    (slice) => {
      setConcurrentChapters(slice.concurrentChapters)
      setRetryAttempts(slice.retryAttempts)
      setItemTimeoutMinutes(slice.itemTimeoutMinutes)
      setBulkHoldThreshold(slice.bulkHoldThreshold)
    },
    discarded,
  )

  const dirty =
    settings !== undefined &&
    (Number(concurrentChapters) !== settings.concurrentChapters ||
      Number(retryAttempts) !== (settings.retryEnabled ? settings.retryMaxAttempts : 0) ||
      Number(itemTimeoutMinutes) !== settings.itemTimeoutMinutes ||
      Number(bulkHoldThreshold) !== settings.bulkHoldThreshold)

  return (
    <SettingsSection
      id="downloads"
      title={<Trans>Download queue</Trans>}
      description={
        <Trans>
          How chapters from scraper sources move through the queue. Torrents are handled by
          qBittorrent below.
        </Trans>
      }
      dirty={dirty}
      saving={save.isPending}
      onDiscard={discard}
      onSave={() => {
        const retries = Number(retryAttempts)
        save.mutate(
          {
            concurrentChapters: Number(concurrentChapters),
            retryEnabled: retries > 0,
            ...(retries > 0 && { retryMaxAttempts: retries }),
            itemTimeoutMinutes: clampItemTimeout(Number(itemTimeoutMinutes)),
            bulkHoldThreshold: Number(bulkHoldThreshold),
          },
          { onSuccess: savedToast },
        )
      }}
    >
      <NumberInput
        label={t`Concurrent chapter downloads`}
        description={t`More isn't always faster: tripping a site's rate limit pauses that source for a while.`}
        min={1}
        max={8}
        clampBehavior="strict"
        value={concurrentChapters}
        onChange={setConcurrentChapters}
        w={220}
        mb="md"
      />
      <Text fw={500} size="sm" mb={4}>
        <Trans>Retries</Trans>
      </Text>
      <SettingsHelp mb="xs">
        <Trans>
          Failed downloads retry on a growing backoff (5m, 10m, 20m, ...). 0 turns automatic retry
          off. Manual retries from Activity don't count.
        </Trans>
      </SettingsHelp>
      <NumberInput
        label={t`Retry a failed download up to (attempts)`}
        min={0}
        max={20}
        clampBehavior="strict"
        value={retryAttempts}
        onChange={setRetryAttempts}
        w={220}
        mb="md"
      />
      <Text fw={500} size="sm" mb={4}>
        <Trans>Stuck downloads</Trans>
      </Text>
      <SettingsHelp mb="xs">
        <Trans>
          A chapter that never finishes holds a worker and can stall the whole queue. Past this
          many minutes it is marked failed and retried like any other failure. 0 means no limit.
        </Trans>
      </SettingsHelp>
      <NumberInput
        label={t`Give up on a chapter after (minutes)`}
        min={0}
        max={1440}
        clampBehavior="strict"
        value={itemTimeoutMinutes}
        onChange={setItemTimeoutMinutes}
        onBlur={() => setItemTimeoutMinutes(clampItemTimeout(Number(itemTimeoutMinutes)))}
        w={220}
        mb="md"
      />
      <Text fw={500} size="sm" mb={4}>
        <Trans>Bulk new chapters</Trans>
      </Text>
      <SettingsHelp mb="xs">
        <Trans>
          When a refresh finds more new chapters for a series than this, none of them are queued.
          That usually means a source renumbered or backfilled its list, not a real release. They
          stay wanted, so you can download them from the series page. 0 means always queue.
        </Trans>
      </SettingsHelp>
      <NumberInput
        label={t`Don't auto-queue more than (chapters)`}
        min={0}
        max={1000}
        clampBehavior="strict"
        value={bulkHoldThreshold}
        onChange={setBulkHoldThreshold}
        w={220}
      />
    </SettingsSection>
  )
}

export function SmartDownloadSection() {
  const { t } = useLingui()
  const { data: settings } = useDownloadSettings()
  const save = useSaveDownloadSettings()
  const [smartDownloadChaptersLeft, setSmartDownloadChaptersLeft] = useState<number | string>(5)
  const [smartDownloadChapters, setSmartDownloadChapters] = useState<number | string>(10)
  const [discarded, discard] = useReducer((n: number) => n + 1, 0)

  useSliceSync(
    settings && {
      smartDownloadChaptersLeft: settings.smartDownloadChaptersLeft,
      smartDownloadChapters: settings.smartDownloadChapters,
    },
    (slice) => {
      setSmartDownloadChaptersLeft(slice.smartDownloadChaptersLeft)
      setSmartDownloadChapters(slice.smartDownloadChapters)
    },
    discarded,
  )

  const dirty =
    settings !== undefined &&
    (Number(smartDownloadChaptersLeft) !== settings.smartDownloadChaptersLeft ||
      Number(smartDownloadChapters) !== settings.smartDownloadChapters)

  return (
    <SettingsSection
      id="smart-download"
      title={<Trans>Smart Download</Trans>}
      description={
        <Trans>
          Downloads the next chapters of a series when you're down to a few unread. Checks every
          five minutes against progress from Kavita or the built-in reader. Turn it on per series
          in its monitoring options.
        </Trans>
      }
      dirty={dirty}
      saving={save.isPending}
      onDiscard={discard}
      onSave={() =>
        save.mutate(
          {
            smartDownloadChaptersLeft: Number(smartDownloadChaptersLeft),
            smartDownloadChapters: Number(smartDownloadChapters),
          },
          { onSuccess: savedToast },
        )
      }
    >
      <Group align="flex-end">
        <NumberInput
          label={t`Fetch more when unread chapters drop to`}
          description={t`At this many or fewer downloaded chapters ahead of where you are, Maki fetches more.`}
          min={1}
          max={10}
          clampBehavior="strict"
          value={smartDownloadChaptersLeft}
          onChange={setSmartDownloadChaptersLeft}
          w={220}
        />
        <NumberInput
          label={t`Chapters to fetch each time`}
          min={1}
          max={20}
          clampBehavior="strict"
          value={smartDownloadChapters}
          onChange={setSmartDownloadChapters}
          w={220}
        />
      </Group>
    </SettingsSection>
  )
}

export function ReadFileCleanupSection() {
  const { t } = useLingui()
  const { data: settings } = useReadFileCleanupSettings()
  const save = useSaveReadFileCleanupSettings()
  const [enabled, setEnabled] = useState(false)
  const [days, setDays] = useState<number | string>(7)
  const [keepLast, setKeepLast] = useState(true)
  const [discarded, discard] = useReducer((n: number) => n + 1, 0)

  useSliceSync(
    settings,
    (slice) => {
      setEnabled(slice.enabled)
      setDays(slice.days)
      setKeepLast(slice.keepLast)
    },
    discarded,
  )

  const dirty =
    settings !== undefined &&
    (enabled !== settings.enabled || Number(days) !== settings.days || keepLast !== settings.keepLast)

  return (
    <SettingsSection
      id="read-cleanup"
      title={<Trans>Clean up read chapters</Trans>}
      description={
        <Trans>
          Deletes a chapter's file once everyone reading the series has finished it and the days below
          have passed. The chapter stays in the list with its read history, and Maki won't download
          it again unless you ask. A series can turn this on or off for itself from its menu.
        </Trans>
      }
      dirty={dirty}
      saving={save.isPending}
      onDiscard={discard}
      onSave={() =>
        save.mutate({ enabled, days: Number(days), keepLast }, { onSuccess: savedToast })
      }
    >
      <Stack gap="md">
        <Switch
          label={t`Delete read chapters for every series`}
          description={t`Off: only series switched on from their own menu are cleaned up.`}
          checked={enabled}
          onChange={(e) => setEnabled(e.currentTarget.checked)}
        />
        <NumberInput
          label={t`Days after reading`}
          min={1}
          max={365}
          clampBehavior="strict"
          value={days}
          onChange={setDays}
          w={220}
        />
        <Switch
          label={t`Keep each reader's last read chapter`}
          description={t`So there is always something to look back at where you stopped.`}
          checked={keepLast}
          onChange={(e) => setKeepLast(e.currentTarget.checked)}
        />
      </Stack>
    </SettingsSection>
  )
}

/** The hardlink switch inside the qBittorrent card. Part of the download settings record, saved on change. */
function HardlinkField() {
  const { t } = useLingui()
  const { data: settings } = useDownloadSettings()
  const save = useSaveDownloadSettings()
  const checked = save.isPending && save.variables ? save.variables.useHardlinks : (settings?.useHardlinks ?? true)

  return (
    <Stack gap={0} mt="lg">
      <Text fw={500} size="sm" mb={4}>
        <Trans>Finished torrents</Trans>
      </Text>
      <SettingsHelp mb="xs">
        <Trans>
          Seeding torrents stay in the download folder, so imports are linked or copied, never
          moved. A hardlink stores the files once but needs the download folder and library on the
          same filesystem; otherwise Maki copies. Hardlinked files are left as released, without
          Maki's ComicInfo.xml, so Kavita may group them apart from Maki's own downloads.
        </Trans>
      </SettingsHelp>
      <Switch
        label={t`Hardlink imported torrents when possible`}
        checked={checked}
        disabled={!settings}
        onChange={(e) => {
          save.mutate({ useHardlinks: e.currentTarget.checked }, { onSuccess: savedToast })
        }}
      />
    </Stack>
  )
}

export function FlareSolverrCard() {
  const { t } = useLingui()
  return (
    <ConnectionSettingsCard
      name="flaresolverr"
      title="FlareSolverr"
      description={t`A helper that gets past Cloudflare for sources like MangaFire. Point this at a running FlareSolverr instance. Sources that need it are tagged in Manage sources.`}
      fields={[{ key: 'url', label: t`URL`, placeholder: 'http://localhost:8191' }]}
    />
  )
}

export function QbittorrentCard() {
  const { t } = useLingui()
  return (
    <ConnectionSettingsCard
      name="qbittorrent"
      title="qBittorrent"
      description={t`Download client for grabbed releases. Finished torrents import into the library automatically. Fill the path mapping only if qBittorrent reports paths Maki can't reach, e.g. /downloads in Docker where Maki sees Z:\\downloads.`}
      fields={[
        { key: 'url', label: t`URL`, placeholder: 'http://localhost:8080' },
        { key: 'username', label: t`Username` },
        { key: 'password', label: t`Password`, secret: true },
        {
          key: 'category',
          label: t`Category`,
          placeholder: 'maki',
          description: t`Tag given to torrents Maki adds. Defaults to maki.`,
        },
        { key: 'pathMapFrom', label: t`Path as qBittorrent reports it`, placeholder: t`/downloads (optional)` },
        { key: 'pathMapTo', label: t`Path as Maki sees it`, placeholder: t`Z:\\downloads (optional)` },
      ]}
    >
      <HardlinkField />
    </ConnectionSettingsCard>
  )
}

const BYTES_PER_MB = 1024 * 1024

export function UpgradeScanSection() {
  const { t } = useLingui()
  const { data: settings } = useUpgradeSettings()
  const { data: profiles } = useUpgradeProfiles()
  const save = useSaveUpgradeSettings()
  const scan = useRunUpgradeScan()
  const [enabled, setEnabled] = useState(false)
  const [defaultProfileId, setDefaultProfileId] = useState<number | null>(null)
  const [scanHour, setScanHour] = useState<number | string>(4)
  const [maxPerDay, setMaxPerDay] = useState<number | string>(25)
  const [maxProbesPerRun, setMaxProbesPerRun] = useState<number | string>(50)
  const [quietPeriodDays, setQuietPeriodDays] = useState<number | string>(7)
  const [trashRetentionDays, setTrashRetentionDays] = useState<number | string>(14)
  const [scanIncognito, setScanIncognito] = useState(true)
  const [discarded, discard] = useReducer((n: number) => n + 1, 0)

  useSliceSync(
    settings && {
      enabled: settings.enabled,
      defaultProfileId: settings.defaultProfileId,
      scanHour: settings.scanHour,
      maxPerDay: settings.maxPerDay,
      maxProbesPerRun: settings.maxProbesPerRun,
      quietPeriodDays: settings.quietPeriodDays,
      trashRetentionDays: settings.trashRetentionDays,
      scanIncognito: settings.scanIncognito,
    },
    (slice) => {
      setEnabled(slice.enabled)
      setDefaultProfileId(slice.defaultProfileId)
      setScanHour(slice.scanHour)
      setMaxPerDay(slice.maxPerDay)
      setMaxProbesPerRun(slice.maxProbesPerRun)
      setQuietPeriodDays(slice.quietPeriodDays)
      setTrashRetentionDays(slice.trashRetentionDays)
      setScanIncognito(slice.scanIncognito)
    },
    discarded,
  )

  const dirty =
    settings !== undefined &&
    (enabled !== settings.enabled ||
      defaultProfileId !== settings.defaultProfileId ||
      Number(scanHour) !== settings.scanHour ||
      Number(maxPerDay) !== settings.maxPerDay ||
      Number(maxProbesPerRun) !== settings.maxProbesPerRun ||
      Number(quietPeriodDays) !== settings.quietPeriodDays ||
      Number(trashRetentionDays) !== settings.trashRetentionDays ||
      scanIncognito !== settings.scanIncognito)

  return (
    <SettingsSection
      id="upgrades"
      title={<Trans>Upgrade scan</Trans>}
      description={
        <Trans>
          Replaces downloaded files with better releases, judged against each series' quality
          profile. The daily scan runs after the chosen hour. Scan now and scans started from a
          series page run regardless of the switch.
        </Trans>
      }
      dirty={dirty}
      saving={save.isPending}
      onDiscard={discard}
      onSave={() =>
        save.mutate(
          {
            enabled,
            defaultProfileId,
            scanHour: Number(scanHour),
            maxPerDay: Number(maxPerDay),
            maxProbesPerRun: Number(maxProbesPerRun),
            quietPeriodDays: Number(quietPeriodDays),
            trashRetentionDays: Number(trashRetentionDays),
            scanIncognito,
          },
          { onSuccess: savedToast },
        )
      }
    >
      <Switch
        label={t`Run the daily scan`}
        description={t`Off stops the daily scan and the daily volume search. Scan now and per-series scans still work.`}
        checked={enabled}
        onChange={(e) => setEnabled(e.currentTarget.checked)}
        mb="md"
      />
      <Select
        label={t`Default profile`}
        description={t`Used by any series that hasn't been pinned to a profile of its own.`}
        value={defaultProfileId == null ? '' : String(defaultProfileId)}
        onChange={(value) => setDefaultProfileId(value ? Number(value) : null)}
        data={[
          { value: '', label: t`None` },
          ...(profiles ?? []).map((p) => ({ value: String(p.id), label: p.name })),
        ]}
        w={260}
        mb="md"
      />
      <Group grow mb="md">
        <NumberInput
          label={t`Daily scan hour`}
          description={t`Server local time, 0 to 23.`}
          min={0}
          max={23}
          clampBehavior="strict"
          value={scanHour}
          onChange={setScanHour}
        />
        <NumberInput
          label={t`Max upgrades per day`}
          description={t`Counted per UTC day. 0 means no cap.`}
          min={0}
          max={1000}
          clampBehavior="strict"
          value={maxPerDay}
          onChange={setMaxPerDay}
        />
      </Group>
      <Group grow mb="md">
        <NumberInput
          label={t`Release checks per scan`}
          description={t`How many candidate releases one scan may inspect before it stops.`}
          min={1}
          max={500}
          clampBehavior="strict"
          value={maxProbesPerRun}
          onChange={setMaxProbesPerRun}
        />
        <NumberInput
          label={t`Quiet period (days)`}
          description={t`Skip a chapter this long after it was added or last upgraded.`}
          min={0}
          max={365}
          clampBehavior="strict"
          value={quietPeriodDays}
          onChange={setQuietPeriodDays}
        />
      </Group>
      <NumberInput
        label={t`Trash retention (days)`}
        description={t`Replaced files are kept this long before being purged. 0 purges on the next housekeeping pass.`}
        min={0}
        max={365}
        clampBehavior="strict"
        value={trashRetentionDays}
        onChange={setTrashRetentionDays}
        w={260}
        mb="md"
      />
      <Switch
        label={t`Scan incognito series`}
        description={t`Off skips series set to No scrobble or Full incognito.`}
        checked={scanIncognito}
        onChange={(e) => setScanIncognito(e.currentTarget.checked)}
        mb="md"
      />
      <Group mt="md">
        <Button
          variant="default"
          loading={scan.isPending}
          onClick={() =>
            scan.mutate(undefined, {
              onSuccess: () => {
                notifications.show({
                  message: now`Scan started`,
                  color: 'var(--ok)',
                })
              },
              onError: (error) => {
                notifications.show({
                  message:
                    error instanceof ApiError && error.status === 409
                      ? now`A scan is already running`
                      : now`Couldn't start the scan`,
                  color: 'var(--danger)',
                })
              },
            })
          }
        >
          <Trans>Scan now</Trans>
        </Button>
      </Group>
    </SettingsSection>
  )
}

export function VolumeReleasesSection() {
  const { t } = useLingui()
  const { data: settings } = useUpgradeSettings()
  const save = useSaveUpgradeSettings()
  const [volumeSearch, setVolumeSearch] = useState(true)
  const [autoGrabMb, setAutoGrabMb] = useState<number | string>(500)
  const [volumeMissingTolerance, setVolumeMissingTolerance] = useState<number | string>(3)
  const [volumeSearchesPerRun, setVolumeSearchesPerRun] = useState<number | string>(10)
  const [proposalExpiryDays, setProposalExpiryDays] = useState<number | string>(30)
  const [discarded, discard] = useReducer((n: number) => n + 1, 0)

  useSliceSync(
    settings && {
      volumeSearch: settings.volumeSearch,
      torrentAutoGrabMaxBytes: settings.torrentAutoGrabMaxBytes,
      volumeMissingTolerance: settings.volumeMissingTolerance,
      volumeSearchesPerRun: settings.volumeSearchesPerRun,
      proposalExpiryDays: settings.proposalExpiryDays,
    },
    (slice) => {
      setVolumeSearch(slice.volumeSearch)
      setAutoGrabMb(slice.torrentAutoGrabMaxBytes / BYTES_PER_MB)
      setVolumeMissingTolerance(slice.volumeMissingTolerance)
      setVolumeSearchesPerRun(slice.volumeSearchesPerRun)
      setProposalExpiryDays(slice.proposalExpiryDays)
    },
    discarded,
  )

  const autoGrabBytes = Math.round(Number(autoGrabMb) * BYTES_PER_MB)

  const dirty =
    settings !== undefined &&
    (volumeSearch !== settings.volumeSearch ||
      autoGrabBytes !== settings.torrentAutoGrabMaxBytes ||
      Number(volumeMissingTolerance) !== settings.volumeMissingTolerance ||
      Number(volumeSearchesPerRun) !== settings.volumeSearchesPerRun ||
      Number(proposalExpiryDays) !== settings.proposalExpiryDays)

  return (
    <SettingsSection
      id="volume-releases"
      title={<Trans>Volume releases</Trans>}
      description={
        <Trans>
          Looks for torrent volume packs that could replace single-chapter files, for series whose
          profile aims for volumes. Needs Prowlarr and qBittorrent, and runs an hour after the
          daily scan.
        </Trans>
      }
      dirty={dirty}
      saving={save.isPending}
      onDiscard={discard}
      onSave={() =>
        save.mutate(
          {
            volumeSearch,
            torrentAutoGrabMaxBytes: autoGrabBytes,
            volumeMissingTolerance: Number(volumeMissingTolerance),
            volumeSearchesPerRun: Number(volumeSearchesPerRun),
            proposalExpiryDays: Number(proposalExpiryDays),
          },
          { onSuccess: savedToast },
        )
      }
    >
      <Switch
        label={t`Search torrents for volume releases`}
        description={t`Also needs the daily scan on.`}
        checked={volumeSearch}
        onChange={(e) => setVolumeSearch(e.currentTarget.checked)}
        mb="md"
      />
      <Group grow mb="md" align="flex-start">
        <NumberInput
          label={t`Auto-grab size limit (MB)`}
          description={t`Larger releases become proposals you approve by hand.`}
          min={0}
          max={102400}
          decimalScale={0}
          clampBehavior="strict"
          value={autoGrabMb}
          onChange={setAutoGrabMb}
          disabled={!volumeSearch}
        />
        <NumberInput
          label={t`Missing chapters allowed per volume`}
          description={t`Auto-grab only when a volume adds this many chapters or fewer that you don't have.`}
          min={0}
          max={50}
          clampBehavior="strict"
          value={volumeMissingTolerance}
          onChange={setVolumeMissingTolerance}
          disabled={!volumeSearch}
        />
      </Group>
      <Group grow align="flex-start">
        <NumberInput
          label={t`Volume searches per run`}
          description={t`Series checked per daily run. Each series is searched at most once a week.`}
          min={1}
          max={200}
          clampBehavior="strict"
          value={volumeSearchesPerRun}
          onChange={setVolumeSearchesPerRun}
          disabled={!volumeSearch}
        />
        <NumberInput
          label={t`Proposal expiry (days)`}
          description={t`A proposal nobody answers is dropped after this long.`}
          min={1}
          max={365}
          clampBehavior="strict"
          value={proposalExpiryDays}
          onChange={setProposalExpiryDays}
          disabled={!volumeSearch}
        />
      </Group>
    </SettingsSection>
  )
}

/** The indexer and category filter, which saves through its own endpoint beside the connection. */
function useProwlarrOptionsForm() {
  const { data: options } = useProwlarrOptions()
  const save = useSaveProwlarrOptions()
  const [selectedIndexers, setSelectedIndexers] = useState<Set<number>>(new Set())
  const [categories, setCategories] = useState<string[]>([])
  const [discarded, discard] = useReducer((n: number) => n + 1, 0)

  useEffect(() => {
    if (options) {
      setSelectedIndexers(
        new Set((options.indexerIds ?? '').split(',').filter(Boolean).map(Number)),
      )
      setCategories((options.categories ?? '').split(',').filter(Boolean))
    }
  }, [options, discarded])

  const sortedIds = (ids: Iterable<number>) => [...ids].sort((a, b) => a - b).join(',')
  const dirty =
    options !== undefined &&
    (sortedIds(selectedIndexers) !==
      sortedIds((options.indexerIds ?? '').split(',').filter(Boolean).map(Number)) ||
      categories.join(',') !== (options.categories ?? ''))

  return {
    selectedIndexers,
    setSelectedIndexers,
    categories,
    setCategories,
    dirty,
    saving: save.isPending,
    reset: discard,
    commit: () =>
      save.mutateAsync({
        indexerIds: [...selectedIndexers].sort((a, b) => a - b).join(',') || null,
        categories: categories.join(',') || null,
      }),
  }
}

export function ProwlarrSection() {
  const { t } = useLingui()
  const options = useProwlarrOptionsForm()
  return (
    <ConnectionSettingsCard
      name="prowlarr"
      title="Prowlarr"
      description={t`Searches your torrent indexers for manga releases, used for volume upgrades and for grabbing releases by hand. No app sync needed.`}
      fields={[
        { key: 'url', label: t`URL`, placeholder: 'http://localhost:9696' },
        { key: 'apiKey', label: t`API key`, secret: true },
      ]}
      extra={options}
    >
      <ProwlarrOptionsSection form={options} />
    </ConnectionSettingsCard>
  )
}

function ProwlarrOptionsSection({ form }: { form: ReturnType<typeof useProwlarrOptionsForm> }) {
  const { t } = useLingui()
  const { data: connection } = useConnectionSettings<Record<string, string | null>>('prowlarr')
  const configured = Boolean(connection?.url && connection?.apiKey)
  const { data: indexers, error: indexersError } = useProwlarrIndexers(configured)
  const indexersErrorMessage = indexersError != null ? String(indexersError) : null
  const { selectedIndexers, setSelectedIndexers, categories, setCategories } = form

  const categoryData = [
    ...new Map(
      (indexers ?? [])
        .flatMap((i) => i.categories)
        .map((c) => [String(c.id), { value: String(c.id), label: `${c.name} (${c.id})` }]),
    ).values(),
    // keep saved categories selectable even when no indexer advertises them
    ...categories
      .filter((c) => !(indexers ?? []).some((i) => i.categories.some((x) => String(x.id) === c)))
      .map((c) => ({ value: c, label: c })),
  ].sort((a, b) => Number(a.value) - Number(b.value))

  return (
    <Stack gap="sm" mt="md">
      {configured && (
        <Text size="sm" c="var(--ink-3)">
          <Trans>
            Restrict release searches to specific indexers and categories. With nothing
            selected, every indexer and category is searched.
          </Trans>
        </Text>
      )}
      {configured && indexersErrorMessage != null && (
        <Text size="sm" c="var(--danger)">
          <Trans>Could not load indexers from Prowlarr: {indexersErrorMessage}</Trans>
        </Text>
      )}
      {configured && indexers && (
        <Stack gap="sm">
          <Stack gap={6}>
            {indexers.map((indexer) => {
              const { name, enable } = indexer
              return (
              <Checkbox
                key={indexer.id}
                label={enable ? name : t`${name} (disabled in Prowlarr)`}
                checked={selectedIndexers.has(indexer.id)}
                onChange={(e) => {
                  const checked = e.currentTarget.checked
                  setSelectedIndexers((prev) => {
                    const next = new Set(prev)
                    if (checked) next.add(indexer.id)
                    else next.delete(indexer.id)
                    return next
                  })
                }}
              />
              )
            })}
            {indexers.length === 0 && (
              <Text size="sm" c="var(--ink-3)">
                <Trans>No indexers configured in Prowlarr.</Trans>
              </Text>
            )}
          </Stack>
          <MultiSelect
            label={t`Categories`}
            placeholder={categories.length === 0 ? t`All categories` : undefined}
            data={categoryData}
            value={categories}
            onChange={setCategories}
            searchable
            clearable
          />
        </Stack>
      )}
    </Stack>
  )
}

