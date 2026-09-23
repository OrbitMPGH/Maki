import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { useLabel, useLanguageChoice } from '../i18n-context'
import { useDebouncedValue } from '@mantine/hooks'
import { Trans, Plural, useLingui } from '@lingui/react/macro'
import { plural, t as now } from '@lingui/core/macro'
import {
  ActionIcon,
  Alert,
  Badge,
  Button,
  Checkbox,
  Code,
  FileButton,
  Group,
  Modal,
  MultiSelect,
  NumberInput,
  Progress,
  Radio,
  Select,
  Stack,
  Switch,
  Table,
  Tabs,
  Text,
  TextInput,
  Title,
  Tooltip,
  UnstyledButton,
} from '@mantine/core'
import {
  IconAlertTriangle,
  IconCheck,
  IconCopy,
  IconDownload,
  IconLayoutDashboard,
  IconRefresh,
  IconTrash,
  IconUpload,
} from '@tabler/icons-react'
import { notifications } from '@mantine/notifications'
import { PageHeader } from '../components/ui/PageHeader'
import { SurfaceFrame } from '../components/ui/SurfaceFrame'
import { ConfirmDialog } from '../components/ui/ConfirmDialog'
import { Panel } from '../components/ui/Panel'
import { RecommendationModelSwitch } from '../components/RecommendationModelSwitch'
import { NamingFormatInput } from '../components/NamingFormatInput'
import { PriorityList } from '../components/PriorityList'
import { useAuth } from '../auth/AuthProvider'
import { SETTINGS_ENTRIES, SETTINGS_TABS, entryVisible } from './settings/registry'
import { useKavitaUser, useSetKavitaUser, useUsers } from '../api/auth'
import { AccountSection } from '../components/settings/AccountSection'
import { NotificationPrefsSection } from '../components/settings/NotificationPrefsSection'
import { OidcSection, SecuritySection } from '../components/settings/SecuritySection'
import { UsersSection } from '../components/settings/UsersSection'
import { ReadingProfilesSection } from '../components/settings/ReadingProfilesSection'
import { ProgressSection } from '../components/settings/ProgressSection'
import { CONTENT_RATINGS, ContentRatingCards } from '../components/ContentRatingCards'
import { useIncognitoOptions, type IncognitoMode } from '../components/ui/incognito'
import { useApplyLanguage, useLanguageOptions } from '../components/ui/language'
import {
  useAddRootFolder,
  useBackups,
  useBackupSettings,
  useCreateBackup,
  useDeleteBackup,
  useRestoreBackup,
  useSaveBackupSettings,
  useUploadRestore,
  downloadBackup,
  useCompleteSetup,
  useConnectionSettings,
  useDeleteRootFolder,
  useDownloadSettings,
  useLibrarySettings,
  useNamingPreview,
  useSaveLibrarySettings,
  useDiscoverSettings,
  useDumpProgress,
  useMetadataSettings,
  useMonitoringSettings,
  useOpdsSettings,
  useProwlarrIndexers,
  useRotateOpdsToken,
  useSaveOpdsSettings,
  useProwlarrOptions,
  useRecommendationIndex,
  useRefreshMetadataDump,
  useRootFolders,
  useSaveDiscoverSettings,
  useSaveDownloadSettings,
  useSaveMetadataSettings,
  useSaveMonitoringSettings,
  useSaveProwlarrOptions,
  useSaveScrobbleSettings,
  useKavitaLibraries,
  useSaveSourceLanguages,
  useSaveSourcePriority,
  useSaveUiSettings,
  useUiSettings,
  type SeriesSections,
  type UiSettings,
  useSetEmbeddingModel,
  useScrobbleSettings,
  useScrobbleStatus,
  useSourceLanguages,
  useSourcePriority,
  useSources,
  useCheckForUpdatesNow,
  useImageCache,
  useRebuildImageCache,
  useRenameManySeries,
  useSaveUpdateSettings,
  useSeries,
  useUpdateSettings,
  useUpdateStatus,
  CONTENT_RATING_LABELS,
  type FolderNamingMode,
  type LibrarySettings,
  type ScrobbleSettings,
} from '../api/hooks'
import { useKavitaReadImport, useReaderSettings, useSaveReaderSettings } from '../api/reader'
import { ConnectionSettingsCard } from '../components/ConnectionSettingsCard'
import { SaveButton, UnsavedSettingsContext } from '../components/settings/SaveButton'
import { SettingsHelp } from '../components/settings/SettingsHelp'
import { SettingsIndex } from '../components/settings/SettingsIndex'
import { DumpProgressBar } from '../components/MetadataDumpProgress'
import { languageName } from '../api/titles'
import { NotificationsSection } from '../components/NotificationsSection'
import { ApiError } from '../api/client'
import { TrackerSyncControls } from '../components/TrackerSyncControls'
import { useThemeChoice } from '../theme-context'
import { formatBytes, formatDateTime, formatNumber } from '../format'

