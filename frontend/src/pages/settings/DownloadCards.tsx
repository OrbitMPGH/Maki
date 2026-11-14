import { errorText } from '../../api/errorText'
import { useEffect, useReducer, useState } from 'react'
import { ApiError } from '../../api/client'
import { Trans, Plural, useLingui } from '@lingui/react/macro'
import { t as now } from '@lingui/core/macro'
import { Box, Button, Checkbox, Group, Input, MultiSelect, SegmentedControl, Select, Stack, Switch, Text } from '@mantine/core'
import { IconAdjustments } from '../../lib/icons'
import { notifications } from '../../lib/toast'
import { SettingsSection } from './SettingsSection'
import { PriorityList } from '../../components/PriorityList'
import { SourceIcon, baseLanguage } from '../../sourceIcons'
import { ManageSourcesModal, type WizardRequest } from './ManageSourcesModal'
import { useSearchParams } from 'react-router-dom'
import {
  useUpgradeProfiles,
  useRunUpgradeScan,
  useSaveUpgradeSettings,
  useUpgradeSettings,
} from '../../api/upgrades'
import { savedToast, useSliceSync } from './sharedRecord'
import {
  useDownloadSettings,
  useIndexerList,
  useIndexerOptions,
  useIndexerSettings,
  useReadFileCleanupSettings,
  useSaveDownloadSettings,
  useSaveIndexerOptions,
  useSaveReadFileCleanupSettings,
  useSaveSourceLanguages,
  type SourceOrderMode,
  useSourceLanguages,
  useSourcePriority,
  useSources,
} from '../../api/hooks'
import { ConnectionSettingsCard } from '../../components/ConnectionSettingsCard'
import { ConnectionTestLine } from '../../components/ConnectionSettingsCardStatus'
import { IndexerFieldRow } from '../../components/settings/IndexerForm'
import { indexerBackendName, useIndexerForm } from '../../components/settings/useIndexerForm'
import { useDownloadClientFields } from '../../components/settings/downloadClientFields'
import { TorrentLifecycleFields } from '../../components/settings/TorrentLifecycleFields'
import { NewSettingPill } from '../../components/settings/NewSettingPill'
import { useNewControlMark } from './newSettings'
import { useTorrentLifecycleForm } from '../../components/settings/useTorrentLifecycleForm'
import { SettingsNumberInput } from '../../components/settings/SettingsNumberInput'
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
      description={<Trans>Most preferred first.</Trans>}
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
  const [wizard, setWizard] = useState<WizardRequest | null>(null)
  // `sources=manage` opens Manage sources and `sources=edit:custom-{id}` that source's wizard on
  // top, for the health page and other links. The parameter is dropped once read, so closing the
  // modal doesn't reopen it.
  const [searchParams, setSearchParams] = useSearchParams()
  const sourcesParam = searchParams.get('sources')
  useEffect(() => {
    if (!sourcesParam) return
    const edit = /^edit:custom-(.+)$/.exec(sourcesParam)
    if (sourcesParam === 'manage' || edit) {
      setWizard(edit ? { editId: edit[1] } : null)
      setManaging(true)
    }
    setSearchParams((params) => {
      params.delete('sources')
      return params
    }, { replace: true })
  }, [sourcesParam, setSearchParams])
  const { data: settings } = useDownloadSettings()
  const save = useSaveDownloadSettings()
  const [sourceOrder, setSourceOrder] = useState<SourceOrderMode>('manual')
  const [scoutOnMatch, setScoutOnMatch] = useState(false)
  const [volumeFirst, setVolumeFirst] = useState(false)
  const [discarded, discard] = useReducer((n: number) => n + 1, 0)

  useSliceSync(
    settings && { sourceOrder: settings.sourceOrder, scoutOnMatch: settings.scoutOnMatch, volumeFirst: settings.volumeFirst },
    (slice) => {
      setSourceOrder(slice.sourceOrder)
      setScoutOnMatch(slice.scoutOnMatch)
      setVolumeFirst(slice.volumeFirst)
    },
    discarded,
  )

  const dirty =
    settings !== undefined &&
    (sourceOrder !== settings.sourceOrder || scoutOnMatch !== settings.scoutOnMatch || volumeFirst !== settings.volumeFirst)

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
      actions={
        <Button size="xs" leftSection={<IconAdjustments size={16} />} onClick={() => setManaging(true)}>
          <Trans>Manage sources</Trans>
        </Button>
      }
      dirty={dirty}
      saving={save.isPending}
      onDiscard={discard}
      onSave={() => save.mutate({ sourceOrder, scoutOnMatch, volumeFirst }, { onSuccess: savedToast })}
    >
      <Input.Wrapper label={t`Pick a source by`} mb="sm">
        <SegmentedControl
          mt={4}
          aria-label={t`Pick a source by`}
          data={[
            { value: 'manual', label: t`Your priority order` },
            { value: 'quality', label: t`Best measured quality` },
          ]}
          value={sourceOrder}
          onChange={(value) => setSourceOrder(value as SourceOrderMode)}
        />
      </Input.Wrapper>
      <Switch
        label={t`Measure sources when a series is added`}
        description={t`Samples a few pages from every linked source.`}
        checked={scoutOnMatch}
        onChange={(e) => setScoutOnMatch(e.currentTarget.checked)}
        mb="sm"
      />
      <Switch
        label={t`Look for volume releases first`}
        description={t`A new series with nothing on disk asks your indexers for a volume release once its sources are matched, before anything is downloaded from them. Series from an import list wait up to a day for their turn. While a release waits for approval (up to 48 hours) or downloads (up to 72 hours), automatic downloads from sources skip the chapters it covers, so they are not fetched twice. Needs Prowlarr or Jackett, and a quality profile that upgrades the series to volumes. Downloads you start yourself are never held.`}
        checked={volumeFirst}
        onChange={(e) => setVolumeFirst(e.currentTarget.checked)}
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
      <ManageSourcesModal
        opened={managing}
        onClose={() => {
          setManaging(false)
          setWizard(null)
        }}
        wizard={wizard}
      />
    </SettingsSection>
  )
}

