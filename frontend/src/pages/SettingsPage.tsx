import { useEffect, useMemo, useRef, useState, type DragEvent, type ReactNode } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useLabel, useLanguageChoice } from '../i18n-context'
import { useDebouncedValue } from '@mantine/hooks'
import { Trans, Plural, useLingui } from '@lingui/react/macro'
import { plural, t as now } from '@lingui/core/macro'
import {
  ActionIcon,
  Alert,
  Badge,
  Button,
  Card,
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
  Slider,
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
  IconChevronDown,
  IconChevronUp,
  IconCopy,
  IconDownload,
  IconGripVertical,
  IconRefresh,
  IconTrash,
  IconUpload,
} from '@tabler/icons-react'
import { notifications } from '@mantine/notifications'
import { PageHeader } from '../components/ui/PageHeader'
import { RecommendationModelCards } from '../components/RecommendationModelCards'
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
  useFlareSolverrSettings,
  useGeneralSettings,
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
  useSaveFlareSolverr,
  useSaveMetadataSettings,
  useSaveMonitoringSettings,
  useSaveProwlarrOptions,
  useSaveScrobbleSettings,
  useSaveSourceLanguages,
  useSaveSourcePriority,
  useSaveUiSettings,
  useUiSettings,
  HOME_SECTION_LABELS,
  type HomeSection,
  type SeriesSections,
  type UiSettings,
  useSetEmbeddingModel,
  useScrobbleSettings,
  useScrobbleStatus,
  useSourceLanguages,
  useSourcePriority,
  useSources,
  useTestFlareSolverr,
  useCheckForUpdatesNow,
  useImageCache,
  useRebuildImageCache,
  useRenameManySeries,
  useSaveUpdateSettings,
  useSeries,
  useUpdateSettings,
  useUpdateStatus,
  type FolderNamingMode,
  type ScrobbleSettings,
} from '../api/hooks'
import { useKavitaReadImport, useReaderSettings, useSaveReaderSettings } from '../api/reader'
import { DEFAULT_PREFS, type ReaderPrefs } from './reader/prefs'
import { ConnectionSettingsCard } from '../components/ConnectionSettingsCard'
import { DumpProgressBar } from '../components/MetadataDumpProgress'
import { languageName } from '../api/titles'
import { NotificationsSection } from '../components/NotificationsSection'
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
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        <Trans>Root Folders</Trans>
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        <Trans>Library folders where series are stored (point Kavita at the same location).</Trans>
      </Text>
      <Stack>
        {rootFolders && rootFolders.length > 0 && (
          <Table>
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
                  <Table.Td>
                    {f.path}
                    {!f.accessible && (
                      <Text span c="red" size="xs" ml="xs">
                        <Trans>(inaccessible)</Trans>
                      </Text>
                    )}
                  </Table.Td>
                  <Table.Td>{formatBytes(f.freeSpace)}</Table.Td>
                  <Table.Td>
                    <ActionIcon
                      variant="subtle"
                      color="red"
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
    </Card>
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
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        <Trans>Languages</Trans>
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        <Trans>
          Which languages to download, most preferred first. When auto-matching, every source is
          ranked by the highest language on this list that it publishes, so a source carrying your
          top language is tried before one that does not. Sources publishing none of the enabled
          languages are skipped by auto-matching entirely. Drag to reorder.
        </Trans>
      </Text>
      <Text size="sm" c="dimmed" mb="md">
        <Trans>
          Sources with a language picker get it set to these languages when a mapping is created
          automatically. Mappings that already exist are never rewritten, and switching a language on
          or off never switches a source on or off.
        </Trans>
      </Text>
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
        <Text size="sm" c="red" mb="md">
          <Trans>At least one language must stay enabled.</Trans>
        </Text>
      )}
      <Button
        variant="default"
        disabled={!dirty || noneEnabled}
        loading={save.isPending}
        onClick={() =>
          order &&
          disabled &&
          save.mutate(
            { order, disabled, available: languages?.available ?? [] },
            { onSuccess: () => notifications.show({ message: now`Saved`, color: 'green' }) },
          )
        }
      >
        <Trans>Save</Trans>
      </Button>
    </Card>
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
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        <Trans>Sources</Trans>
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        <Trans>
          When a series auto-matches multiple sources, chapters download from the highest-priority
          enabled source first. Applies to new auto-matches and manual "Auto-match" runs; existing
          series mappings keep their current priorities. Drag to reorder.
        </Trans>
      </Text>
      <Text size="sm" c="dimmed" mb="md">
        <Trans>
          A source publishing a higher-ranked language is ranked ahead of this list when
          auto-matching.
        </Trans>
      </Text>
      <Text size="sm" c="dimmed" mb="md">
        <Trans>
          Switching a source off skips it when auto-matching and stops every series from using it,
          without changing the per-series toggles: turn it back on and each series picks up exactly
          where it was.
        </Trans>
      </Text>
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
                <Text size="xs" c="dimmed">
                  {source?.baseUrl}
                </Text>
                {source?.needsFlareSolverr && (
                  <Badge size="sm" color="orange" variant="light">
                    <Trans>Needs FlareSolverr</Trans>
                  </Badge>
                )}
                {langs.length > 3 ? (
                  <Badge size="sm" color="blue" variant="light">
                    <Trans>Multi-language</Trans>
                  </Badge>
                ) : (
                  langs.map((lang) => (
                    <Badge key={lang} size="sm" color="blue" variant="light">
                      {languageName(lang)}
                    </Badge>
                  ))
                )}
              </>
            )
          }}
        />
      )}
      <Button
        variant="default"
        disabled={!dirty}
        loading={save.isPending}
        onClick={() =>
          order &&
          disabled &&
          save.mutate(
            { order, disabled },
            { onSuccess: () => notifications.show({ message: now`Saved`, color: 'green' }) },
          )
        }
      >
        <Trans>Save</Trans>
      </Button>
    </Card>
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
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        <Trans>Metadata</Trans>
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        <Trans>
          Series metadata comes from MangaBaka. With the local database enabled, Maki keeps a
          nightly snapshot on disk (~3 GB) so searches and library imports are instant instead of
          rate-limited. Until the first download finishes, the API is used automatically.
        </Trans>
      </Text>
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
          <Text size="sm" c="red">
            <Trans>Last download failed: {lastError}. The next scheduled run retries.</Trans>
          </Text>
        )}
        <Group justify="space-between">
          <Text size="sm" c="dimmed">
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
    </Card>
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
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        <Trans>Recommendations</Trans>
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        <Trans>
          Discover recommends by semantic "feel" and searches by description, using a local
          embedding model. The vectors download prebuilt, so this normally needs no attention;
          search falls back to titles and recommendations to genres whenever it's off or still
          downloading.
        </Trans>
      </Text>

      <RecommendationModelCards status={status} busy={setModel.isPending} onSelect={selectModel} />
    </Card>
  )
}