function RootFoldersSection() {
  const { t } = useLingui()
  const [newPath, setNewPath] = useState('')
  const { data: rootFolders } = useRootFolders()
  const addFolder = useAddRootFolder()
  const deleteFolder = useDeleteRootFolder()

  const add = () => {
    if (!newPath.trim()) return
    addFolder.mutate(newPath.trim(), {
      onSuccess: () => setNewPath(''),
    })
  }

  return (
    <Panel>
      <Title order={4} mb="sm">
        <Trans>Root Folders</Trans>
      </Title>
      <SettingsHelp mb="md">
        <Trans>Library folders where series are stored (point Kavita at the same location).</Trans>
      </SettingsHelp>
      <Stack>
        {rootFolders && rootFolders.length > 0 && (
          <Table className="panel-table ops-table">
            <Table.Thead>
              <Table.Tr>
                <Table.Th><Trans>Path</Trans></Table.Th>
                <Table.Th><Trans>Free space</Trans></Table.Th>
                <Table.Th />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {rootFolders.map((f) => (
                <Table.Tr key={f.id}>
                  <Table.Td ff="monospace">
                    {f.path}
                    {!f.accessible && (
                      <Text span c="var(--danger)" size="xs" ml="xs">
                        <Trans>(inaccessible)</Trans>
                      </Text>
                    )}
                  </Table.Td>
                  <Table.Td>{formatBytes(f.freeSpace)}</Table.Td>
                  <Table.Td>
                    <ActionIcon
                      variant="subtle"
                      color="var(--danger)"
                      onClick={() =>
                        deleteFolder.mutate(f.id, {
                        })
                      }
                      aria-label={t`Delete root folder`}
                    >
                      <IconTrash size={16} />
                    </ActionIcon>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}
        <Group>
          <TextInput
            placeholder={t`C:\\Manga or /library`}
            value={newPath}
            onChange={(e) => setNewPath(e.currentTarget.value)}
            style={{ flex: 1 }}
          />
          <Button onClick={add} loading={addFolder.isPending}>
            <Trans>Add</Trans>
          </Button>
        </Group>
      </Stack>
    </Panel>
  )
}

function SourceLanguageSection() {
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
    <Panel>
      <Title order={4} mb="sm">
        <Trans>Languages</Trans>
      </Title>
      <SettingsHelp mb="md">
        <Trans>
          Languages to download, most preferred first. Auto-match tries sources that publish your
          top language first and skips sources that publish none of these. New auto-matched
          sources get these languages; existing mappings are never changed. Drag to reorder.
        </Trans>
      </SettingsHelp>
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
        <Text size="sm" c="var(--danger)" mb="md">
          <Trans>At least one language must stay enabled.</Trans>
        </Text>
      )}
      <Group justify="flex-end" mt="md">
        <SaveButton
          dirty={dirty}
          disabled={noneEnabled}
          loading={save.isPending}
          onClick={() =>
            order &&
            disabled &&
            save.mutate(
              { order, disabled, available: languages?.available ?? [] },
              { onSuccess: () => notifications.show({ message: now`Saved`, color: 'green' }) },
            )
          }
        />
      </Group>
    </Panel>
  )
}

function SourcePrioritySection() {
  const { t } = useLingui()
  const { data: sources } = useSources()
  const { data: priority } = useSourcePriority()
  const save = useSaveSourcePriority()
  const [order, setOrder] = useState<string[] | null>(null)
  const [disabled, setDisabled] = useState<string[] | null>(null)

  useEffect(() => {
    if (priority) {
      setOrder(priority.order)
      setDisabled(priority.disabled)
    }
  }, [priority])

  const displayName = (name: string) => sources?.find((s) => s.name === name)?.displayName ?? name
  const key = (list: string[]) => [...list].sort().join(',')
  const dirty =
    order !== null &&
    disabled !== null &&
    priority !== undefined &&
    (order.join(',') !== priority.order.join(',') || key(disabled) !== key(priority.disabled))

  return (
    <Panel>
      <Title order={4} mb="sm">
        <Trans>Sources</Trans>
      </Title>
      <SettingsHelp mb="md">
        <Trans>
          Download order when a series matches several sources, highest first. Language ranking
          above comes first. Applies to new auto-matches and manual Auto-match runs; other series
          keep their order.
          Switching a source off pauses it for every series without touching their own toggles.
          Drag to reorder.
        </Trans>
      </SettingsHelp>
      {order && disabled && (
        <PriorityList
          items={order}
          disabled={disabled}
          onChange={(nextOrder, nextDisabled) => {
            setOrder(nextOrder)
            setDisabled(nextDisabled)
          }}
          renderLabel={displayName}
          toggleLabel={(name) => {
            const sourceName = displayName(name)
            return t`Enable ${sourceName}`
          }}
          renderExtra={(name) => {
            const source = sources?.find((s) => s.name === name)
            const langs = source?.supportedLanguages.filter((lang) => lang !== 'en') ?? []
            return (
              <>
                <Text size="xs" c="var(--ink-3)">
                  {source?.baseUrl}
                </Text>
                {source?.needsFlareSolverr && (
                  <Badge size="sm" color="var(--warn)" variant="light">
                    <Trans>Needs FlareSolverr</Trans>
                  </Badge>
                )}
                {langs.length > 3 ? (
                  <Badge size="sm" color="var(--info)" variant="light">
                    <Trans>Multi-language</Trans>
                  </Badge>
                ) : (
                  langs.map((lang) => (
                    <Badge key={lang} size="sm" color="var(--info)" variant="light">
                      {languageName(lang)}
                    </Badge>
                  ))
                )}
              </>
            )
          }}
        />
      )}
      <Group justify="flex-end" mt="md">
        <SaveButton
          dirty={dirty}
          loading={save.isPending}
          onClick={() =>
            order &&
            disabled &&
            save.mutate(
              { order, disabled },
              { onSuccess: () => notifications.show({ message: now`Saved`, color: 'green' }) },
            )
          }
        />
      </Group>
    </Panel>
  )
}

function MetadataSection() {
  const { t } = useLingui()
  const { data: settings } = useMetadataSettings()
  const { data: progress } = useDumpProgress()
  const save = useSaveMetadataSettings()
  const refresh = useRefreshMetadataDump()
  // "checking" is the six-hourly checksum request; showing a bar for it would flash a download
  // that isn't happening. The toast applies the same rule, see MetadataDumpProgress.
  const downloading = Boolean(progress?.running && progress.phase !== 'checking')
  const lastError = progress?.lastError
  const snapshotSize = settings ? formatBytes(settings.dumpSizeBytes) : undefined
  const refreshedAtLabel = settings?.dumpRefreshedAt
    ? formatDateTime(settings.dumpRefreshedAt)
    : undefined

  return (
    <Panel>
      <Title order={4} mb="sm">
        <Trans>Metadata</Trans>
      </Title>
      <SettingsHelp mb="md">
        <Trans>
          Series metadata comes from MangaBaka. The local database keeps a nightly snapshot on
          disk (~3 GB) so search and imports skip the API's rate limit, and Discover needs it. The
          API is used until the first download finishes.
        </Trans>
      </SettingsHelp>
      <Stack gap="sm">
        <Switch
          label={t`Use local MangaBaka database`}
          checked={settings?.useLocalDb ?? true}
          onChange={(e) =>
            save.mutate(e.currentTarget.checked, {
            })
          }
        />
        {downloading && progress && <DumpProgressBar progress={progress} />}
        {!downloading && lastError && (
          <Text size="sm" c="var(--danger)">
            <Trans>Last download failed: {lastError}. The next scheduled run retries.</Trans>
          </Text>
        )}
        <Group justify="space-between">
          <Text size="sm" c="var(--ink-3)">
            {settings === undefined ? (
              '...'
            ) : settings.dumpPresent ? (
              refreshedAtLabel ? (
                <Trans>
                  Snapshot on disk: {snapshotSize}, refreshed {refreshedAtLabel}
                </Trans>
              ) : (
                <Trans>Snapshot on disk: {snapshotSize}, refreshed at an unknown time</Trans>
              )
            ) : downloading ? (
              <Trans>First download in progress</Trans>
            ) : (
              <Trans>No snapshot downloaded yet</Trans>
            )}
          </Text>
          <Button
            variant="default"
            size="xs"
            loading={refresh.isPending}
            disabled={downloading}
            onClick={() =>
              refresh.mutate(undefined, {
                onSuccess: (result) =>
                  notifications.show({
                    message: result.alreadyRunning
                      ? now`A refresh is already running`
                      : now`Refresh started, downloading in the background if a new snapshot is available`,
                    color: result.alreadyRunning ? 'gray' : 'green',
                  }),
              })
            }
          >
            <Trans>Refresh now</Trans>
          </Button>
        </Group>
      </Stack>
    </Panel>
  )
}

function RecommendationIndexSection() {
  const { data: status } = useRecommendationIndex()
  const setModel = useSetEmbeddingModel()  

  const selectModel = (kind: string) =>
    setModel.mutate(kind, {
      onSuccess: (r) =>
        notifications.show({
          message: r.switching
            ? kind === 'off'
              ? now`Turning embeddings off…`
              : now`Switching to ${kind}: downloading the model and index…`
            : r.reason,
          color: r.switching ? 'blue' : 'gray',
        }),
      onError: (e) => notifications.show({ message: String(e), color: 'red' }),
    })

  return (
    <Panel>
      <Title order={4} mb="sm">
        <Trans>Recommendations</Trans>
      </Title>
      <SettingsHelp mb="md">
        <Trans>
          A local embedding model lets Discover recommend by feel and search by description. The
          vectors download prebuilt, so this normally needs no attention. While it's off or still
          downloading, search falls back to titles and recommendations to genres.
        </Trans>
      </SettingsHelp>

      <RecommendationModelSwitch status={status} busy={setModel.isPending} onSelect={selectModel} />
    </Panel>
  )
}

/**
 * The library settings are one record with one PUT, and the three required fields have to travel
 * with every write. Same idea as useUiPatch: patch what changed, carry the rest over.
 */
function useLibraryPatch() {
  const { data: settings } = useLibrarySettings()
  const save = useSaveLibrarySettings()
  const patch = (changes: Partial<LibrarySettings>) =>
    save.mutate(
      {
        writeComicInfo: settings?.writeComicInfo ?? true,
        folderNamingMode: settings?.folderNamingMode ?? 'rename',
        writeCoverToFolder: settings?.writeCoverToFolder ?? false,
        ...changes,
      },
      { onSuccess: () => notifications.show({ message: now`Saved`, color: 'green' }) },
    )
  return { settings, patch }
}

/** What a series starts with when it is added or imported: the specials rule and the incognito rules. */
function NewSeriesDefaultsSection() {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const incognitoOptions = useIncognitoOptions()
  const { data: monitoring } = useMonitoringSettings()
  const saveMonitoring = useSaveMonitoringSettings()
  const { settings, patch } = useLibraryPatch()

  return (
    <Panel>
      <Title order={4} mb="sm">
        <Trans>New series defaults</Trans>
      </Title>
      <SettingsHelp mb="md">
        <Trans>
          What a series starts with when it is added or imported. Changing these never touches
          series already in the library.
        </Trans>
      </SettingsHelp>

      <Switch
        mb="lg"
        label={t`Don't want specials`}
        description={t`Decimal chapters (10.5, x.1) stay listed but are never downloaded or counted in the chapter total. Covers specials released later too.`}
        checked={monitoring?.unmonitorSpecials ?? false}
        onChange={(e) => saveMonitoring.mutate(e.currentTarget.checked)}
      />

      <Text fw={500} size="sm" mb={4}>
        <Trans>Incognito by content rating</Trans>
      </Text>
      <SettingsHelp mb="sm">
        <Trans>
          Pre-filled on the add form, where any single add can change it. "No scrobble" keeps a
          series off your trackers; "Full" also keeps it out of stats and history.
        </Trans>
      </SettingsHelp>
      <Stack gap="xs">
        {CONTENT_RATINGS.map((rating) => {
          const ratingLabel = renderLabel(CONTENT_RATING_LABELS[rating])
          return (
            <Group key={rating} gap="sm" wrap="nowrap">
              <Text size="sm" w={110} style={{ flexShrink: 0 }}>
                {ratingLabel}
              </Text>
              <Select
                aria-label={t`Incognito for ${ratingLabel}`}
                data={incognitoOptions}
                value={settings?.incognitoByRating?.[rating] ?? 'Off'}
                disabled={!settings}
                size="xs"
                w={170}
                onChange={(value) =>
                  patch({
                    incognitoByRating: {
                      ...(settings?.incognitoByRating ?? {}),
                      [rating]: (value as IncognitoMode | null) ?? 'Off',
                    },
                  })
                }
              />
            </Group>
          )
        })}
      </Stack>
    </Panel>
  )
}

function DiscoverSection() {
  const { data: settings } = useDiscoverSettings()
  const save = useSaveDiscoverSettings()

  return (
    <Panel>
      <Title order={4} mb="sm">
        <Trans>Content rating</Trans>
      </Title>
      <SettingsHelp mb="md">
        <Trans>
          The most explicit rating shown to you in search, Discover and recommendations. Everything
          up to and including it is allowed.
        </Trans>
      </SettingsHelp>
      <ContentRatingCards
        value={settings?.maxContentRating ?? 'erotica'}
        onChange={(rating) => save.mutate(rating)}
      />
    </Panel>
  )
}

/** What Maki writes into and next to the files: ComicInfo.xml and the folder poster. */
function LibraryFilesSection() {
  const { t } = useLingui()
  const { settings, patch } = useLibraryPatch()

  return (
    <Panel>
      <Title order={4} mb="sm">
        <Trans>Files</Trans>
      </Title>
      <SettingsHelp mb="md">
        <Trans>
          Writes a standard <Code>ComicInfo.xml</Code> into imported CBZs so Kavita groups and
          names chapters consistently. Off leaves torrent grabs and manual imports untouched.
          Maki's own downloads always get one, and PDFs never do. A series page's "Update
          ComicInfo" action standardizes one series later.
        </Trans>
      </SettingsHelp>
      <Switch
        mb="lg"
        label={t`Write ComicInfo.xml into imported files`}
        checked={settings?.writeComicInfo ?? true}
        onChange={(e) => patch({ writeComicInfo: e.currentTarget.checked })}
      />
      <Switch
        label={t`Save a cover.jpg into each series' library folder`}
        description={t`For readers like Komga and Kavita that pick up a poster from the folder. Runs right away when switched on.`}
        checked={settings?.writeCoverToFolder ?? false}
        onChange={(e) => patch({ writeCoverToFolder: e.currentTarget.checked })}
      />
    </Panel>
  )
}

function NamingSection() {
  const { t } = useLingui()
  const { settings, patch } = useLibraryPatch()
  const { data: allSeries } = useSeries()
  const seriesCount = allSeries?.length ?? 0
  const renameMany = useRenameManySeries()
  const [confirmRenameAll, setConfirmRenameAll] = useState(false)

  // Null means "not edited yet, show what's stored". Keeping the two apart is what lets the field
  // stay editable while a save is in flight without the response yanking the caret back.
  const [folderDraft, setFolderDraft] = useState<string | null>(null)
  const [chapterDraft, setChapterDraft] = useState<string | null>(null)
  const folderFormat = folderDraft ?? settings?.seriesFolderFormat ?? ''
  const chapterFormat = chapterDraft ?? settings?.chapterFormat ?? ''

  const [debouncedFolder] = useDebouncedValue(folderFormat, 350)
  const [debouncedChapter] = useDebouncedValue(chapterFormat, 350)
  const preview = useNamingPreview(debouncedFolder, debouncedChapter)
  const previewErrors = preview.data?.errors ?? []
  const folderError = previewErrors.find((e) => e.startsWith('Series folder format:'))
  const chapterError = previewErrors.find((e) => e.startsWith('Chapter format:'))
  const stale = debouncedFolder !== folderFormat || debouncedChapter !== chapterFormat

  const saveFormats = () => {
    // A stale preview doesn't block the save: the server validates too, and a commit that lands
    // inside the debounce window (closing the token picker right after inserting one) would
    // otherwise be dropped silently.
    if (!settings || (!stale && previewErrors.length > 0)) {
      return
    }

    if (
      folderFormat === settings.seriesFolderFormat &&
      chapterFormat === settings.chapterFormat
    ) {
      return
    }

    patch({ seriesFolderFormat: folderFormat, chapterFormat: chapterFormat })
  }

  return (
    <Panel>
      <Title order={4} mb="sm">
        <Trans>Naming</Trans>
      </Title>
      <SettingsHelp mb="md">
        <Trans>
          How Maki names series folders and the chapter files it downloads. The "?" button lists
          every token. Changes apply to new series and downloads; files already on disk stay put
          until you rename them from a series' page or with the button below.
        </Trans>
      </SettingsHelp>
      <Stack gap="md" mb="md">
        <NamingFormatInput
          label={t`Series Folder Format`}
          description={t`Used when adding a series, importing one, or renaming its folder`}
          value={folderFormat}
          example={preview.data?.seriesFolder}
          error={folderError?.replace('Series folder format: ', '')}
          onChange={setFolderDraft}
          onCommit={saveFormats}
        />
        <NamingFormatInput
          label={t`Chapter Format`}
          description={t`Used for chapters Maki downloads, and for imported files unless you keep their original names below`}
          value={chapterFormat}
          example={preview.data?.chapterFile}
          error={chapterError?.replace('Chapter format: ', '')}
          onChange={setChapterDraft}
          onCommit={saveFormats}
        />
      </Stack>

      <Button
        variant="default"
        size="xs"
        mb="lg"
        disabled={!allSeries?.length}
        onClick={() => setConfirmRenameAll(true)}
      >
        <Trans>Rename every series to current format</Trans>
      </Button>

      <Modal
        opened={confirmRenameAll}
        onClose={() => setConfirmRenameAll(false)}
        title={t`Rename every series`}
      >
        <Text size="sm" mb="md">
          <Trans>
            Applies the Series Folder Format and Chapter Format above to all {seriesCount} series
            in the library, renaming folders and files on disk to match. Series already matching
            the format are left alone. This can take a while for a large library.
          </Trans>
        </Text>
        <Group justify="flex-end">
          <Button variant="default" onClick={() => setConfirmRenameAll(false)}>
            <Trans>Cancel</Trans>
          </Button>
          <Button
            color="var(--danger)"
            loading={renameMany.isPending}
            onClick={() =>
              renameMany.mutate((allSeries ?? []).map((s) => s.id), {
                onSuccess: (results) => {
                  const renamed = results.filter((r) => r.applied).length
                  const failed = results.filter((r) => r.error).length
                  notifications.show({
                    // A plural even though "series" does not inflect in English: the count still
                    // drives the verb in Polish and Russian, and only an ICU plural gives them the
                    // categories to do it.
                    message:
                      failed > 0
                        ? now`Renamed ${renamed}, ${failed} failed`
                        : plural(renamed, { one: 'Renamed # series', other: 'Renamed # series' }),
                    color: failed > 0 ? 'yellow' : 'green',
                  })
                  setConfirmRenameAll(false)
                },
              })
            }
          >
            <Trans>Rename all</Trans>
          </Button>
        </Group>
      </Modal>

      <Text fw={500} size="sm" mb={4}>
        <Trans>Folder naming on import</Trans>
      </Text>
      <SettingsHelp mb="sm">
        <Trans>
          When importing an existing series from disk: rename its folder to the Series Folder
          Format, or keep it as found.
        </Trans>
      </SettingsHelp>
      <Radio.Group
        value={settings?.folderNamingMode ?? 'rename'}
        onChange={(value) => patch({ folderNamingMode: value as FolderNamingMode })}
      >
        <Stack gap="xs" mt="xs">
          <Radio value="rename" label={t`Rename folder to Maki standard`} />
          <Radio
            value="keep-new-standard"
            label={t`Keep folder name, but put new downloads in a Maki standard folder`}
          />
          <Radio value="keep-original" label={t`Keep folder name, and put new downloads there too`} />
        </Stack>
      </Radio.Group>

      <Text fw={500} size="sm" mt="lg" mb={4}>
        <Trans>File naming on import</Trans>
      </Text>
      <SettingsHelp mb="sm">
        <Trans>
          Off keeps a release's own file name, which often says more than the format can. Maki's
          own downloads always follow the format, and renaming a series from its page still
          renames everything in it.
        </Trans>
      </SettingsHelp>
      <Switch
        label={t`Rename imported files to the Chapter Format`}
        checked={settings?.renameImportedFiles ?? true}
        onChange={(e) => patch({ renameImportedFiles: e.currentTarget.checked })}
      />
    </Panel>
  )
}

/**
 * Keeping Maki's reader and Kavita in step. The reader's own settings live on the Reader card
 * (ReadingProfilesSection); this is only the two Kavita actions that used to sit underneath them.
 */
function KavitaSyncSection() {
  const { t } = useLingui()
  const { data: settings } = useReaderSettings()
  const save = useSaveReaderSettings()
  const { me } = useAuth()

  // Push-back and the read-status import are only meaningful for the account Kavita is bound to:
  // pushing somebody else's read would land the echo in a different high-water row and count every
  // chapter into Rewind twice.
  const ownsKavita = settings?.kavitaUserId != null && settings.kavitaUserId === me?.id

  return (
    <Panel>
      <Title order={4} mb="sm">
        <Trans>Kavita sync</Trans>
      </Title>

      <Stack gap="md">
        <div>
          <Switch
            label={t`Mark chapters read in Kavita too`}
            checked={settings?.pushToKavita ?? false}
            disabled={!ownsKavita || !settings}
            onChange={(e) =>
              settings &&
              save.mutate(
                { defaults: settings.defaults, pushToKavita: e.currentTarget.checked },
                { onSuccess: () => notifications.show({ message: now`Saved`, color: 'green' }) },
              )
            }
          />
          <Text size="xs" c="var(--ink-3)" mt={4}>
            <Trans>
              Finishing a chapter in Maki's reader also marks it read in Kavita. Only for series
              matched to a Kavita series. Stats never count a chapter twice.
            </Trans>
          </Text>
          {ownsKavita ? null : (
            <Text size="xs" c="var(--ink-3)" mt={4}>
              <Trans>
                Kavita's reading belongs to one Maki account, and it isn't yours. An admin can change
                that under Settings → Integrations → Kavita.
              </Trans>
            </Text>
          )}
        </div>

        {ownsKavita ? <KavitaReadImportControl /> : null}
      </Stack>
    </Panel>
  )
}

/**
 * OPDS is off until switched on, and enabling it is what mints the token, so the URL box only
 * appears once there is something real to copy.
 */
function OpdsSection() {
  const { t } = useLingui()
  const { data: opds } = useOpdsSettings()
  const save = useSaveOpdsSettings()
  const rotate = useRotateOpdsToken()
  const [rotateModalOpen, setRotateModalOpen] = useState(false)

  // The token itself is never stored, only its SHA-256 digest, so the full feed URL exists exactly
  // once, in the response that minted it. Held here for as long as the page stays open; after that
  // the only way to get a URL again is to regenerate, which is the same deal as any API key.
  const [revealedPath, setRevealedPath] = useState<string | null>(null)

  const enabled = opds?.enabled ?? false
  const trackProgress = opds?.trackProgress ?? true
  // The server emits a relative path on purpose (it can't know the host behind a reverse proxy),
  // so the address the user actually pastes is assembled here.
  const feedUrl = revealedPath ? `${window.location.origin}${revealedPath}` : null

  const saveWith = (patch: Partial<{ enabled: boolean; trackProgress: boolean }>) =>
    save.mutate(
      { enabled, trackProgress, ...patch },
      {
        onSuccess: (result) => {
          // Enabling for the first time mints the token, so this is the one save that reveals a URL.
          if (result.feedUrl) setRevealedPath(result.feedUrl)
          notifications.show({ message: now`Saved`, color: 'green' })
        },
      },
    )

  const copy = () => {
    if (!feedUrl) return
    void navigator.clipboard
      .writeText(feedUrl)
      .then(() => notifications.show({ message: now`Feed URL copied`, color: 'green' }))
  }

  return (
    <Panel>
      <Title order={4} mb="sm">
        OPDS
      </Title>
      <SettingsHelp mb="md">
        <Trans>
          Serves the library as an OPDS catalogue for reading apps like Panels, Chunky, KOReader
          and Mihon. Chapters download whole or stream a page at a time.
        </Trans>
      </SettingsHelp>

      <Stack gap="md">
        <div>
          <Switch
            label={t`Enable the OPDS catalogue`}
            checked={enabled}
            onChange={(e) => saveWith({ enabled: e.currentTarget.checked })}
          />
          <Text size="xs" c="var(--ink-3)" mt={4}>
            <Trans>
              Anyone with the feed URL can read the whole library. Regenerating it breaks the apps
              using it and nothing else.
            </Trans>
          </Text>
        </div>

        {enabled && (
          <div>
            <Text size="sm" fw={500} mb={4}>
              <Trans>Feed URL</Trans>
            </Text>
            {feedUrl ? (
              <>
                <Group gap="xs" wrap="nowrap">
                  <Code style={{ overflowWrap: 'anywhere' }}>{feedUrl}</Code>
                  <Tooltip label={t`Copy feed URL`}>
                    <ActionIcon variant="light" onClick={copy}>
                      <IconCopy size={16} />
                    </ActionIcon>
                  </Tooltip>
                </Group>
                <Alert color="var(--warn)" variant="light" mt="xs">
                  <Trans>
                    Copy this now. It can't be shown again; if you lose it, regenerate.
                  </Trans>
                </Alert>
                <Text size="xs" c="var(--ink-3)" mt={4}>
                  <Trans>
                    Add it to your reading app as an OPDS catalogue. From outside your network, swap
                    the host for the address you use there.
                  </Trans>
                </Text>
              </>
            ) : (
              <Group gap="xs" wrap="nowrap">
                <Code>{opds?.tokenPrefix ? `${opds.tokenPrefix}…` : t`none yet`}</Code>
                <Button
                  size="compact-xs"
                  variant="light"
                  color="var(--danger)"
                  leftSection={<IconRefresh size={14} />}
                  onClick={() => setRotateModalOpen(true)}
                >
                  <Trans>Regenerate</Trans>
                </Button>
              </Group>
            )}
          </div>
        )}

        {enabled && (
          <div>
            <Switch
              label={t`Track reading progress from OPDS`}
              checked={trackProgress}
              onChange={(e) => saveWith({ trackProgress: e.currentTarget.checked })}
            />
            <Text size="xs" c="var(--ink-3)" mt={4}>
              <Trans>
                Pages a streaming app fetches count as read, so OPDS reading reaches your library,
                Rewind and trackers. Turn it off if an app reports progress you didn't make; some
                fetch pages ahead.
              </Trans>
            </Text>
          </div>
        )}
      </Stack>

      <Modal
        opened={rotateModalOpen}
        onClose={() => setRotateModalOpen(false)}
        title={t`Regenerate OPDS token`}
        centered
      >
        <Stack>
          <Text size="sm">
            <Trans>
              The current feed URL stops working immediately. Every app using it needs the new
              one.
            </Trans>
          </Text>
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setRotateModalOpen(false)}>
              <Trans>Cancel</Trans>
            </Button>
            <Button
              color="var(--danger)"
              loading={rotate.isPending}
              onClick={() =>
                rotate.mutate(undefined, {
                  onSuccess: (result) => {
                    setRotateModalOpen(false)
                    // The only moment the new URL exists in a readable form.
                    setRevealedPath(result.feedUrl)
                    notifications.show({ message: now`New OPDS feed URL generated`, color: 'green' })
                  },
                })
              }
            >
              <Trans>Regenerate</Trans>
            </Button>
          </Group>
        </Stack>
      </Modal>
    </Panel>
  )
}

