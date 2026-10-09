import { useState, type FormEvent } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { useLabel } from '../../i18n-context'
import { useDebouncedValue } from '@mantine/hooks'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { plural, t as now } from '@lingui/core/macro'
import {
  ActionIcon,
  Button,
  Group,
  Modal,
  Radio,
  Select,
  Stack,
  Switch,
  Table,
  Text,
  TextInput,
} from '@mantine/core'
import { IconTrash } from '@tabler/icons-react'
import { notifications } from '@mantine/notifications'
import { SettingsSection } from './SettingsSection'
import { ConfirmDialog } from '../../components/ui/ConfirmDialog'
import { RecommendationModelSwitch } from '../../components/RecommendationModelSwitch'
import { NamingFormatInput } from '../../components/NamingFormatInput'
import { CONTENT_RATINGS } from '../../components/ContentRatingCards'
import { useIncognitoOptions, type IncognitoMode } from '../../components/ui/incognito'
import {
  useAddRootFolder,
  useDeleteRootFolder,
  useLibrarySettings,
  useNamingPreview,
  useSaveLibrarySettings,
  useDumpProgress,
  useMetadataSettings,
  useMonitoringSettings,
  useRecommendationIndex,
  useRefreshMetadataDump,
  useRootFolders,
  useSaveMetadataSettings,
  useSaveMonitoringSettings,
  useSetEmbeddingModel,
  useRenameManySeries,
  useSeries,
  CONTENT_RATING_LABELS,
  type FolderNamingMode,
  type LibrarySettings,
} from '../../api/hooks'
import { SettingsHelp } from '../../components/settings/SettingsHelp'
import { MONITOR_OPTIONS } from '../../components/series/SeriesActionsMenu'
import { DumpProgressBar } from '../../components/MetadataDumpProgress'
import { formatBytes, formatDateTime, formatNumber } from '../../format'

export function RootFoldersSection() {
  const { t } = useLingui()
  const [newPath, setNewPath] = useState('')
  const { data: rootFolders } = useRootFolders()
  const addFolder = useAddRootFolder()
  const deleteFolder = useDeleteRootFolder()
  const [deleting, setDeleting] = useState<{ id: number; path: string } | null>(null)

  const add = () => {
    if (!newPath.trim()) return
    addFolder.mutate(newPath.trim(), {
      onSuccess: () => setNewPath(''),
    })
  }

  return (
    <SettingsSection
      id="root-folders"
      title={<Trans>Root folders</Trans>}
      description={
        <Trans>
          Folders where series are stored. Point Kavita at the same folders. Which users can see
          each folder is set per user under Users &amp; security.
        </Trans>
      }
    >
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
                      onClick={() => setDeleting({ id: f.id, path: f.path })}
                      aria-label={t`Remove root folder`}
                    >
                      <IconTrash size={16} />
                    </ActionIcon>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}
        <Group
          component="form"
          align="flex-end"
          onSubmit={(e: FormEvent) => {
            e.preventDefault()
            add()
          }}
        >
          <TextInput
            label={t`Path`}
            placeholder={t`C:\\Manga or /library`}
            value={newPath}
            onChange={(e) => setNewPath(e.currentTarget.value)}
            style={{ flex: 1 }}
          />
          <Button type="submit" loading={addFolder.isPending}>
            <Trans>Add</Trans>
          </Button>
        </Group>
      </Stack>

      <ConfirmDialog
        opened={deleting !== null}
        onClose={() => setDeleting(null)}
        title={<Trans>Remove this root folder?</Trans>}
        confirmLabel={<Trans>Remove folder</Trans>}
        loading={deleteFolder.isPending}
        onConfirm={() =>
          deleting &&
          deleteFolder.mutate(deleting.id, {
            onSuccess: () => setDeleting(null),
          })
        }
      >
        <Stack gap="xs">
          <Text size="sm" ff="monospace">{deleting?.path}</Text>
          <Text size="sm">
            <Trans>
              Files on disk are not touched. Every user's access to this folder is removed, and
              adding it back later starts with no grants.
            </Trans>
          </Text>
        </Stack>
      </ConfirmDialog>
    </SettingsSection>
  )
}

export function MetadataSection() {
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
    <SettingsSection
      id="metadata"
      title={<Trans>Metadata database</Trans>}
      description={
        <Trans>
          Series metadata comes from MangaBaka. A local copy, about 3.5 GB and refreshed from the
          nightly dump, lets search and imports skip the API's rate limit, and Discover needs it.
          The API is used until the first download finishes.
        </Trans>
      }
    >
      <Stack gap="sm">
        <Switch
          label={t`Use the local MangaBaka database`}
          description={t`Off answers from the API instead and turns Discover off. The copy on disk is kept and still refreshed.`}
          checked={settings?.useLocalDb ?? true}
          onChange={(e) =>
            save.mutate(e.currentTarget.checked)
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
                    color: result.alreadyRunning ? 'var(--neutral)' : 'var(--ok)',
                  }),
              })
            }
          >
            <Trans>Refresh now</Trans>
          </Button>
        </Group>
      </Stack>
    </SettingsSection>
  )
}