function MonitoringSection() {
  const { t } = useLingui()
  const { data: settings } = useMonitoringSettings()
  const save = useSaveMonitoringSettings()

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        <Trans>Monitoring</Trans>
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        <Trans>
          Specials are decimal chapters (10.5 omake, x.1/x.2 splits). When enabled, specials on
          newly added or imported series are marked "not wanted": they stay listed, but they never
          download and they don't count toward the series' chapter total. Applies as each chapter
          is discovered, so specials released later are covered too. Existing chapters are
          unaffected; change them on the series page or in bulk from its Chapters tab.
        </Trans>
      </Text>
      <Switch
        label={t`Don't want specials on new series`}
        checked={settings?.unmonitorSpecials ?? false}
        onChange={(e) =>
          save.mutate(e.currentTarget.checked, {
          })
        }
      />
    </Card>
  )
}

function DiscoverSection() {
  const { data: settings } = useDiscoverSettings()
  const save = useSaveDiscoverSettings()

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        Discover
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        <Trans>
          Highest content rating shown in "Add Series" search results, everything up to and
          including it is allowed. Discover and recommendations never surface pornographic titles
          regardless of this setting.
        </Trans>
      </Text>
      <ContentRatingCards
        value={settings?.maxContentRating ?? 'erotica'}
        onChange={(rating) => save.mutate(rating)}
      />
    </Card>
  )
}

function LibrarySection() {
  const { t } = useLingui()
  const incognitoOptions = useIncognitoOptions()
  const { data: settings } = useLibrarySettings()
  const save = useSaveLibrarySettings()
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
    // A stale preview doesn't block the save — the server validates too, and a commit that lands
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

    save.mutate(
      {
        writeComicInfo: settings.writeComicInfo,
        folderNamingMode: settings.folderNamingMode,
        writeCoverToFolder: settings.writeCoverToFolder ?? false,
        seriesFolderFormat: folderFormat,
        chapterFormat: chapterFormat,
      },
      { onSuccess: () => notifications.show({ message: now`Saved`, color: 'green' }) },
    )
  }

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        <Trans>Library files</Trans>
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        <Trans>
          Maki writes a standardized <Code>ComicInfo.xml</Code> into each CBZ so Kavita groups and
          names chapters consistently. Turn this off to leave imported files (torrent grabs and
          manual imports) exactly as they came; chapters Maki downloads itself from a source still
          get a ComicInfo, since Maki builds those files. You can always standardize a single
          series later with the "Update ComicInfo" bulk action on its page.
        </Trans>
      </Text>
      <Switch
        mb="lg"
        label={t`Write ComicInfo.xml into imported files`}
        checked={settings?.writeComicInfo ?? true}
        onChange={(e) =>
          save.mutate(
            {
              writeComicInfo: e.currentTarget.checked,
              folderNamingMode: settings?.folderNamingMode ?? 'rename',
              writeCoverToFolder: settings?.writeCoverToFolder ?? false,
            },
            { onSuccess: () => notifications.show({ message: now`Saved`, color: 'green' }) },
          )
        }
      />

      <Switch
        mb="lg"
        label={t`Save a cover.jpg into each series' library folder`}
        description={t`For other readers (Komga, Kavita) that read a poster placed directly in the folder. Will run immediately when switched on.`}
        checked={settings?.writeCoverToFolder ?? false}
        onChange={(e) =>
          save.mutate(
            {
              writeComicInfo: settings?.writeComicInfo ?? true,
              folderNamingMode: settings?.folderNamingMode ?? 'rename',
              writeCoverToFolder: e.currentTarget.checked,
            },
            { onSuccess: () => notifications.show({ message: now`Saved`, color: 'green' }) },
          )
        }
      />

      <Text fw={500} size="sm" mb={4}>
        <Trans>Naming</Trans>
      </Text>
      <Text size="sm" c="dimmed" mb="sm">
        <Trans>
          How Maki names a series' folder and the chapter files it downloads. Both take tokens;
          the "?" button lists every one with an example, and its dialog is directly editable too.
          A change applies to series added and chapters downloaded from here on. Nothing already
          on disk moves until you rename it, either from a series' page or with the button below
          for the whole library.
        </Trans>
      </Text>
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
            color="red"
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
      <Text size="sm" c="dimmed" mb="sm">
        <Trans>
          Only affects importing an existing series from disk: whether Maki renames its current
          folder to match the Series Folder Format above, or leaves it as found.
        </Trans>
      </Text>
      <Radio.Group
        value={settings?.folderNamingMode ?? 'rename'}
        onChange={(value) =>
          save.mutate(
            {
              writeComicInfo: settings?.writeComicInfo ?? true,
              folderNamingMode: value as FolderNamingMode,
              writeCoverToFolder: settings?.writeCoverToFolder ?? false,
            },
            { onSuccess: () => notifications.show({ message: now`Saved`, color: 'green' }) },
          )
        }
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
      <Text size="sm" c="dimmed" mb="sm">
        <Trans>
          Whether files Maki adopts from disk are renamed to the Chapter Format above. A scene
          release's own name often carries more than the format can say (the edition, the group,
          the year), so turning this off keeps what the release named it. Chapters Maki downloads
          itself are always named by the format, and renaming a series from its own page still
          renames everything in it.
        </Trans>
      </Text>
      <Switch
        mb="lg"
        label={t`Rename imported files to the Chapter Format`}
        checked={settings?.renameImportedFiles ?? true}
        onChange={(e) =>
          save.mutate(
            {
              writeComicInfo: settings?.writeComicInfo ?? true,
              folderNamingMode: settings?.folderNamingMode ?? 'rename',
              writeCoverToFolder: settings?.writeCoverToFolder ?? false,
              renameImportedFiles: e.currentTarget.checked,
            },
            { onSuccess: () => notifications.show({ message: now`Saved`, color: 'green' }) },
          )
        }
      />

      <Text fw={500} size="sm" mt="lg" mb={4}>
        <Trans>Incognito by content rating</Trans>
      </Text>
      <Text size="sm" c="dimmed" mb="sm">
        <Trans>
          What the incognito setting is pre-filled with when a series of each rating is added.
          "No scrobble" keeps it off your trackers; "Full" also keeps it out of stats and reading
          history. The add form still shows the value, so any single add can override it, and
          changing a rule here never touches a series already in the library.
        </Trans>
      </Text>
      <Stack gap="xs">
        {CONTENT_RATINGS.map((rating) => (
          <Group key={rating} gap="sm" wrap="nowrap">
            <Text size="sm" tt="capitalize" w={110} style={{ flexShrink: 0 }}>
              {rating}
            </Text>
            <Select
              aria-label={t`Incognito for ${rating}`}
              data={incognitoOptions}
              value={settings?.incognitoByRating?.[rating] ?? 'Off'}
              disabled={!settings}
              size="xs"
              w={170}
              onChange={(value) =>
                save.mutate(
                  {
                    writeComicInfo: settings?.writeComicInfo ?? true,
                    folderNamingMode: settings?.folderNamingMode ?? 'rename',
                    writeCoverToFolder: settings?.writeCoverToFolder ?? false,
                    incognitoByRating: {
                      ...(settings?.incognitoByRating ?? {}),
                      [rating]: (value as IncognitoMode | null) ?? 'Off',
                    },
                  },
                  { onSuccess: () => notifications.show({ message: now`Saved`, color: 'green' }) },
                )
              }
            />
          </Group>
        ))}
      </Stack>
    </Card>
  )
}