function KavitaImportResultSummary({
  result,
}: {
  result: { seriesMatched: number; chaptersMarked: number; seriesUnmatched: number }
}) {
  const { chaptersMarked, seriesMatched, seriesUnmatched } = result
  return (
    <Text size="xs" c="var(--ink-3)">
      {seriesUnmatched > 0 ? (
        <Trans>
          <Plural value={chaptersMarked} one="# chapter" other="# chapters" /> marked read across{' '}
          {seriesMatched} series, {seriesUnmatched} Kavita series unmatched
        </Trans>
      ) : (
        <Trans>
          <Plural value={chaptersMarked} one="# chapter" other="# chapters" /> marked read across{' '}
          {seriesMatched} series
        </Trans>
      )}
    </Text>
  )
}

function KavitaReadImportControl() {
  const { status, start } = useKavitaReadImport()
  const result = status?.result

  return (
    <div>
      <Text fw={500} size="sm" mb={4}>
        <Trans>Import read status from Kavita</Trans>
      </Text>
      <Text size="xs" c="var(--ink-3)" mb="sm">
        <Trans>
          Marks chapters you finished in Kavita as read here, so progress doesn't start from zero.
          Safe to rerun; it never unmarks anything. These reads stay out of Rewind because Kavita
          doesn't record when they happened.
        </Trans>
      </Text>
      <Group gap="sm">
        <Button
          variant="light"
          loading={status?.running ?? false}
          onClick={() =>
            start.mutate(undefined, {
              onError: (e) => notifications.show({ message: e.message, color: 'red' }),
            })
          }
        >
          <Trans>Import read status</Trans>
        </Button>
        {status?.running && (
          <Text size="xs" c="var(--ink-3)">
            <Trans>Reading progress from Kavita…</Trans>
          </Text>
        )}
        {!status?.running && status?.error && (
          <Text size="xs" c="var(--danger)">
            {status.error}
          </Text>
        )}
        {!status?.running && !status?.error && result && <KavitaImportResultSummary result={result} />}
      </Group>
    </div>
  )
}