export function RecommendationIndexSection() {
  const { t } = useLingui()
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
          color: r.switching ? 'var(--info)' : 'var(--neutral)',
        }),
    })

  return (
    <SettingsSection
      id="recommendations"
      title={<Trans>Semantic search &amp; recommendations</Trans>}
      description={
        <Trans>
          A local embedding model lets Discover recommend by feel and search by description. The
          vectors download prebuilt, so this normally needs no attention. While it's off or still
          downloading, search falls back to titles and recommendations to genres.
        </Trans>
      }
    >
      <RecommendationModelSwitch
        status={status}
        busy={setModel.isPending}
        onSelect={selectModel}
        description={t`Uses about 240 MB of RAM. Needs the local MangaBaka database above.`}
      />
    </SettingsSection>
  )
}

/**
 * The library settings are one record with one PUT, and the three required fields have to travel
 * with every write. Same idea as useUiPatch: patch what changed, carry the rest over.
 */
function useLibraryPatch() {
  const { data: settings } = useLibrarySettings()
  const save = useSaveLibrarySettings()
  const queryClient = useQueryClient()
  const patch = (changes: Partial<LibrarySettings>) => {
    // Merge over the freshest cache, not `settings`: that's a render snapshot, and two patches
    // fired before the first refetch lands would otherwise have the second undo the first.
    const current = queryClient.getQueryData<LibrarySettings>(['settings', 'library']) ?? settings
    save.mutate(
      {
        writeComicInfo: current?.writeComicInfo ?? true,
        folderNamingMode: current?.folderNamingMode ?? 'rename',
        writeCoverToFolder: current?.writeCoverToFolder ?? false,
        ...changes,
      },
      { onSuccess: () => notifications.show({ message: now`Saved`, color: 'var(--ok)' }) },
    )
  }
  return { settings, patch }
}

/** What a series starts with when it is added or imported: the specials rule and the incognito rules. */
export function NewSeriesDefaultsSection() {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const incognitoOptions = useIncognitoOptions()
  const { data: monitoring } = useMonitoringSettings()
  const saveMonitoring = useSaveMonitoringSettings()
  const { settings, patch } = useLibraryPatch()

  return (
    <SettingsSection
      id="monitoring"
      title={<Trans>New series defaults</Trans>}
      description={
        <Trans>
          What a series starts with when it is added or imported. Series already in the library
          keep their own settings.
        </Trans>
      }
    >
      <Select
        mb="md"
        styles={{ wrapper: { maxWidth: 320 } }}
        label={t`Monitoring`}
        description={t`What a series added with monitoring on, or imported from disk, starts with. Smart downloads the next few chapters ahead of your reading.`}
        data={MONITOR_OPTIONS.filter((o) => o.value !== 'None').map((o) => ({
          value: o.value,
          label: renderLabel(o.label),
        }))}
        value={monitoring?.defaultMode ?? 'All'}
        onChange={(value) => value && saveMonitoring.mutate({ defaultMode: value })}
        allowDeselect={false}
      />

      <Switch
        mb="lg"
        label={t`Skip specials`}
        description={t`New series start without decimal chapters (10.5, x.1): they stay listed but are never downloaded or counted. Also applies to Smart-monitored series when a special appears later.`}
        checked={monitoring?.unmonitorSpecials ?? false}
        onChange={(e) => saveMonitoring.mutate({ unmonitorSpecials: e.currentTarget.checked })}
      />

      <Text fw={500} size="sm" mb={4}>
        <Trans>Incognito by content rating</Trans>
      </Text>
      <SettingsHelp mb="sm">
        <Trans>
          Pre-filled on the add form, where any single add can change it. "No scrobble" keeps a
          series off your trackers; "Full" also keeps it out of stats and history. Series imported
          from disk start with incognito off.
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
    </SettingsSection>
  )
}