export function DownloadQueueSection() {
  const { t } = useLingui()
  const { data: settings } = useDownloadSettings()
  const save = useSaveDownloadSettings()
  const [concurrentChapters, setConcurrentChapters] = useState<number>(2)
  // One number on screen, two fields on the wire: 0 means retry is off, and turning it off keeps the
  // stored cap so switching it back on doesn't forget what it was.
  const [retryAttempts, setRetryAttempts] = useState<number>(5)
  const [itemTimeoutMinutes, setItemTimeoutMinutes] = useState<number>(120)
  const [bulkHoldThreshold, setBulkHoldThreshold] = useState<number>(5)
  const [adaptiveRefresh, setAdaptiveRefresh] = useState(true)
  const [discarded, discard] = useReducer((n: number) => n + 1, 0)

  useSliceSync(
    settings && {
      concurrentChapters: settings.concurrentChapters,
      retryAttempts: settings.retryEnabled ? settings.retryMaxAttempts : 0,
      itemTimeoutMinutes: settings.itemTimeoutMinutes,
      bulkHoldThreshold: settings.bulkHoldThreshold,
      adaptiveRefresh: settings.adaptiveRefresh,
    },
    (slice) => {
      setConcurrentChapters(slice.concurrentChapters)
      setRetryAttempts(slice.retryAttempts)
      setItemTimeoutMinutes(slice.itemTimeoutMinutes)
      setBulkHoldThreshold(slice.bulkHoldThreshold)
      setAdaptiveRefresh(slice.adaptiveRefresh)
    },
    discarded,
  )

  const dirty =
    settings !== undefined &&
    (Number(concurrentChapters) !== settings.concurrentChapters ||
      Number(retryAttempts) !== (settings.retryEnabled ? settings.retryMaxAttempts : 0) ||
      Number(itemTimeoutMinutes) !== settings.itemTimeoutMinutes ||
      Number(bulkHoldThreshold) !== settings.bulkHoldThreshold ||
      adaptiveRefresh !== settings.adaptiveRefresh)

  return (
    <SettingsSection
      id="downloads"
      title={<Trans>Download queue</Trans>}
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
            adaptiveRefresh,
          },
          { onSuccess: savedToast },
        )
      }}
    >
      <Box className="settings-field-grid" data-pairs mb="lg">
        <SettingsNumberInput
          label={t`Concurrent chapter downloads`}
          min={1}
          max={8}
          value={concurrentChapters}
          onChange={setConcurrentChapters}
        />
        <SettingsNumberInput
          label={t`Retry attempts for a failed download`}
          description={t`0 turns retry off.`}
          min={0}
          max={20}
          value={retryAttempts}
          onChange={setRetryAttempts}
        />
        <SettingsNumberInput
          label={t`Time limit per chapter`}
          description={t`In minutes. 0 means no limit.`}
          min={0}
          max={1440}
          value={itemTimeoutMinutes}
          onChange={setItemTimeoutMinutes}
          settle={clampItemTimeout}
        />
        <SettingsNumberInput
          label={t`Auto-queue limit`}
          description={t`The most new chapters queued at once. Bigger batches stay wanted for you to download by hand. 0 means always queue.`}
          min={0}
          max={1000}
          value={bulkHoldThreshold}
          onChange={setBulkHoldThreshold}
        />
      </Box>
      <Switch
        label={t`Check quiet series less often`}
        checked={adaptiveRefresh}
        onChange={(e) => setAdaptiveRefresh(e.currentTarget.checked)}
      />
    </SettingsSection>
  )
}