function DownloadSection() {
  const { t } = useLingui()
  const { data: settings } = useDownloadSettings()
  const save = useSaveDownloadSettings()
  const [concurrentChapters, setConcurrentChapters] = useState<number | string>(2)
  // One number on screen, two fields on the wire: 0 means retry is off, and turning it off keeps the
  // stored cap so switching it back on doesn't forget what it was.
  const [retryAttempts, setRetryAttempts] = useState<number | string>(5)
  const [smartDownloadChaptersLeft, setSmartDownloadChaptersLeft] = useState<number | string>(5)
  const [smartDownloadChapters, setSmartDownloadChapters] = useState<number | string>(10)
  const [itemTimeoutMinutes, setItemTimeoutMinutes] = useState<number | string>(120)
  const [useHardlinks, setUseHardlinks] = useState(true)

  useEffect(() => {
    if (settings) {
      setConcurrentChapters(settings.concurrentChapters)
      setRetryAttempts(settings.retryEnabled ? settings.retryMaxAttempts : 0)
      setSmartDownloadChaptersLeft(settings.smartDownloadChaptersLeft)
      setSmartDownloadChapters(settings.smartDownloadChapters)
      setItemTimeoutMinutes(settings.itemTimeoutMinutes)
      setUseHardlinks(settings.useHardlinks)
    }
  }, [settings])

  const dirty =
    settings !== undefined &&
    (Number(concurrentChapters) !== settings.concurrentChapters ||
      Number(retryAttempts) !== (settings.retryEnabled ? settings.retryMaxAttempts : 0) ||
      Number(smartDownloadChaptersLeft) !== settings.smartDownloadChaptersLeft ||
      Number(smartDownloadChapters) !== settings.smartDownloadChapters ||
      Number(itemTimeoutMinutes) !== settings.itemTimeoutMinutes ||
      useHardlinks !== settings.useHardlinks)

  return (
    <Panel>
      <Title order={4} mb="sm">
        <Trans>Downloads</Trans>
      </Title>
      <SettingsHelp mb="md">
        <Trans>
          Chapters downloaded at once from scraper sources. More isn't always faster: tripping a
          site's rate limit pauses every download. Torrents aren't affected.
        </Trans>
      </SettingsHelp>
      <NumberInput
        label={t`Concurrent chapter downloads`}
        min={1}
        max={8}
        clampBehavior="strict"
        value={concurrentChapters}
        onChange={setConcurrentChapters}
        w={220}
        mb="md"
      />
      <Text fw={500} size="sm" mb={4}>
        Smart Download
      </Text>
      <SettingsHelp mb="xs">
        <Trans>
          Downloads the next chapters of a series when you're down to a few unread. Checks every
          five minutes against progress from Kavita or the built-in reader. Turn it on per series
          in its monitoring options.
        </Trans>
      </SettingsHelp>
      <Group align="flex-end" mb="md">
        <NumberInput
          label={t`Chapters unread before trigger`}
          min={1}
          max={10}
          clampBehavior="strict"
          value={smartDownloadChaptersLeft}
          onChange={setSmartDownloadChaptersLeft}
          w={220}
        />
        <NumberInput
          label={t`Chapters to download at once`}
          min={1}
          max={20}
          clampBehavior="strict"
          value={smartDownloadChapters}
          onChange={setSmartDownloadChapters}
          w={220}
        />
      </Group>
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
        w={220}
        mb="md"
      />
      <Text fw={500} size="sm" mb={4}>
        <Trans>Torrent imports</Trans>
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
        checked={useHardlinks}
        onChange={(e) => setUseHardlinks(e.currentTarget.checked)}
        mb="md"
      />
      <Text fw={500} size="sm" mb={4}>
        <Trans>Retry Handling</Trans>
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
      <Group justify="flex-end" mt="md">
        <SaveButton
          dirty={dirty}
          loading={save.isPending}
          onClick={() =>
            save.mutate(
              {
                concurrentChapters: Number(concurrentChapters),
                retryEnabled: Number(retryAttempts) > 0,
                retryMaxAttempts:
                  Number(retryAttempts) > 0 ? Number(retryAttempts) : (settings?.retryMaxAttempts ?? 5),
                smartDownloadChaptersLeft: Number(smartDownloadChaptersLeft),
                smartDownloadChapters: Number(smartDownloadChapters),
                itemTimeoutMinutes: Number(itemTimeoutMinutes),
                useHardlinks,
              },
              {
                onSuccess: () =>
                  notifications.show({ message: now`Saved`, color: 'green' }),
              },
            )
          }
        />
      </Group>
    </Panel>
  )
}