/** What Maki writes into and next to the files: ComicInfo.xml and the folder poster. */
export function LibraryFilesSection() {
  const { t } = useLingui()
  const { settings, patch } = useLibraryPatch()

  return (
    <SettingsSection
      id="library-files"
      title={<Trans>ComicInfo &amp; covers</Trans>}
      description={
        <Trans>
          Extra files Maki writes next to the chapters so Kavita and Komga name and group them the
          way Maki does.
        </Trans>
      }
    >
      <Switch
        mb="lg"
        label={t`Write ComicInfo.xml into imported files`}
        description={t`Standardises the ComicInfo.xml in CBZ files that arrive by torrent or import. Maki's own downloads always get one, hardlinked torrents are left as released, and PDFs never get one. A series page's "Update ComicInfo" action does one series later.`}
        checked={settings?.writeComicInfo ?? true}
        onChange={(e) => patch({ writeComicInfo: e.currentTarget.checked })}
      />
      <Switch
        label={t`Save a cover.jpg into each series' library folder`}
        description={t`For readers like Komga and Kavita that pick up a poster from the folder. Runs right away when switched on.`}
        checked={settings?.writeCoverToFolder ?? false}
        onChange={(e) => patch({ writeCoverToFolder: e.currentTarget.checked })}
      />
    </SettingsSection>
  )
}

export function NamingSection() {
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
  const folderError = preview.data?.seriesFolderErrors.join('; ') || undefined
  const chapterError = preview.data?.chapterErrors.join('; ') || undefined
  const stale = debouncedFolder !== folderFormat || debouncedChapter !== chapterFormat

  const saveFormats = () => {
    // A stale preview doesn't block the save: the server validates too, and a commit that lands
    // inside the debounce window (closing the token picker right after inserting one) would
    // otherwise be dropped silently.
    if (!settings || (!stale && (folderError || chapterError))) {
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
    <SettingsSection
      id="naming"
      title={<Trans>Folder &amp; file naming</Trans>}
      description={
        <Trans>
          How Maki names series folders and the chapter files it downloads. The "?" button lists
          every token. Changes apply to new series and downloads; files already on disk stay put
          until you rename them from a series' page or with the button below.
        </Trans>
      }
    >
      <Stack gap="md" mb="md">
        <NamingFormatInput
          label={t`Series folder format`}
          description={t`Used when adding a series, importing one, or renaming its folder`}
          value={folderFormat}
          example={preview.data?.seriesFolder}
          error={folderError}
          onChange={setFolderDraft}
          onCommit={saveFormats}
        />
        <NamingFormatInput
          label={t`Chapter file format`}
          description={t`Used for chapters Maki downloads, and for torrent imports unless you keep their file names below.`}
          value={chapterFormat}
          example={preview.data?.chapterFile}
          error={chapterError}
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
            Applies the series folder format and chapter file format above to all{' '}
            <Plural value={seriesCount} one="# series" other="# series" /> in the library, renaming
            folders and files on disk to match. Series already matching the format are left alone.
            This can take a while for a large library.
          </Trans>
        </Text>
        <Group justify="flex-end">
          <Button variant="default" onClick={() => setConfirmRenameAll(false)}>
            <Trans>Cancel</Trans>
          </Button>
          <Button
            color="var(--danger-fill)"
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
                        ? plural(renamed, {
                            one: `Renamed # series, ${formatNumber(failed)} failed`,
                            other: `Renamed # series, ${formatNumber(failed)} failed`,
                          })
                        : plural(renamed, { one: 'Renamed # series', other: 'Renamed # series' }),
                    color: failed > 0 ? 'var(--warn)' : 'var(--ok)',
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
          When importing an existing series from disk: rename its folder to the series folder
          format, or keep it as found.
        </Trans>
      </SettingsHelp>
      <Radio.Group
        aria-label={t`Folder naming on import`}
        value={settings?.folderNamingMode ?? 'rename'}
        onChange={(value) => patch({ folderNamingMode: value as FolderNamingMode })}
      >
        <Stack gap="xs" mt="xs">
          <Radio value="rename" label={t`Rename the folder to the series folder format`} />
          <Radio
            value="keep-new-standard"
            label={t`Keep the folder name, but put new downloads in a folder named by the format`}
          />
          <Radio value="keep-original" label={t`Keep the folder name, and put new downloads there too`} />
        </Stack>
      </Radio.Group>

      <Text fw={500} size="sm" mt="lg" mb={4}>
        <Trans>File naming on import</Trans>
      </Text>
      <SettingsHelp mb="sm">
        <Trans>
          Applies to torrent grabs and reviewed imports. Off keeps the release's own file name,
          which often says more than the format can. Series imported from disk keep their file
          names either way.
        </Trans>
      </SettingsHelp>
      <Switch
        label={t`Rename imported files to the chapter file format`}
        checked={settings?.renameImportedFiles ?? true}
        onChange={(e) => patch({ renameImportedFiles: e.currentTarget.checked })}
      />
    </SettingsSection>
  )
}