function ReaderSection() {
  const { t } = useLingui()
  const { data: settings } = useReaderSettings()
  const save = useSaveReaderSettings()
  const { me } = useAuth()
  const defaults = settings?.defaults ?? DEFAULT_PREFS
  const [scale, setScale] = useState(defaults.scale)
  useEffect(() => setScale(defaults.scale), [defaults.scale])

  // Push-back and the read-status import are only meaningful for the account Kavita is bound to:
  // pushing somebody else's read would land the echo in a different high-water row and count every
  // chapter into Rewind twice.
  const ownsKavita = settings?.kavitaUserId != null && settings.kavitaUserId === me?.id

  const saveWith = (patch: Partial<typeof defaults>, pushToKavita?: boolean) =>
    save.mutate(
      { defaults: { ...defaults, ...patch }, pushToKavita: pushToKavita ?? settings?.pushToKavita ?? false },
      { onSuccess: () => notifications.show({ message: now`Saved`, color: 'green' }) },
    )

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        <Trans>Reader</Trans>
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        <Trans>
          The fallback for Maki's built-in reader: what a series gets when no reading profile
          covers its type and nothing is pinned or overridden on the series itself.
        </Trans>
      </Text>

      <Stack gap="md">
        <Radio.Group
          label={t`Layout`}
          value={defaults.mode}
          onChange={(value) => saveWith({ mode: value as ReaderPrefs['mode'] })}
        >
          <Stack gap="xs" mt="xs">
            <Radio value="paged" label={t`Single page`} />
            <Radio value="double" label={t`Two pages side by side`} />
            <Radio value="vertical" label={t`Continuous vertical (webtoon)`} />
          </Stack>
        </Radio.Group>

        <Radio.Group
          label={t`Reading direction`}
          value={defaults.direction}
          onChange={(value) => saveWith({ direction: value as ReaderPrefs['direction'] })}
        >
          <Stack gap="xs" mt="xs">
            <Radio value="rtl" label={t`Right to left (manga)`} />
            <Radio value="ltr" label={t`Left to right`} />
          </Stack>
        </Radio.Group>

        <Radio.Group
          label={t`Page fit`}
          value={defaults.fit}
          onChange={(value) => saveWith({ fit: value as ReaderPrefs['fit'] })}
        >
          <Stack gap="xs" mt="xs">
            <Radio value="height" label={t`Fit height`} />
            <Radio value="width" label={t`Fit width`} />
            <Radio value="screen" label={t`Fit screen`} />
            <Radio value="original" label={t`Original size`} />
          </Stack>
        </Radio.Group>

        {defaults.fit === 'original' && (
          <div>
            <Text size="sm" fw={500} mb={4}>
              <Trans>Scale ({scale}%)</Trans>
            </Text>
            <Slider min={25} max={400} step={5} value={scale} onChange={setScale} onChangeEnd={(value) => saveWith({ scale: value })} />
          </div>
        )}

        <Switch
          label={t`Advance to the next chapter at the end`}
          checked={defaults.autoNextChapter}
          onChange={(e) => saveWith({ autoNextChapter: e.currentTarget.checked })}
        />
        <Switch
          label={t`Tap zones (click the page edges to turn)`}
          checked={defaults.tapZones}
          onChange={(e) => saveWith({ tapZones: e.currentTarget.checked })}
        />
        <div>
          <Switch
            label={t`Flash the chapter name on chapter change`}
            checked={defaults.chapterBanner}
            onChange={(e) => saveWith({ chapterBanner: e.currentTarget.checked })}
          />
          <Text size="xs" c="dimmed" mt={4}>
            <Trans>
              Credit pages and the next chapter's opening pages often look the same, so a chapter
              turn can pass unnoticed. This shows the chapter name over the page for a couple of
              seconds when you enter one.
            </Trans>
          </Text>
        </div>

        <div>
          <Switch
            label={t`Mark chapters read in Kavita too`}
            checked={settings?.pushToKavita ?? false}
            disabled={!ownsKavita}
            onChange={(e) => saveWith({}, e.currentTarget.checked)}
          />
          <Text size="xs" c="dimmed" mt={4}>
            <Trans>
              Off by default. When on, finishing a chapter in Maki's reader also marks it read for
              your Kavita user, so the two stay in step. Only applies to series Maki has matched to
              a Kavita series, reading stats are never counted twice either way.
            </Trans>
          </Text>
          {ownsKavita ? null : (
            <Text size="xs" c="dimmed" mt={4}>
              <Trans>
                Kavita is one server behind one API key, so its reading belongs to a single Maki
                account, and it isn't yours. An admin picks which one under Settings → Kavita.
              </Trans>
            </Text>
          )}
        </div>

        {ownsKavita ? <KavitaReadImportControl /> : null}
      </Stack>
    </Card>
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
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        OPDS
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        <Trans>
          Serves the library as an OPDS catalogue so reading apps (Panels, Chunky, KOReader,
          Mihon/Tachiyomi's OPDS extensions) connect straight to Maki, with no Kavita in between.
          Chapters can be downloaded whole or streamed a page at a time.
        </Trans>
      </Text>

      <Stack gap="md">
        <div>
          <Switch
            label={t`Enable the OPDS catalogue`}
            checked={enabled}
            onChange={(e) => saveWith({ enabled: e.currentTarget.checked })}
          />
          <Text size="xs" c="dimmed" mt={4}>
            <Trans>
              The feed URL carries its own token and is the only credential a reading app needs,
              so anyone holding it can read the whole library. It is deliberately not your API
              key: revoking it below breaks configured readers and nothing else.
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
                <Alert color="yellow" variant="light" mt="xs">
                  <Trans>
                    Copy this now, it is shown only once. Maki stores a fingerprint of the token,
                    not the token, so it cannot be displayed again. Lose it and you regenerate.
                  </Trans>
                </Alert>
                <Text size="xs" c="dimmed" mt={4}>
                  <Trans>
                    Paste it into your reading app as an OPDS catalogue. If you reach Maki from
                    outside your network, swap the host for the address you use there.
                  </Trans>
                </Text>
              </>
            ) : (
              <Group gap="xs" wrap="nowrap">
                <Code>{opds?.tokenPrefix ? `${opds.tokenPrefix}…` : 'none yet'}</Code>
                <Button
                  size="compact-xs"
                  variant="light"
                  color="red"
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
            <Text size="xs" c="dimmed" mt={4}>
              <Trans>
                Pages fetched by a streaming reader count as read, so OPDS reading shows up in
                your library, Rewind and your trackers. Turn it off if an app reports progress you
                didn't make: some fetch pages ahead, or grab the last page to size their page bar.
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
              The current feed URL stops working immediately. Every reading app you've set up
              with it will need the new URL.
            </Trans>
          </Text>
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setRotateModalOpen(false)}>
              <Trans>Cancel</Trans>
            </Button>
            <Button
              color="red"
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
    </Card>
  )
}