type RestoreTarget = { kind: 'existing'; name: string } | { kind: 'upload'; file: File }

function BackupSection() {
  const { t } = useLingui()
  const { data: backups } = useBackups()
  const { data: retentionSettings } = useBackupSettings()
  const create = useCreateBackup()
  const remove = useDeleteBackup()
  const restore = useRestoreBackup()
  const upload = useUploadRestore()
  const saveRetention = useSaveBackupSettings()

  const [retention, setRetention] = useState<number | string>(5)
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
      color: 'blue',
      autoClose: false,
    })

  const confirmRestore = () => {
    if (!target) return
    const onSuccess = () => {
      setTarget(null)
      restarting()
    }
    const onError = (e: Error) =>
      notifications.show({ title: now`Restore failed`, message: e.message, color: 'red' })

    if (target.kind === 'existing') restore.mutate(target.name, { onSuccess, onError })
    else upload.mutate(target.file, { onSuccess, onError })
  }

  return (
    <Panel>
      <Title order={4} mb="sm">
        <Trans>Backup &amp; Restore</Trans>
      </Title>
      <SettingsHelp mb="md">
        <Trans>
          A zip of your database and <Code>config.json</Code>: the library and every setting.
          Re-downloadable data such as the MangaBaka dump and covers is left out. One is taken
          automatically before every upgrade migration.
        </Trans>
      </SettingsHelp>
      <Alert color="var(--warn)" icon={<IconAlertTriangle size={16} />} mb="md" variant="light">
        <Trans>
          Backups hold API keys and passwords in plain text. Treat a downloaded one like a
          password.
        </Trans>
      </Alert>

      <Stack>
        {backups && backups.length > 0 && (
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
                  <Table.Td>{formatDateTime(b.manifest.createdUtc)}</Table.Td>
                  <Table.Td>
                    <Badge size="sm" variant="light" color={b.manifest.kind === 'auto' ? 'var(--neutral)' : 'var(--info)'}>
                      {b.manifest.kind}
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
        )}

        <Group>
          <Button
            onClick={() =>
              create.mutate(undefined, {
                onSuccess: () => notifications.show({ message: now`Backup created`, color: 'green' }),
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

        <Group align="flex-end">
          <NumberInput
            label={t`Backups to keep (per kind)`}
            min={1}
            max={50}
            clampBehavior="strict"
            value={retention}
            onChange={setRetention}
            w={220}
          />
          <SaveButton
            dirty={retentionDirty}
            loading={saveRetention.isPending}
            onClick={() =>
              saveRetention.mutate(
                { retention: Number(retention) },
                { onSuccess: () => notifications.show({ message: now`Saved`, color: 'green' }) },
              )
            }
          />
        </Group>
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
            <Button color="var(--danger)" loading={restore.isPending || upload.isPending} onClick={confirmRestore}>
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
    </Panel>
  )
}

function ProwlarrOptionsSection() {
  const { t } = useLingui()
  const { data: connection } = useConnectionSettings<Record<string, string | null>>('prowlarr')
  const configured = Boolean(connection?.url && connection?.apiKey)
  const { data: indexers, error: indexersError } = useProwlarrIndexers(configured)
  const indexersErrorMessage = indexersError != null ? String(indexersError) : null
  const { data: options } = useProwlarrOptions()
  const save = useSaveProwlarrOptions()
  const [selectedIndexers, setSelectedIndexers] = useState<Set<number>>(new Set())
  const [categories, setCategories] = useState<string[]>([])

  useEffect(() => {
    if (options) {
      setSelectedIndexers(
        new Set((options.indexerIds ?? '').split(',').filter(Boolean).map(Number)),
      )
      setCategories((options.categories ?? '').split(',').filter(Boolean))
    }
  }, [options])

  const sortedIds = (ids: Iterable<number>) => [...ids].sort((a, b) => a - b).join(',')
  const dirty =
    options !== undefined &&
    (sortedIds(selectedIndexers) !==
      sortedIds((options.indexerIds ?? '').split(',').filter(Boolean).map(Number)) ||
      categories.join(',') !== (options.categories ?? ''))

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
            Restrict release searches to specific indexers and Torznab categories. With nothing
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
          <Group justify="flex-end">
            <SaveButton
              dirty={dirty}
              loading={save.isPending}
              onClick={() =>
                save.mutate(
                  {
                    indexerIds: [...selectedIndexers].sort((a, b) => a - b).join(',') || null,
                    categories: categories.join(',') || null,
                  },
                  {
                    onSuccess: () => notifications.show({ message: now`Saved`, color: 'green' }),
                  },
                )
              }
            />
          </Group>
        </Stack>
      )}
    </Stack>
  )
}