export function SmartDownloadSection() {
  const { t } = useLingui()
  const { data: settings } = useDownloadSettings()
  const save = useSaveDownloadSettings()
  const [smartDownloadChaptersLeft, setSmartDownloadChaptersLeft] = useState<number>(5)
  const [smartDownloadChapters, setSmartDownloadChapters] = useState<number>(10)
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
      title={<Trans>Smart download</Trans>}
      description={<Trans>Turned on per series in its monitoring options.</Trans>}
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
      <Box className="settings-field-grid" data-pairs data-align="end">
        <SettingsNumberInput
          label={t`Fetch more when unread chapters drop to`}
          min={1}
          max={10}
          value={smartDownloadChaptersLeft}
          onChange={setSmartDownloadChaptersLeft}
        />
        <SettingsNumberInput
          label={t`Chapters to fetch each time`}
          min={1}
          max={20}
          value={smartDownloadChapters}
          onChange={setSmartDownloadChapters}
        />
      </Box>
    </SettingsSection>
  )
}

export function ReadFileCleanupSection() {
  const { t } = useLingui()
  const { data: settings } = useReadFileCleanupSettings()
  const save = useSaveReadFileCleanupSettings()
  const [enabled, setEnabled] = useState(false)
  const [days, setDays] = useState<number>(7)
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
      description={<Trans>Deletes a chapter's file once everyone has read it. The chapter and its history stay.</Trans>}
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
          checked={enabled}
          onChange={(e) => setEnabled(e.currentTarget.checked)}
        />
        <SettingsNumberInput
          label={t`Days after reading`}
          min={1}
          max={365}
          value={days}
          onChange={setDays}
        />
        <Switch
          label={t`Keep each reader's last read chapter`}
          checked={keepLast}
          onChange={(e) => setKeepLast(e.currentTarget.checked)}
        />
      </Stack>
    </SettingsSection>
  )
}

export function FlareSolverrCard() {
  const { t } = useLingui()
  return (
    <ConnectionSettingsCard
      name="flaresolverr"
      title="FlareSolverr"
      description={t`Gets past Cloudflare for the sources that need it.`}
      fields={[{ key: 'url', label: t`URL`, placeholder: 'http://localhost:8191' }]}
    />
  )
}