function KavitaImportResultSummary({
  result,
}: {
  result: { seriesMatched: number; chaptersMarked: number; seriesUnmatched: number }
}) {
  const { chaptersMarked, seriesMatched, seriesUnmatched } = result
  return (
    <Text size="xs" c="dimmed">
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
      <Text size="xs" c="dimmed" mb="sm">
        <Trans>
          Marks every chapter you've already finished in Kavita as read in Maki, so the built-in
          reader and the library's progress bars don't start from zero. Safe to run more than
          once: it never un-marks anything. These chapters are deliberately left out of Rewind:
          Kavita doesn't say when they were read, and dating them today would pile your whole back
          catalogue onto one day of the year in review. Rewind keeps counting only the reading
          Maki sees happen, through the scrobble sync and its own reader.
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
          <Text size="xs" c="dimmed">
            <Trans>Reading progress from Kavita…</Trans>
          </Text>
        )}
        {!status?.running && status?.error && (
          <Text size="xs" c="red">
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
  const [retryEnabled, setRetryEnabled] = useState(true)
  const [retryMaxAttempts, setRetryMaxAttempts] = useState<number | string>(5)
  const [smartDownloadChaptersLeft, setSmartDownloadChaptersLeft] = useState<number | string>(5)
  const [smartDownloadChapters, setSmartDownloadChapters] = useState<number | string>(10)
  const [itemTimeoutMinutes, setItemTimeoutMinutes] = useState<number | string>(120)
  const [useHardlinks, setUseHardlinks] = useState(true)

  useEffect(() => {
    if (settings) {
      setConcurrentChapters(settings.concurrentChapters)
      setRetryEnabled(settings.retryEnabled)
      setRetryMaxAttempts(settings.retryMaxAttempts)
      setSmartDownloadChaptersLeft(settings.smartDownloadChaptersLeft)
      setSmartDownloadChapters(settings.smartDownloadChapters)
      setItemTimeoutMinutes(settings.itemTimeoutMinutes)
      setUseHardlinks(settings.useHardlinks)
    }
  }, [settings])

  const dirty =
    settings !== undefined &&
    (Number(concurrentChapters) !== settings.concurrentChapters ||
      retryEnabled !== settings.retryEnabled ||
      Number(retryMaxAttempts) !== settings.retryMaxAttempts ||
      Number(smartDownloadChaptersLeft) !== settings.smartDownloadChaptersLeft ||
      Number(smartDownloadChapters) !== settings.smartDownloadChapters ||
      Number(itemTimeoutMinutes) !== settings.itemTimeoutMinutes ||
      useHardlinks !== settings.useHardlinks)

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        <Trans>Downloads</Trans>
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        <Trans>
          How many chapters download at once from scraper sources. Higher isn't always faster:
          each worker is a live connection to the same site, and tripping its rate limit pauses
          every download. Torrent releases aren't affected. Takes effect after a restart.
        </Trans>
      </Text>
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
      <Text size="sm" c="dimmed" mb="xs">
        <Trans>
          Automatically downloads the next chapters of a series when you have only a few unread
          chapters left. The settings below control how many unread chapters trigger the download
          and how many chapters are downloaded at once. Runs every five minutes, based on reading
          progress from Kavita or the built-in reader. Enabled per series as a monitoring option.
        </Trans>
      </Text>
      <Group align="flex-end" mb="md">
        <NumberInput
        label={t`Chapters unread before trigger`}
        min={1}
        max={10}
        clampBehavior="strict"
        value={smartDownloadChaptersLeft}
        onChange={setSmartDownloadChaptersLeft}
        w={220}
        mb="md"
      />
      <NumberInput
        label={t`Chapters to download at once`}
        min={1}
        max={20}
        clampBehavior="strict"
        value={smartDownloadChapters}
        onChange={setSmartDownloadChapters}
        w={220}
        mb="md"
      />
        </Group>
      <Text fw={500} size="sm" mb={4}>
        <Trans>Stuck downloads</Trans>
      </Text>
      <Text size="sm" c="dimmed" mb="xs">
        <Trans>
          A chapter that never finishes holds a worker for as long as the app runs, and with only
          a couple of workers that stops the whole queue: everything else sits on "Queued" with
          nothing wrong with it. Past this many minutes the download is abandoned and marked
          failed, so retry handling takes over. Set 0 to remove the limit. Takes effect after a
          restart.
        </Trans>
      </Text>
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
      <Text size="sm" c="dimmed" mb="xs">
        <Trans>
          A finished torrent keeps seeding from the download folder, so its files are brought into
          the library rather than moved. A hardlink gives the library its own name for the same
          bytes, so the release isn't stored twice. It only works when the download folder and the
          library sit on the same filesystem; when they don't, Maki copies instead. Hardlinked
          files are left exactly as the release built them, which means no ComicInfo.xml
          standardization for them, so Kavita may group them separately from chapters Maki
          downloaded itself.
        </Trans>
      </Text>
      <Switch
        label={t`Hardlink imported torrents when possible`}
        checked={useHardlinks}
        onChange={(e) => setUseHardlinks(e.currentTarget.checked)}
        mb="md"
      />
      <Text fw={500} size="sm" mb={4}>
        <Trans>Retry Handling</Trans>
      </Text>
      <Text size="sm" c="dimmed" mb="xs">
        <Trans>
          Failed downloads are automatically retried on an escalating backoff (5m, 10m, 20m, ...)
          up to the attempt cap below. A manual retry from the Activity page doesn't count against
          it.
        </Trans>
      </Text>
      <Group align="flex-end" mb="md">
        <Switch
          label={t`Automatically retry failed downloads`}
          checked={retryEnabled}
          onChange={(e) => setRetryEnabled(e.currentTarget.checked)}
        />
        <NumberInput
          label={t`Max attempts`}
          min={1}
          max={20}
          clampBehavior="strict"
          value={retryMaxAttempts}
          onChange={setRetryMaxAttempts}
          disabled={!retryEnabled}
          w={140}
        />
      </Group>
      <Button
        variant="default"
        disabled={!dirty}
        loading={save.isPending}
        onClick={() =>
          save.mutate(
            {
              concurrentChapters: Number(concurrentChapters),
              retryEnabled,
              retryMaxAttempts: Number(retryMaxAttempts),
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
      >
        <Trans>Save</Trans>
      </Button>
    </Card>
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
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        <Trans>Backup &amp; Restore</Trans>
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        <Trans>
          A backup is a zip of your database and <Code>config.json</Code>, your whole library and
          all settings. Big, re-downloadable data (the MangaBaka dump, embeddings, covers, cache)
          is left out. One is taken automatically right before any upgrade migration runs.
          Restoring replaces the current data and restarts Maki.
        </Trans>
      </Text>
      <Alert color="yellow" icon={<IconAlertTriangle size={16} />} mb="md" variant="light">
        <Trans>
          Backup files contain your settings secrets (API keys, passwords) in plain text. Treat a
          downloaded backup like a password. Restore auto-recovers only under a supervisor (Docker
          / systemd); a bare process just stops and you restart it yourself.
        </Trans>
      </Alert>

      <Stack>
        {backups && backups.length > 0 && (
          <Table>
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
                    <Badge size="sm" variant="light" color={b.manifest.kind === 'auto' ? 'gray' : 'blue'}>
                      {b.manifest.kind}
                    </Badge>
                  </Table.Td>
                  <Table.Td>
                    <Text size="xs" c="dimmed">
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
                        color="red"
                        onClick={() => remove.mutate(b.name)}
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
          <Button
            variant="default"
            disabled={!retentionDirty}
            loading={saveRetention.isPending}
            onClick={() =>
              saveRetention.mutate(
                { retention: Number(retention) },
                { onSuccess: () => notifications.show({ message: now`Saved`, color: 'green' }) },
              )
            }
          >
            <Trans>Save</Trans>
          </Button>
        </Group>
      </Stack>

      <Modal opened={target !== null} onClose={() => setTarget(null)} title={t`Restore backup`} centered>
        <Stack>
          <Text size="sm">
            <Trans>
              This replaces your current library and settings with <b>{backupName}</b>, then restarts
              Maki. The current data is not kept, take a backup first if you want a way back.
            </Trans>
          </Text>
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setTarget(null)}>
              <Trans>Cancel</Trans>
            </Button>
            <Button color="red" loading={restore.isPending || upload.isPending} onClick={confirmRestore}>
              <Trans>Restore &amp; restart</Trans>
            </Button>
          </Group>
        </Stack>
      </Modal>
    </Card>
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
        <Text size="sm" c="dimmed">
          <Trans>
            Restrict release searches to specific indexers and Torznab categories. With nothing
            selected, every indexer and category is searched.
          </Trans>
        </Text>
      )}
      {configured && indexersErrorMessage != null && (
        <Text size="sm" c="red">
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
              <Text size="sm" c="dimmed">
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
            <Button
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
            >
              <Trans>Save</Trans>
            </Button>
          </Group>
        </Stack>
      )}
    </Stack>
  )
}

function FlareSolverrSection() {
  const { data: settings } = useFlareSolverrSettings()
  const save = useSaveFlareSolverr()
  const test = useTestFlareSolverr()
  const [url, setUrl] = useState('')

  useEffect(() => {
    if (settings?.url) setUrl(settings.url)
  }, [settings?.url])

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        FlareSolverr
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        <Trans>
          Required for Cloudflare-protected sources like MangaFire. Point this at a running
          FlareSolverr instance (e.g. http://localhost:8191).
        </Trans>
      </Text>
      <Group>
        <TextInput
          placeholder="http://localhost:8191"
          value={url}
          onChange={(e) => setUrl(e.currentTarget.value)}
          style={{ flex: 1 }}
        />
        <Button
          variant="default"
          loading={test.isPending}
          onClick={() =>
            test.mutate(url || null, {
              onSuccess: () =>
                notifications.show({ message: now`FlareSolverr is reachable`, color: 'green' }),
            })
          }
        >
          <Trans>Test</Trans>
        </Button>
        <Button
          loading={save.isPending}
          onClick={() =>
            save.mutate(url || null, {
              onSuccess: () => notifications.show({ message: now`Saved`, color: 'green' }),
            })
          }
        >
          <Trans>Save</Trans>
        </Button>
      </Group>
    </Card>
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

  const conn = (service: string) => status?.connections.find((c) => c.service === service)

  const set = (patch: Partial<ScrobbleSettings>) =>
    setForm((f) => (f ? { ...f, ...patch } : f))

  const origin = window.location.origin
  // The app registrations, interval and library filter belong to the instance. The server returns
  // them as null to anyone else and drops them on save, so a non-admin never sees the inputs.
  const isAdmin = data?.isAdmin ?? false

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="xs">
        <Trans>Scrobbling</Trans>
      </Title>
      <Text size="sm" c="dimmed" mb="sm">
        <Trans>
          Pushes your Kavita reading progress to AniList, MyAnimeList and MangaBaka (any
          combination, leave a site's credentials empty to disable it). Manage connections and
          review matches on the Scrobble page. Uses the Kavita connection configured above.
        </Trans>
      </Text>
      <Stack gap="xs">
        <Text size="sm" fw={600}>
          AniList
        </Text>
        {isAdmin && (
          <>
          <Text size="xs" c="dimmed">
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
          <Text size="xs" c="dimmed">
            <Trans>
              Create an API client at myanimelist.net/apiconfig (App Type: web) with redirect URL{' '}
              <Code>{origin}/api/v1/scrobble/oauth/mal</Code>. Paste the <b>Client ID</b> (not the
              secret) exactly as shown there. If connecting opens a browser “sign in to
              myanimelist.net” popup and then <Code>invalid_client</Code>, MyAnimeList didn&apos;t
              recognise the Client ID: re-copy it and make sure the App Type is set.
            </Trans>
          </Text>
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
          description={t`From MangaBaka settings, no OAuth needed, works immediately`}
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
          <Group grow mt="xs">
            <TextInput
              label={t`Sync interval (minutes)`}
              value={form?.intervalMinutes?.toString() ?? '30'}
              onChange={(e) => {
                const parsed = parseInt(e.currentTarget.value, 10)
                set({ intervalMinutes: Number.isNaN(parsed) ? 30 : parsed })
              }}
            />
            <TextInput
              label={t`Kavita library ids`}
              description={t`Comma-separated; empty = scrobble all libraries`}
              value={form?.libraryIds ?? ''}
              onChange={(e) => set({ libraryIds: e.currentTarget.value })}
            />
          </Group>
        )}
        <Switch
          label={t`Add unread series as plan-to-read`}
          description={t`Series in Kavita with no reading progress are added to the sites as 'plan to read'. Never modifies entries already on your lists.`}
          checked={form?.planToRead ?? false}
          onChange={(e) => {
            const checked = e.currentTarget.checked
            set({ planToRead: checked })
          }}
        />
        <Group justify="flex-end">
          <Button
            loading={save.isPending}
            disabled={!form}
            onClick={() =>
              form &&
              save.mutate(form, {
                onSuccess: () => notifications.show({ message: now`Saved`, color: 'green' }),
              })
            }
          >
            <Trans>Save</Trans>
          </Button>
        </Group>
      </Stack>
    </Card>
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
 * the user across devices.
 */
function StartPageSection() {
  const { t } = useLingui()
  const { data: ui } = useUiSettings()
  const patch = useUiPatch()
  const { data: metadata } = useMetadataSettings()
  const discoverAvailable = Boolean(metadata?.useLocalDb && metadata?.dumpPresent)
  const homeEnabled = ui?.homeLayout.enabled ?? true

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb={4}>
        <Trans>Start page</Trans>
      </Title>
      <Text size="sm" c="dimmed" mb="sm">
        <Trans>Which page Maki opens on. Stored on the server, so it applies on every device.</Trans>
      </Text>
      <Select
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
      />
    </Card>
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
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb={4}>
        <Trans>Language</Trans>
      </Title>
      <Text size="sm" c="dimmed" mb="sm">
        <Trans>
          Which language Maki's interface is in. Stored on the server, so it applies on every
          device. This is separate from Title language below, which is about the metadata rather
          than the app.
        </Trans>
      </Text>
      <Select
        data={options}
        value={ui?.language ?? ''}
        onChange={(value) => value !== null && apply?.(value)}
        disabled={!apply}
        allowDeselect={false}
        maw={260}
      />
      <Text size="xs" c="dimmed" mt="sm">
        <Trans>
          Showing {currentLocaleLabel}. Translations other than English are machine-made and being
          corrected over time; anything still untranslated falls back to English.
        </Trans>
      </Text>
    </Card>
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
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb={4}>
        <Trans>Title language</Trans>
      </Title>
      <Text size="sm" c="dimmed" mb="sm">
        <Trans>
          Which language series titles are shown in, where the metadata provider has one. Display
          only: folders and file names keep the English title, and so does sorting.
        </Trans>
      </Text>
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
    </Card>
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
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb={4}>
        <Trans>Series page</Trans>
      </Title>
      <Text size="sm" c="dimmed" mb="sm">
        <Trans>
          Which rails appear below the chapter list. Turning one off also stops it being fetched.
        </Trans>
      </Text>
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
    </Card>
  )
}

/**
 * Which Home sections appear, in what order, and whether Home exists at all.
 *
 * Reorder is drag-and-drop, same mechanism as SourcePrioritySection: the real order only
 * changes on drop, rows shift purely visually (transform) while dragging. Up/down buttons
 * stay alongside as the keyboard-reachable equivalent.
 */
function HomeSectionsSection() {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const { data: ui } = useUiSettings()
  const patch = useUiPatch()
  const sections = ui?.homeLayout.sections ?? []
  const homeEnabled = ui?.homeLayout.enabled ?? true

  const [dragFromIndex, setDragFromIndex] = useState<number | null>(null)
  const [hoverIndex, setHoverIndex] = useState<number | null>(null)
  const [rowHeight, setRowHeight] = useState(0)
  const containerRef = useRef<HTMLDivElement>(null)

  const write = (next: HomeSection[]) =>
    patch?.({ homeLayout: { enabled: homeEnabled, sections: next } })

  const move = (index: number, delta: number) => {
    const target = index + delta
    if (target < 0 || target >= sections.length) return
    const next = [...sections]
    ;[next[index], next[target]] = [next[target], next[index]]
    write(next)
  }

  const toggle = (index: number, enabled: boolean) =>
    write(sections.map((s, i) => (i === index ? { ...s, enabled } : s)))

  function handleContainerDragOver(e: DragEvent) {
    e.preventDefault()
    if (dragFromIndex === null || !containerRef.current || rowHeight === 0) return
    const rect = containerRef.current.getBoundingClientRect()
    const rawIndex = Math.floor((e.clientY - rect.top) / rowHeight)
    const clamped = Math.min(Math.max(rawIndex, 0), sections.length - 1)
    setHoverIndex(clamped)
  }

  function commitDrag() {
    if (dragFromIndex !== null && hoverIndex !== null && dragFromIndex !== hoverIndex) {
      const next = [...sections]
      const [moved] = next.splice(dragFromIndex, 1)
      next.splice(hoverIndex, 0, moved)
      write(next)
    }
    setDragFromIndex(null)
    setHoverIndex(null)
  }

  return (
    <Card withBorder radius="md" padding="md">
      <Group justify="space-between" align="flex-start" wrap="nowrap" mb="sm">
        <div>
          <Title order={4} mb={4}>
            <Trans>Home screen</Trans>
          </Title>
          <Text size="sm" c="dimmed">
            <Trans>
              Pick which sections appear and what order they run in. Turn Home off entirely if you
              don&apos;t read in Maki: the tab disappears and the library takes over as the start
              page.
            </Trans>
          </Text>
        </div>
        <Switch
          checked={homeEnabled}
          disabled={!patch}
          onChange={(e) =>
            patch?.({ homeLayout: { enabled: e.currentTarget.checked, sections } })
          }
          aria-label={t`Enable the Home screen`}
        />
      </Group>

      {homeEnabled && (
        <Stack gap={6} ref={containerRef} onDragOver={handleContainerDragOver}>
          {sections.map((section, index) => {
            let shift = 0
            if (dragFromIndex !== null && hoverIndex !== null && index !== dragFromIndex) {
              if (dragFromIndex < hoverIndex && index > dragFromIndex && index <= hoverIndex)
                shift = -1
              else if (dragFromIndex > hoverIndex && index >= hoverIndex && index < dragFromIndex)
                shift = 1
            }
            const label = renderLabel(HOME_SECTION_LABELS[section.key])
            return (
              <Group
                key={section.key}
                gap="xs"
                wrap="nowrap"
                px="xs"
                py={6}
                draggable={!!patch}
                onDragStart={(e) => {
                  const original = e.currentTarget
                  const clone = original.cloneNode(true) as HTMLElement
                  clone.style.position = 'fixed'
                  clone.style.top = '-9999px'
                  clone.style.left = '-9999px'
                  clone.style.width = `${original.offsetWidth}px`
                  clone.style.pointerEvents = 'none'
                  document.body.appendChild(clone)
                  e.dataTransfer.setDragImage(clone, e.nativeEvent.offsetX, e.nativeEvent.offsetY)
                  setTimeout(() => document.body.removeChild(clone), 0)
                  setDragFromIndex(index)
                  setHoverIndex(index)
                  setRowHeight(original.getBoundingClientRect().height)
                }}
                onDragEnd={commitDrag}
                style={{
                  border: '1px solid var(--border)',
                  borderRadius: 'var(--mantine-radius-md)',
                  opacity: dragFromIndex === index ? 0 : section.enabled ? 1 : 0.55,
                  cursor: patch ? 'grab' : undefined,
                  transform: shift ? `translateY(${shift * rowHeight}px)` : undefined,
                  transition: 'transform 150ms ease',
                  pointerEvents: dragFromIndex !== null && index !== dragFromIndex ? 'none' : undefined,
                }}
              >
                <IconGripVertical size={14} opacity={0.5} />
                <ActionIcon
                  variant="subtle"
                  color="gray"
                  size="sm"
                  disabled={index === 0 || !patch}
                  aria-label={t`Move ${label} up`}
                  onClick={() => move(index, -1)}
                >
                  <IconChevronUp size={15} />
                </ActionIcon>
                <ActionIcon
                  variant="subtle"
                  color="gray"
                  size="sm"
                  disabled={index === sections.length - 1 || !patch}
                  aria-label={t`Move ${label} down`}
                  onClick={() => move(index, 1)}
                >
                  <IconChevronDown size={15} />
                </ActionIcon>
                <Text size="sm" fw={550} style={{ flex: 1 }}>
                  {label}
                </Text>
                <Switch
                  size="sm"
                  checked={section.enabled}
                  disabled={!patch}
                  onChange={(e) => toggle(index, e.currentTarget.checked)}
                  aria-label={t`Show ${label}`}
                  onMouseDown={(e) => e.stopPropagation()}
                  draggable={false}
                />
              </Group>
            )
          })}
        </Stack>
      )}
    </Card>
  )
}

function AppearanceSection() {
  const renderLabel = useLabel()
  const { themeId, setThemeId, presets } = useThemeChoice()

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb={4}>
        <Trans>Appearance</Trans>
      </Title>
      <Text size="sm" c="dimmed" mb="sm">
        <Trans>
          Pick an accent colour, or switch to the light theme. Applies instantly and is remembered
          on this device.
        </Trans>
      </Text>
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
    </Card>
  )
}

function GeneralSection() {
  const { data: general } = useGeneralSettings()
  const completeSetup = useCompleteSetup()

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        <Trans>General</Trans>
      </Title>
      <Stack gap="xs">
        <Group>
          <Text size="sm" w={80}>
            <Trans>Port</Trans>
          </Text>
          <Code>{general?.port ?? '...'}</Code>
        </Group>
        {/* The instance API key used to live here, with a regenerate button. There is no instance
            key any more: credentials belong to accounts and are created under My account, where
            each one can be revoked without affecting anything else. */}
        <Group justify="space-between" mt="xs">
          <Text size="sm" c="dimmed">
            <Trans>Re-open the first-time setup guide.</Trans>
          </Text>
          <Button
            variant="default"
            size="xs"
            loading={completeSetup.isPending}
            onClick={() => completeSetup.mutate(false)}
          >
            <Trans>Run setup guide</Trans>
          </Button>
        </Group>
      </Stack>
    </Card>
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

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        <Trans>Updates</Trans>
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        {status?.isDocker ? (
          <Trans>
            Checks GitHub daily for a newer release and raises a banner and a Notifications event
            when one is found. Docker installs are notify-only, pull the new image and recreate
            the container.
          </Trans>
        ) : (
          <Trans>
            Checks GitHub daily for a newer release and raises a banner and a Notifications event
            when one is found. Bare installs are notify-only, pull the latest code and rebuild.
          </Trans>
        )}
      </Text>
      <Stack gap="sm">
        <Switch
          label={t`Check for updates`}
          checked={settings?.checkForUpdates ?? true}
          onChange={(e) => save.mutate(e.currentTarget.checked)}
        />
        <Group justify="space-between">
          <Text size="sm" c="dimmed">
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
    </Card>
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
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        <Trans>Image cache</Trans>
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        <Trans>
          Clears the reader&apos;s page thumbnails and the source-comparison samples, drops poster
          folders for series that no longer exist, and re-downloads series posters from the
          metadata provider. Thumbnails come back on their own the next time a chapter is opened,
          so nothing is lost by clearing them.
        </Trans>
      </Text>

      {usage && (
        <Stack gap={4} mb="md">
          <Text size="sm" c="dimmed">
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
          <Text size="sm" c="dimmed">
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
          color={status?.lastError ? 'red' : 'brand'}
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
    </Card>
  )
}

/**
 * Which Maki account Kavita's reading belongs to. Instance-wide on purpose: Kavita is one server
 * reached with one API key, so everything it reports is a single person's reading and there is no way
 * to tell two Kavita users apart from here. Naming the owner is what keeps the adopt/merge/zero-delta
 * chain intact: the recurring pass, the read-status import, the per-chapter sync and the push-back
 * all act as the same user, so a chapter read in Maki and re-reported by Kavita counts once.
 */
function KavitaUserSection() {
  const { t } = useLingui()
  const { data: bound } = useKavitaUser()
  const { data: users } = useUsers()
  const save = useSetKavitaUser()

  const options = (users ?? [])
    .filter((u) => !u.disabled && !u.pendingSetup)
    .map((u) => ({ value: String(u.id), label: u.displayName || u.userName }))

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        <Trans>Kavita reading</Trans>
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        <Trans>
          Whose reading history Kavita's progress is recorded as. Unset means the lowest-numbered
          admin, which is what a single-user instance wants. Only this account can import read
          status from Kavita or push its reads back.
        </Trans>
      </Text>
      <Select
        label={t`Attribute Kavita's reading to`}
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
    </Card>
  )
}

/**
 * Every card, keyed by its registry id. The registry decides order, tab and who may see it; this
 * only says how each id is built, so adding a setting is one entry there plus one line here.
 *
 * A hook rather than a module-scope table: the Prowlarr/qBittorrent/Kavita cards below carry
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
      'start-page': <StartPageSection />,
      'title-language': <TitleLanguageSection />,
      'home-screen': <HomeSectionsSection />,
      'series-page': <SeriesPageSection />,

      reader: <ReaderSection />,
      'reading-profiles': <ReadingProfilesSection />,
      progress: <ProgressSection />,
      opds: <OpdsSection />,
      'discover-rating': <DiscoverSection />,

      'root-folders': <RootFoldersSection />,
      'library-files': <LibrarySection />,
      monitoring: <MonitoringSection />,
      metadata: <MetadataSection />,
      recommendations: <RecommendationIndexSection />,

      downloads: <DownloadSection />,
      sources: (
        <Stack gap="md">
          <SourceLanguageSection />
          <SourcePrioritySection />
        </Stack>
      ),
      flaresolverr: <FlareSolverrSection />,
      prowlarr: (
        <ConnectionSettingsCard
          name="prowlarr"
          title="Prowlarr"
          description={t`Search manga releases on your indexers. Uses Prowlarr's aggregated search API, no app sync needed.`}
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
          description={t`Download client for grabbed releases. Completed torrents are imported into the library automatically (category defaults to 'maki'). If qBittorrent reports download paths Maki can't reach (e.g. it runs in Docker and reports /downloads while Maki sees Z:\\downloads), fill the optional path mapping to translate them.`}
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

      'kavita-user': <KavitaUserSection />,
      kavita: (
        <ConnectionSettingsCard
          name="kavita"
          title="Kavita"
          description={t`When configured, Maki asks Kavita to scan the series folder right after new chapters download or imported files change, then pushes the series poster, web links and publication status into Kavita (covers you've set yourself in Kavita are never overwritten). Get the API key from Kavita under User Settings → 3rd Party Clients. If Kavita sees the library under a different path (e.g. it runs in Docker), fill the optional path mapping so Maki translates folder paths.`}
          fields={[
            { key: 'url', label: t`URL`, placeholder: 'http://localhost:5000' },
            { key: 'apiKey', label: t`API key`, secret: true },
            { key: 'pathMapFrom', label: t`Path mapping - Maki side`, placeholder: t`C:\\Manga (optional)` },
            { key: 'pathMapTo', label: t`Path mapping - Kavita side`, placeholder: t`/manga (optional)` },
          ]}
        />
      ),
      scrobbling: <ScrobbleSection />,
      notifications: <NotificationsSection />,

      users: <UsersSection />,
      security: <SecuritySection />,
      oidc: <OidcSection />,

      backup: <BackupSection />,
      'image-cache': <ImageCacheSection />,
      updates: <UpdatesSection />,
      general: <GeneralSection />,
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
    <>
      <PageHeader
        title={t`Settings`}
        description={
          isAdmin
            ? t`Storage, metadata, download clients and integrations for your Maki instance.`
            : t`Your account and how Maki looks.`
        }
      />
      <Tabs
        value={activeTab}
        onChange={(value) => value && setSearchParams({ tab: value })}
        keepMounted={false}
      >
        <Tabs.List mb="md">
          {tabs.map((tab) => (
            <Tabs.Tab key={tab.key} value={tab.key}>
              {renderLabel(tab.label)}
            </Tabs.Tab>
          ))}
        </Tabs.List>

        {tabs.map((tab) => (
          <Tabs.Panel key={tab.key} value={tab.key}>
            <Stack maw={820}>
              <Text size="sm" c="dimmed">
                {renderLabel(tab.description)}
              </Text>
              {visible
                .filter((entry) => entry.tab === tab.key)
                .map((entry) => (
                  <div key={entry.id} id={`setting-${entry.id}`} style={{ scrollMarginTop: 80 }}>
                    {sectionNodes[entry.id]}
                  </div>
                ))}
            </Stack>
          </Tabs.Panel>
        ))}
      </Tabs>
    </>
  )
}