function ScrobbleSection() {
  const { t } = useLingui()
  const { data } = useScrobbleSettings()
  const { data: status } = useScrobbleStatus()
  const save = useSaveScrobbleSettings()
  const [form, setForm] = useState<ScrobbleSettings | null>(null)

  useEffect(() => {
    if (data && form === null) setForm(data)
  }, [data, form])

  // The app registrations, interval and library filter belong to the instance. The server returns
  // them as null to anyone else and drops them on save, so a non-admin never sees the inputs.
  const isAdmin = data?.isAdmin ?? false

  const conn = (service: string) => status?.connections.find((c) => c.service === service)

  // Stored as a comma-separated id list. Ids Kavita no longer reports stay selectable, so a library
  // that is briefly missing isn't dropped from the filter by the next save.
  const { data: kavitaLibraries, error: kavitaLibrariesError } = useKavitaLibraries(isAdmin)
  const selectedLibraries = (form?.libraryIds ?? '').split(',').map((id) => id.trim()).filter(Boolean)
  const libraryOptions = [
    ...(kavitaLibraries ?? []).map((l) => ({ value: String(l.id), label: l.name ?? `#${l.id}` })),
    ...selectedLibraries
      .filter((id) => !(kavitaLibraries ?? []).some((l) => String(l.id) === id))
      .map((id) => ({ value: id, label: `#${id}` })),
  ]
  // A 400 here only ever means the Kavita connection isn't filled in yet, which is not an error.
  const kavitaNotSetUp = kavitaLibrariesError instanceof ApiError && kavitaLibrariesError.status === 400
  const kavitaLibrariesErrorMessage =
    kavitaLibrariesError != null && !kavitaNotSetUp ? kavitaLibrariesError.message : null

  const set = (patch: Partial<ScrobbleSettings>) =>
    setForm((f) => (f ? { ...f, ...patch } : f))
  const dirty = form !== null && data !== undefined && JSON.stringify(form) !== JSON.stringify(data)

  const origin = window.location.origin

  return (
    <Panel>
      <Title order={4} mb="xs">
        <Trans>Scrobbling</Trans>
      </Title>
      <SettingsHelp mb="sm">
        <Trans>
          Pushes your reading progress to your trackers. Connect your accounts and review matches
          on the Scrobble page.
        </Trans>
      </SettingsHelp>
      <Stack gap="xs">
        <Text size="sm" fw={600}>
          AniList
        </Text>
        {isAdmin && (
          <>
            <Text size="xs" c="var(--ink-3)">
              <Trans>
                Create an API client at anilist.co/settings/developer with redirect URL{' '}
                <Code>{origin}/api/v1/scrobble/oauth/anilist</Code>
              </Trans>
            </Text>
            <Group grow>
              <TextInput
                label={t`Client ID`}
                value={form?.aniListClientId ?? ''}
                onChange={(e) => set({ aniListClientId: e.currentTarget.value })}
              />
              <TextInput
                label={t`Client secret`}
                type="password"
                value={form?.aniListClientSecret ?? ''}
                onChange={(e) => set({ aniListClientSecret: e.currentTarget.value })}
              />
            </Group>
          </>
        )}
        <TrackerSyncControls service="anilist" label="AniList" connection={conn('anilist')} />

        <Text size="sm" fw={600} mt="xs">
          MyAnimeList
        </Text>
        {isAdmin && (
          <>
            <SettingsHelp>
              <Trans>
                Create an API client at myanimelist.net/apiconfig (App Type: web) with redirect URL{' '}
                <Code>{origin}/api/v1/scrobble/oauth/mal</Code>. If connecting ends in{' '}
                <Code>invalid_client</Code>, re-copy the Client ID (not the secret) and check the
                App Type is set.
              </Trans>
            </SettingsHelp>
            <Group grow>
              <TextInput
                label={t`Client ID`}
                value={form?.malClientId ?? ''}
                onChange={(e) => set({ malClientId: e.currentTarget.value })}
              />
              <TextInput
                label={t`Client secret`}
                type="password"
                value={form?.malClientSecret ?? ''}
                onChange={(e) => set({ malClientSecret: e.currentTarget.value })}
              />
            </Group>
          </>
        )}
        <TrackerSyncControls service="mal" label="MyAnimeList" connection={conn('mal')} />

        <Text size="sm" fw={600} mt="xs">
          MangaBaka
        </Text>
        <TextInput
          label={t`Personal Access Token`}
          description={t`From your MangaBaka settings. No OAuth needed.`}
          type="password"
          placeholder="mb-..."
          value={form?.mangaBakaToken ?? ''}
          onChange={(e) => set({ mangaBakaToken: e.currentTarget.value })}
        />
        <TrackerSyncControls service="mangabaka" label="MangaBaka" connection={conn('mangabaka')} />

        <Text size="sm" fw={600} mt="xs">
          Kitsu
        </Text>
        <Group grow>
          <TextInput
            label={t`Email`}
            value={form?.kitsuEmail ?? ''}
            onChange={(e) => set({ kitsuEmail: e.currentTarget.value })}
          />
          <TextInput
            label={t`Password`}
            type="password"
            value={form?.kitsuPassword ?? ''}
            onChange={(e) => set({ kitsuPassword: e.currentTarget.value })}
          />
        </Group>
        <TrackerSyncControls service="kitsu" label="Kitsu" connection={conn('kitsu')} />

        {isAdmin && (
          <Group grow mt="xs" align="flex-start">
            <NumberInput
              label={t`Sync interval (minutes)`}
              min={5}
              max={1440}
              clampBehavior="strict"
              value={form?.intervalMinutes ?? 30}
              onChange={(value) => set({ intervalMinutes: typeof value === 'number' ? value : 30 })}
            />
            <MultiSelect
              label={t`Kavita libraries`}
              description={
                kavitaNotSetUp
                  ? t`Set up the Kavita connection above to pick libraries.`
                  : t`Leave empty to scrobble every library.`
              }
              placeholder={selectedLibraries.length === 0 ? t`All libraries` : undefined}
              data={libraryOptions}
              value={selectedLibraries}
              onChange={(ids) => set({ libraryIds: ids.length > 0 ? ids.join(',') : null })}
              error={
                kavitaLibrariesErrorMessage != null
                  ? t`Could not load libraries from Kavita: ${kavitaLibrariesErrorMessage}`
                  : undefined
              }
              clearable
            />
          </Group>
        )}
        <Switch
          label={t`Add unread series as plan-to-read`}
          description={t`Kavita series you haven't started are added as plan to read. Entries already on your lists are never changed.`}
          checked={form?.planToRead ?? false}
          onChange={(e) => {
            const checked = e.currentTarget.checked
            set({ planToRead: checked })
          }}
        />
        <Group justify="flex-end">
          <SaveButton
            dirty={dirty}
            loading={save.isPending}
            onClick={() =>
              form &&
              save.mutate(form, {
                onSuccess: () => notifications.show({ message: now`Saved`, color: 'green' }),
              })
            }
          />
        </Group>
      </Stack>
    </Panel>
  )
}

/**
 * The UI settings are one record with one PUT, so each control has to send the *whole* thing.
 * This hook keeps every call site honest about that: patch what changed, carry the rest over.
 * Returns null while the settings are still loading, which is the caller's cue to stay read-only
 * rather than save a half-known record.
 */
function useUiPatch(): ((patch: Partial<UiSettings>) => void) | null {
  const { data: ui } = useUiSettings()
  const save = useSaveUiSettings()
  if (!ui) return null
  return (patch) => save.mutate({ ...ui, ...patch })
}

/**
 * Which page "/" opens on. Server-stored (unlike Appearance, which is per-browser), so it follows
 * the user across devices. Lives on the Home card because turning Home off is what changes it most.
 */
function StartPageSelect() {
  const { t } = useLingui()
  const { data: ui } = useUiSettings()
  const patch = useUiPatch()
  const { data: metadata } = useMetadataSettings()
  const discoverAvailable = Boolean(metadata?.useLocalDb && metadata?.dumpPresent)
  const homeEnabled = ui?.homeLayout.enabled ?? true

  return (
    <Select
      label={t`Start page`}
      description={t`Which page Maki opens on, on every device.`}
      data={[
        // Disabled rather than hidden, mirroring how the nav drops these tabs: offering a
        // choice that silently degrades to somewhere else is worse than saying why it's out.
        { value: 'home', label: t`Home`, disabled: !homeEnabled },
        { value: 'library', label: t`Library` },
        { value: 'discover', label: t`Discover`, disabled: !discoverAvailable },
      ]}
      value={ui?.startPage ?? 'home'}
      onChange={(value) => value && patch?.({ startPage: value as UiSettings['startPage'] })}
      disabled={!patch}
      allowDeselect={false}
      maw={260}
      mb="md"
    />
  )
}

/**
 * Which language the interface is drawn in.
 *
 * Sits directly above Title language because the two get confused, and the copy on both cards
 * exists to separate them: this one is the language of the app, that one is the language of the
 * metadata. Wanting Japanese titles inside a Swedish interface is ordinary, so neither derives from
 * the other.
 *
 * Server-stored, unlike Appearance: a translation is the sort of thing somebody wants on every
 * device they read on, not a per-browser choice. `localStorage` still holds a copy, but only so the
 * first paint does not have to wait for the settings round trip.
 */
function LanguageSection() {
  const { data: ui } = useUiSettings()
  const { locale, locales } = useLanguageChoice()
  const options = useLanguageOptions()
  const apply = useApplyLanguage()

  const currentLocaleLabel = locales.find((l) => l.code === locale)?.label ?? locale

  return (
    <Panel>
      <Title order={4} mb={4}>
        <Trans>Language</Trans>
      </Title>
      <SettingsHelp mb="sm">
        <Trans>
          The language of Maki's interface, on every device. Title language below is separate:
          it sets the language of series titles.
        </Trans>
      </SettingsHelp>
      <Select
        data={options}
        value={ui?.language ?? ''}
        onChange={(value) => value !== null && apply?.(value)}
        disabled={!apply}
        allowDeselect={false}
        maw={260}
      />
      <Text size="xs" c="var(--ink-3)" mt="sm">
        <Trans>
          Showing {currentLocaleLabel}. Non-English translations are machine-made and improving;
          anything untranslated shows in English.
        </Trans>
      </Text>
    </Panel>
  )
}

/**
 * Which language series titles are shown in.
 *
 * Deliberately display-only: it never touches `Series.Title`, which is what the folder on disk and
 * every file in it are named after, so one person's preference cannot rename another's library.
 * The visible cost is that sorting still follows the canonical (English) title.
 */
function TitleLanguageSection() {
  const { t } = useLingui()
  const { data: ui } = useUiSettings()
  const patch = useUiPatch()

  // The languages MangaBaka actually tags primary titles with, plus "native" for the
  // original-script title, which carries no code of its own.
  const options = [
    { value: '', label: t`English (provider default)` },
    { value: 'native', label: t`Original script` },
    { value: 'ja', label: t`Japanese` },
    { value: 'ko', label: t`Korean` },
    { value: 'zh', label: t`Chinese` },
    { value: 'es', label: t`Spanish` },
    { value: 'fr', label: t`French` },
    { value: 'de', label: t`German` },
    { value: 'it', label: t`Italian` },
    { value: 'pt-br', label: t`Portuguese (Br)` },
    { value: 'ru', label: t`Russian` },
  ]

  // Stored as an ordered list, and English is appended as the fallback so a series with no title in
  // the chosen language reads as English rather than as whatever the provider happened to list.
  const stored = ui?.titleLanguage ?? ''
  const primary = stored.split(',')[0] ?? ''

  return (
    <Panel>
      <Title order={4} mb={4}>
        <Trans>Title language</Trans>
      </Title>
      <SettingsHelp mb="sm">
        <Trans>
          Which language series titles are shown in, where the metadata provider has one. Display
          only: folders and file names keep the English title, and so does sorting.
        </Trans>
      </SettingsHelp>
      <Select
        data={options}
        value={primary}
        onChange={(value) =>
          patch?.({ titleLanguage: !value || value === 'en' ? '' : `${value},en` })
        }
        disabled={!patch}
        allowDeselect={false}
        maw={260}
      />
    </Panel>
  )
}