export function DownloadClientCard() {
  const { t } = useLingui()
  const fields = useDownloadClientFields()
  const lifecycle = useTorrentLifecycleForm()
  return (
    <ConnectionSettingsCard
      name="downloadclient"
      title={t`Download client`}
      fields={fields}
      extra={lifecycle.group}
    >
      <TorrentLifecycleFields form={lifecycle} />
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
  const [scanHour, setScanHour] = useState<number>(4)
  const [maxPerDay, setMaxPerDay] = useState<number>(25)
  const [maxProbesPerRun, setMaxProbesPerRun] = useState<number>(50)
  const [quietPeriodDays, setQuietPeriodDays] = useState<number>(7)
  const [trashRetentionDays, setTrashRetentionDays] = useState<number>(14)
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
      description={<Trans>Replaces downloaded files with better releases for each series' quality profile.</Trans>}
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
        checked={enabled}
        onChange={(e) => setEnabled(e.currentTarget.checked)}
        mb="md"
      />
      <Select
        label={t`Default profile`}
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
        <SettingsNumberInput
          label={t`Daily scan hour`}
          description={t`Server local time, 0 to 23.`}
          min={0}
          max={23}
          value={scanHour}
          onChange={setScanHour}
        />
        <SettingsNumberInput
          label={t`Max upgrades per day`}
          description={t`0 means no cap.`}
          min={0}
          max={1000}
          value={maxPerDay}
          onChange={setMaxPerDay}
        />
      </Group>
      <Group grow mb="md">
        <SettingsNumberInput
          label={t`Release checks per scan`}
          min={1}
          max={500}
          value={maxProbesPerRun}
          onChange={setMaxProbesPerRun}
        />
        <SettingsNumberInput
          label={t`Quiet period`}
          description={t`Days to skip a chapter after it was added or last upgraded.`}
          min={0}
          max={365}
          value={quietPeriodDays}
          onChange={setQuietPeriodDays}
        />
      </Group>
      <SettingsNumberInput
        label={t`Trash retention`}
        description={t`Days to keep replaced files. 0 deletes them at the next cleanup.`}
        min={0}
        max={365}
        value={trashRetentionDays}
        onChange={setTrashRetentionDays}
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
  const [autoGrabMb, setAutoGrabMb] = useState<number>(500)
  const [volumeMissingTolerance, setVolumeMissingTolerance] = useState<number>(3)
  const [volumeSearchesPerRun, setVolumeSearchesPerRun] = useState<number>(10)
  const [proposalExpiryDays, setProposalExpiryDays] = useState<number>(30)
  const [autoApprove, setAutoApprove] = useState(false)
  const [autoApproveMb, setAutoApproveMb] = useState<number>(12288)
  const [missingSearch, setMissingSearch] = useState(true)
  const [minSeeders, setMinSeeders] = useState<number>(1)
  const missingMark = useNewControlMark('missing-chapter-search')
  const seedersMark = useNewControlMark('minimum-seeders')
  const autoApproveMark = useNewControlMark('auto-approve-volumes')
  const [discarded, discard] = useReducer((n: number) => n + 1, 0)

  useSliceSync(
    settings && {
      missingSearch: settings.missingSearch,
      torrentMinSeeders: settings.torrentMinSeeders,
      autoApprove: settings.autoApprove,
      autoApproveMaxBytes: settings.autoApproveMaxBytes,
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
      setAutoApprove(slice.autoApprove)
      setAutoApproveMb(slice.autoApproveMaxBytes / BYTES_PER_MB)
      setMissingSearch(slice.missingSearch)
      setMinSeeders(slice.torrentMinSeeders)
    },
    discarded,
  )

  const autoGrabBytes = Math.round(Number(autoGrabMb) * BYTES_PER_MB)
  const autoApproveBytes = Math.round(Number(autoApproveMb) * BYTES_PER_MB)

  const dirty =
    settings !== undefined &&
    (volumeSearch !== settings.volumeSearch ||
      autoGrabBytes !== settings.torrentAutoGrabMaxBytes ||
      Number(volumeMissingTolerance) !== settings.volumeMissingTolerance ||
      Number(volumeSearchesPerRun) !== settings.volumeSearchesPerRun ||
      Number(proposalExpiryDays) !== settings.proposalExpiryDays ||
      autoApprove !== settings.autoApprove ||
      autoApproveBytes !== settings.autoApproveMaxBytes ||
      missingSearch !== settings.missingSearch ||
      Number(minSeeders) !== settings.torrentMinSeeders)

  return (
    <SettingsSection
      id="volume-releases"
      title={<Trans>Volume releases</Trans>}
      description={<Trans>Needs an indexer and a download client.</Trans>}
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
            autoApprove,
            autoApproveMaxBytes: autoApproveBytes,
            missingSearch,
            torrentMinSeeders: Number(minSeeders),
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
      <div id="setting-missing-chapter-search" {...missingMark}>
        <Switch
          label={
            <>
              {t`Search torrents for chapters no source has`}
              <NewSettingPill id="missing-chapter-search" />
            </>
          }
          description={t`For monitored series with wanted chapters that none of their sources list, such as licensed titles a site took down, or series with no source at all. Each series is searched at most once a week, a few series at a time. Works with the daily scan off. Chapters a source lists keep downloading from it.`}
          checked={missingSearch}
          onChange={(e) => setMissingSearch(e.currentTarget.checked)}
          mb="md"
        />
      </div>
      <div id="setting-minimum-seeders" {...seedersMark}>
        <SettingsNumberInput
          label={
            <>
              {t`Minimum seeders`}
              <NewSettingPill id="minimum-seeders" />
            </>
          }
          description={t`Releases with fewer seeders are never grabbed or approved on their own. You can still grab them from the release search.`}
          min={1}
          max={10000}
          decimalScale={0}
          value={minSeeders}
          onChange={setMinSeeders}
          mb="md"
        />
      </div>
      <Group grow mb="md" align="flex-start">
        <SettingsNumberInput
          label={t`Auto-grab size limit`}
          suffix={t` MB`}
          description={t`Larger releases become proposals you approve by hand.`}
          min={0}
          max={102400}
          decimalScale={0}
          value={autoGrabMb}
          onChange={setAutoGrabMb}
          disabled={!volumeSearch}
        />
        <SettingsNumberInput
          label={t`Missing chapters allowed per volume`}
          min={0}
          max={50}
          value={volumeMissingTolerance}
          onChange={setVolumeMissingTolerance}
          disabled={!volumeSearch}
        />
      </Group>
      <Group grow align="flex-start">
        <SettingsNumberInput
          label={t`Volume searches per run`}
          min={1}
          max={200}
          value={volumeSearchesPerRun}
          onChange={setVolumeSearchesPerRun}
          disabled={!volumeSearch}
        />
        <SettingsNumberInput
          label={t`Proposal expiry`}
          description={t`Days before a proposal you haven't approved expires.`}
          min={1}
          max={365}
          value={proposalExpiryDays}
          onChange={setProposalExpiryDays}
          disabled={!volumeSearch}
        />
      </Group>
      <div id="setting-auto-approve-volumes" {...autoApproveMark}>
        <Switch
          label={
            <>
              {t`Approve volume releases automatically`}
              <NewSettingPill id="auto-approve-volumes" />
            </>
          }
          description={t`Grabs a proposed release without waiting for you when it is a digital release within the size ceiling and its only reasons for waiting are that it names no volume range, adds chapters you don't have, or is over the auto-grab size. Anything Maki is unsure about, such as chapters it cannot place, still waits for you. It only replaces files the release is at least as good as, and replaced files go to the trash as usual. Pending proposals are checked on the next daily run, or when you press Search now.`}
          checked={autoApprove}
          onChange={(e) => setAutoApprove(e.currentTarget.checked)}
          disabled={!volumeSearch}
          mt="md"
          mb="sm"
        />
        <SettingsNumberInput
          label={t`Automatic approval size ceiling (MB)`}
          description={t`Never approve a release larger than this on your behalf. The 12288 MB default is about 50 digital volumes.`}
          min={100}
          max={204800}
          decimalScale={0}
          value={autoApproveMb}
          onChange={setAutoApproveMb}
          disabled={!volumeSearch || !autoApprove}
        />
      </div>
    </SettingsSection>
  )
}

/** The indexer and category filter of the active backend, which saves through its own endpoint beside the connection. */
function useIndexerOptionsForm() {
  const { data: connection } = useIndexerSettings()
  const backend = connection?.backend
  const { data: options } = useIndexerOptions(backend)
  const save = useSaveIndexerOptions()
  const [selectedIndexers, setSelectedIndexers] = useState<Set<string>>(new Set())
  const [categories, setCategories] = useState<string[]>([])
  const [discarded, discard] = useReducer((n: number) => n + 1, 0)

  useEffect(() => {
    if (options) {
      setSelectedIndexers(new Set((options.indexerIds ?? '').split(',').filter(Boolean)))
      setCategories((options.categories ?? '').split(',').filter(Boolean))
    }
  }, [options, discarded])

  const sortedIds = (ids: Iterable<string>) => [...ids].sort().join(',')
  const dirty =
    options !== undefined &&
    (sortedIds(selectedIndexers) !== sortedIds((options.indexerIds ?? '').split(',').filter(Boolean)) ||
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
      backend === undefined
        ? Promise.resolve()
        : save.mutateAsync({
            backend,
            indexerIds: sortedIds(selectedIndexers) || null,
            categories: categories.join(',') || null,
          }),
  }
}

export function IndexerSection() {
  const { t } = useLingui()
  const form = useIndexerForm()
  const options = useIndexerOptionsForm()
  const optionsDirty = options.dirty && !form.switching
  const selectedName = indexerBackendName(form.selected)

  return (
    <SettingsSection
      id="indexer"
      title={t`Indexer`}
      dirty={form.dirty || optionsDirty}
      saving={form.saving || options.saving}
      onSave={() => {
        const jobs = [form.dirty && form.commit(), optionsDirty && options.commit()].filter(
          (j) => j instanceof Promise,
        )
        Promise.all(jobs).then(savedToast, () => {})
      }}
      onDiscard={() => {
        form.reset()
        options.reset()
      }}
    >
      <IndexerFieldRow
        form={form}
        status={<ConnectionTestLine testKey={form.selected ? `indexer:${form.selected}` : undefined} />}
      />
      {form.switching ? (
        <Text size="sm" c="var(--ink-3)" mt="md">
          <Trans>Save to switch to {selectedName}.</Trans>
        </Text>
      ) : (
        <IndexerOptionsSection form={options} />
      )}
    </SettingsSection>
  )
}

function IndexerOptionsSection({ form }: { form: ReturnType<typeof useIndexerOptionsForm> }) {
  const { t } = useLingui()
  const { data: connection } = useIndexerSettings()
  const configured = Boolean(connection?.url && connection?.apiKey)
  const backendName = indexerBackendName(connection?.backend)
  const { data: indexers, error: indexersError } = useIndexerList(connection?.backend, configured)
  const indexersErrorMessage = indexersError != null ? errorText(indexersError) : null
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
          <Trans>Nothing selected searches them all.</Trans>
        </Text>
      )}
      {configured && indexersErrorMessage != null && (
        <Text size="sm" c="var(--danger)">
          <Trans>Could not load indexers from {backendName}: {indexersErrorMessage}</Trans>
        </Text>
      )}
      {configured && indexers && (
        <Stack gap="sm">
          <Stack gap={6}>
            {indexers.map((indexer) => {
              const { id, name, enabled } = indexer
              return (
                <Checkbox
                  key={id}
                  label={enabled ? name : t`${name} (disabled in ${backendName})`}
                  checked={selectedIndexers.has(id)}
                  onChange={(e) => {
                    const checked = e.currentTarget.checked
                    setSelectedIndexers((prev) => {
                      const next = new Set(prev)
                      if (checked) next.add(id)
                      else next.delete(id)
                      return next
                    })
                  }}
                />
              )
            })}
            {indexers.length === 0 && (
              <Text size="sm" c="var(--ink-3)">
                <Trans>No indexers configured in {backendName}.</Trans>
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