/**
 * The two supplementary rails on a series page. Both are extras around the chapter list and both
 * cost a catalogue query, so somebody who never uses them can turn them off and stop paying for them.
 */
function SeriesPageSection() {
  const { t } = useLingui()
  const { data: ui } = useUiSettings()
  const patch = useUiPatch()
  const sections = ui?.seriesSections
  const related = sections?.related !== false
  const similar = sections?.similar !== false

  const write = (next: Partial<SeriesSections>) =>
    patch?.({ seriesSections: { related, similar, ...next } })

  return (
    <Panel>
      <Title order={4} mb={4}>
        <Trans>Series page</Trans>
      </Title>
      <SettingsHelp mb="sm">
        <Trans>
          Which rails appear below the chapter list. Turning one off also stops it being fetched.
        </Trans>
      </SettingsHelp>
      <Stack gap="sm">
        <Switch
          checked={related}
          disabled={!patch}
          onChange={(e) => write({ related: e.currentTarget.checked })}
          label={t`Related series`}
          description={t`Sequels, prequels, spin-offs and side stories that MangaBaka has linked to this one.`}
        />
        <Switch
          checked={similar}
          disabled={!patch}
          onChange={(e) => write({ similar: e.currentTarget.checked })}
          label={t`More like this`}
          description={t`Titles that read alike, matched on feel rather than on a declared relation. Needs the recommendation index.`}
        />
      </Stack>
    </Panel>
  )
}

/**
 * Whether Home exists at all, and the way into the page layout editors. The sections themselves are
 * arranged on Home and Discover, in their edit mode, rather than from a list here.
 */
function HomeSectionsSection() {
  const { t } = useLingui()
  const { data: ui } = useUiSettings()
  const { data: metadata } = useMetadataSettings()
  const patch = useUiPatch()
  const homeEnabled = ui?.homeLayout.enabled ?? true
  const discoverAvailable = Boolean(metadata?.useLocalDb && metadata?.dumpPresent)

  return (
    <Panel>
      <Group justify="space-between" align="flex-start" wrap="nowrap" mb="sm">
        <div>
          <Title order={4} mb={4}>
            <Trans>Home &amp; start page</Trans>
          </Title>
          <SettingsHelp>
            <Trans>
              Arrange Home and Discover on the pages themselves: pick which sections show, drag them
              into order and add your own rails. Turn Home off if you don&apos;t read in Maki: the
              tab disappears and Library becomes the start page.
            </Trans>
          </SettingsHelp>
        </div>
        <Switch
          checked={homeEnabled}
          disabled={!patch || !ui}
          onChange={(e) =>
            ui && patch?.({ homeLayout: { ...ui.homeLayout, enabled: e.currentTarget.checked } })
          }
          aria-label={t`Enable the Home screen`}
        />
      </Group>

      <StartPageSelect />

      <Group gap="xs" mt="md">
        <Button
          component={Link}
          to="/home?edit=1"
          variant="default"
          leftSection={<IconLayoutDashboard size={16} />}
          disabled={!homeEnabled}
        >
          <Trans>Edit Home layout</Trans>
        </Button>
        {discoverAvailable && (
          <Button
            component={Link}
            to="/discover?edit=1"
            variant="default"
            leftSection={<IconLayoutDashboard size={16} />}
          >
            <Trans>Edit Discover layout</Trans>
          </Button>
        )}
      </Group>
    </Panel>
  )
}

function AppearanceSection() {
  const renderLabel = useLabel()
  const { themeId, setThemeId, presets } = useThemeChoice()

  return (
    <Panel>
      <Title order={4} mb={4}>
        <Trans>Appearance</Trans>
      </Title>
      <SettingsHelp mb="sm">
        <Trans>
          Pick an accent colour, the light theme, or match your system's light or dark mode.
          Remembered on this device.
        </Trans>
      </SettingsHelp>
      <Group gap="sm">
        {presets.map((p) => {
          const active = p.id === themeId
          return (
            <UnstyledButton
              key={p.id}
              onClick={() => setThemeId(p.id)}
              aria-pressed={active}
              style={{
                display: 'flex',
                alignItems: 'center',
                gap: 8,
                padding: '8px 12px',
                borderRadius: 10,
                border: `1px solid ${active ? 'var(--brand)' : 'var(--border)'}`,
                background: active ? 'var(--surface-hover)' : 'transparent',
                boxShadow: active ? '0 0 0 1px var(--brand)' : undefined,
              }}
            >
              <span
                style={{
                  width: 18,
                  height: 18,
                  borderRadius: '50%',
                  background: p.swatch,
                  border: '1px solid rgba(0,0,0,0.25)',
                  flexShrink: 0,
                }}
              />
              <Text size="sm" fw={active ? 600 : 500}>
                {renderLabel(p.label)}
              </Text>
              {active && <IconCheck size={14} style={{ color: 'var(--brand)' }} />}
            </UnstyledButton>
          )
        })}
      </Group>
    </Panel>
  )
}

function UpdatesSection() {
  const { t } = useLingui()
  const { data: settings } = useUpdateSettings()
  const save = useSaveUpdateSettings()
  const { data: status } = useUpdateStatus()
  const checkNow = useCheckForUpdatesNow()
  const latestVersion = status?.latestVersion
  const checkedAtLabel = status?.checkedAt ? formatDateTime(status.checkedAt) : undefined
  const howToUpdate = status?.isDocker
    ? t`pull the new image and recreate the container`
    : t`pull the latest code and rebuild`

  return (
    <Panel>
      <Title order={4} mb="sm">
        <Trans>Updates</Trans>
      </Title>
      <SettingsHelp mb="md">
        <Trans>
          Checks GitHub daily for a new release and shows a banner and a notification when there
          is one. Updating is manual: {howToUpdate}.
        </Trans>
      </SettingsHelp>
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
                    color: r.updateAvailable ? 'yellow' : 'green',
                  })
                },
              })
            }
          >
            <Trans>Check now</Trans>
          </Button>
        </Group>
      </Stack>
    </Panel>
  )
}

/**
 * The images Maki keeps on disk, and the one button that rebuilds them.
 *
 * Two different kinds of file behind one card: reader page thumbnails, which any request
 * regenerates on demand and so are only ever deleted, and series posters, which nothing regenerates
 * on its own — a poster lost to a failed download stays missing until something re-fetches it.
 * "Rebuild missing" is therefore the useful button most of the time; the forced pass exists for
 * artwork that is stale rather than broken, and costs a provider lookup and a download per series.
 */
function ImageCacheSection() {
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
          color: r.started ? 'green' : 'yellow',
        })
      },
      onError: (e) => notifications.show({ message: String(e), color: 'red' }),
    })

  return (
    <Panel>
      <Title order={4} mb="sm">
        <Trans>Image cache</Trans>
      </Title>
      <SettingsHelp mb="md">
        <Trans>
          Clears reader thumbnails and source-comparison samples, removes posters of deleted
          series, and re-downloads posters. Thumbnails regenerate the next time a chapter opens.
        </Trans>
      </SettingsHelp>

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
    </Panel>
  )
}

/**
 * Which Maki account Kavita's reading belongs to, shown inside the Kavita card. Instance-wide on purpose: Kavita is one server
 * reached with one API key, so everything it reports is a single person's reading and there is no way
 * to tell two Kavita users apart from here. Naming the owner is what keeps the adopt/merge/zero-delta
 * chain intact: the recurring pass, the read-status import, the per-chapter sync and the push-back
 * all act as the same user, so a chapter read in Maki and re-reported by Kavita counts once.
 */
function KavitaOwnerField() {
  const { t } = useLingui()
  const { data: bound } = useKavitaUser()
  const { data: users } = useUsers()
  const save = useSetKavitaUser()

  const options = (users ?? [])
    .filter((u) => !u.disabled && !u.pendingSetup)
    .map((u) => ({ value: String(u.id), label: u.displayName || u.userName }))

  return (
    <Select
      mt="md"
      maw={420}
      label={t`Attribute Kavita's reading to`}
      description={t`Unset means the lowest-numbered admin, which suits a single-user instance. Only this account can import from Kavita or push reads back.`}
      placeholder={t`Lowest-numbered admin`}
      clearable
      data={options}
      value={bound?.userId != null ? String(bound.userId) : null}
      onChange={(value) =>
        save.mutate(value === null ? null : Number(value), {
          onSuccess: () => notifications.show({ message: now`Saved`, color: 'green' }),
        })
      }
    />
  )
}

/**
 * Every card, keyed by its registry id. The registry decides order, tab and who may see it; this
 * only says how each id is built, so adding a setting is one entry there plus one line here.
 *
 * A hook rather than a module-scope table: the connection cards below carry
 * translated `title`/`description`/`fields` props, and a plain object literal would freeze those
 * in whatever language was active when the module first loaded.
 */
function useSectionNodes(): Record<string, ReactNode> {
  const { t, i18n } = useLingui()

  // Memoized so the elements keep their identity between renders, the way the module-scope table
  // used to. Without it every section subtree re-renders whenever anything on this page changes.
  // Keyed on the locale because that is the one thing that has to rebuild them.
  return useMemo<Record<string, ReactNode>>(
    () => ({
      account: <AccountSection />,
      'notification-prefs': <NotificationPrefsSection />,
      appearance: <AppearanceSection />,
      language: <LanguageSection />,
      'title-language': <TitleLanguageSection />,
      'home-screen': <HomeSectionsSection />,
      'series-page': <SeriesPageSection />,

      reader: <ReadingProfilesSection />,
      'kavita-sync': <KavitaSyncSection />,
      progress: <ProgressSection />,
      opds: <OpdsSection />,
      'discover-rating': <DiscoverSection />,

      'root-folders': <RootFoldersSection />,
      'library-files': <LibraryFilesSection />,
      naming: <NamingSection />,
      monitoring: <NewSeriesDefaultsSection />,
      metadata: <MetadataSection />,
      recommendations: <RecommendationIndexSection />,

      downloads: <DownloadSection />,
      sources: (
        <Stack gap="md">
          <SourceLanguageSection />
          <SourcePrioritySection />
        </Stack>
      ),
      flaresolverr: (
        <ConnectionSettingsCard
          name="flaresolverr"
          title="FlareSolverr"
          description={t`Needed for Cloudflare-protected sources like MangaFire. Point this at a running FlareSolverr instance.`}
          fields={[{ key: 'url', label: t`URL`, placeholder: 'http://localhost:8191' }]}
        />
      ),
      prowlarr: (
        <ConnectionSettingsCard
          name="prowlarr"
          title="Prowlarr"
          description={t`Searches your indexers for manga releases through Prowlarr's search API. No app sync needed.`}
          fields={[
            { key: 'url', label: t`URL`, placeholder: 'http://localhost:9696' },
            { key: 'apiKey', label: t`API key`, secret: true },
          ]}
        >
          <ProwlarrOptionsSection />
        </ConnectionSettingsCard>
      ),
      qbittorrent: (
        <ConnectionSettingsCard
          name="qbittorrent"
          title="qBittorrent"
          description={t`Download client for grabbed releases. Finished torrents import into the library automatically. Fill the path mapping only if qBittorrent reports paths Maki can't reach, e.g. /downloads in Docker where Maki sees Z:\\downloads.`}
          fields={[
            { key: 'url', label: t`URL`, placeholder: 'http://localhost:8080' },
            { key: 'username', label: t`Username` },
            { key: 'password', label: t`Password`, secret: true },
            { key: 'category', label: t`Category`, placeholder: 'maki' },
            { key: 'pathMapFrom', label: t`Path mapping - qBittorrent side`, placeholder: t`/downloads (optional)` },
            { key: 'pathMapTo', label: t`Path mapping - Maki side`, placeholder: t`Z:\\downloads (optional)` },
          ]}
        />
      ),

      kavita: (
        <ConnectionSettingsCard
          name="kavita"
          title="Kavita"
          description={t`Maki asks Kavita to scan a series after its files change, then pushes its poster, links and status. Covers you set in Kavita are kept. The API key is under User Settings → 3rd Party Clients in Kavita. Fill the path mapping only if Kavita sees the library under a different path, e.g. in Docker.`}
          fields={[
            { key: 'url', label: t`URL`, placeholder: 'http://localhost:5000' },
            { key: 'apiKey', label: t`API key`, secret: true },
            { key: 'pathMapFrom', label: t`Path mapping - Maki side`, placeholder: t`C:\\Manga (optional)` },
            { key: 'pathMapTo', label: t`Path mapping - Kavita side`, placeholder: t`/manga (optional)` },
          ]}
        >
          <KavitaOwnerField />
        </ConnectionSettingsCard>
      ),
      scrobbling: <ScrobbleSection />,
      notifications: <NotificationsSection />,

      users: <UsersSection />,
      security: <SecuritySection />,
      oidc: <OidcSection />,

      backup: <BackupSection />,
      'image-cache': <ImageCacheSection />,
      updates: <UpdatesSection />,
    }),
    [t, i18n.locale],
  )
}

export default function SettingsPage() {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const { me, can } = useAuth()
  const isAdmin = me?.isAdmin ?? false
  const [searchParams, setSearchParams] = useSearchParams()
  const sectionNodes = useSectionNodes()
  const completeSetup = useCompleteSetup()

  // Which cards this account may see at all. Everything an admin-only card writes is rejected by
  // the server for anyone else, so rendering one would just fill the page with failed requests.
  const visible = useMemo(
    () => SETTINGS_ENTRIES.filter((e) => entryVisible(e, isAdmin, can)),
    [isAdmin, can],
  )
  const tabs = useMemo(
    () => SETTINGS_TABS.filter((t) => visible.some((e) => e.tab === t.key)),
    [visible],
  )

  // The tab lives in the URL rather than in state so a deep link from the command palette lands on
  // the right one, and so the panel holding the target card is mounted by the time the scroll effect
  // below runs.
  const requested = searchParams.get('tab')
  const activeTab = tabs.some((t) => t.key === requested) ? requested! : (tabs[0]?.key ?? 'account')
  const tabEntries = useMemo(() => visible.filter((e) => e.tab === activeTab), [visible, activeTab])

  // Panels unmount on a tab change (`keepMounted={false}`), which used to drop half-typed edits
  // without a word. Cards report through SaveButton; a switch away from unsaved edits asks first.
  const unsaved = useRef(new Set<string>())
  const [unsavedCount, setUnsavedCount] = useState(0)
  const reportUnsaved = useCallback((id: string, dirty: boolean) => {
    if (dirty) unsaved.current.add(id)
    else unsaved.current.delete(id)
    setUnsavedCount(unsaved.current.size)
  }, [])
  const [pendingTab, setPendingTab] = useState<string | null>(null)
  const switchTab = (value: string) => setSearchParams({ tab: value })

  useEffect(() => {
    if (unsavedCount === 0) return
    const warn = (event: BeforeUnloadEvent) => event.preventDefault()
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [unsavedCount])

  const target = searchParams.get('s')
  useEffect(() => {
    if (!target) return
    // Consumed immediately, so picking the same entry twice in a row flashes it twice. This also
    // re-runs the effect with no target, which is why nothing below is torn down on cleanup: the
    // scroll and the flash have to outlive the render that clears the parameter.
    setSearchParams(
      (current) => {
        const next = new URLSearchParams(current)
        next.delete('s')
        return next
      },
      { replace: true },
    )

    const el = document.getElementById(`setting-${target}`)
    if (!el) return
    const show = () => el.scrollIntoView({ block: 'center', behavior: 'smooth' })
    // Cards above the target fill in as their queries resolve (the source table, the indexer list),
    // which pushes it down after the first scroll lands. Re-anchoring twice costs nothing and is
    // what makes a deep link arrive at the card rather than somewhere above it.
    show()
    window.setTimeout(show, 400)
    window.setTimeout(show, 1000)
    el.classList.add('settings-flash')
    window.setTimeout(() => el.classList.remove('settings-flash'), 2200)
  }, [target, setSearchParams])

  return (
    <SurfaceFrame pageStyle="operational" className="settings-surface">
      <PageHeader
        compact
        title={t`Settings`}
        description={
          isAdmin
            ? t`Storage, metadata, download clients and integrations for your Maki instance.`
            : t`Your account and how Maki looks.`
        }
        actions={
          isAdmin && (
            <Button
              variant="default"
              size="xs"
              loading={completeSetup.isPending}
              onClick={() => completeSetup.mutate(false)}
            >
              <Trans>Run setup guide</Trans>
            </Button>
          )
        }
      />
      <Tabs
        value={activeTab}
        variant="unstyled"
        classNames={{ list: 'series-tabs page-tabs', tab: 'series-tab' }}
        onChange={(value) => {
          if (!value || value === activeTab) return
          if (unsaved.current.size > 0) setPendingTab(value)
          else switchTab(value)
        }}
        keepMounted={false}
      >
        <Tabs.List>
          {tabs.map((tab) => (
            <Tabs.Tab key={tab.key} value={tab.key}>
              {renderLabel(tab.label)}
            </Tabs.Tab>
          ))}
        </Tabs.List>

        <UnsavedSettingsContext value={reportUnsaved}>
          {tabs.map((tab) => (
            <Tabs.Panel key={tab.key} value={tab.key}>
              <div className="settings-layout">
                <Stack className="settings-content">
                  <Text size="sm" c="var(--ink-3)">
                    {renderLabel(tab.description)}
                  </Text>
                  {tabEntries.map((entry) => (
                    <div key={entry.id} id={`setting-${entry.id}`} style={{ scrollMarginTop: 80 }}>
                      {sectionNodes[entry.id]}
                    </div>
                  ))}
                </Stack>
                <SettingsIndex entries={tabEntries} />
              </div>
            </Tabs.Panel>
          ))}
        </UnsavedSettingsContext>
      </Tabs>

      <Modal
        opened={pendingTab !== null}
        onClose={() => setPendingTab(null)}
        title={t`Discard unsaved changes?`}
        size="sm"
      >
        <Stack gap="md">
          <Text size="sm">
            <Plural
              value={unsavedCount}
              one="A card on this tab has changes that are not saved. Leaving the tab drops them."
              other="# cards on this tab have changes that are not saved. Leaving the tab drops them."
            />
          </Text>
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setPendingTab(null)}>
              <Trans>Keep editing</Trans>
            </Button>
            <Button
              color="var(--danger)"
              onClick={() => {
                if (pendingTab) switchTab(pendingTab)
                setPendingTab(null)
              }}
            >
              <Trans>Discard changes</Trans>
            </Button>
          </Group>
        </Stack>
      </Modal>
    </SurfaceFrame>
  )
}
