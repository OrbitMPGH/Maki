import { useMemo, useState, type FormEvent } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { useLabel } from '../../i18n-context'
import { useDebouncedValue } from '@mantine/hooks'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { plural, t as now } from '@lingui/core/macro'
import {
  ActionIcon,
  Button,
  Checkbox,
  Group,
  Modal,
  Radio,
  Select,
  Stack,
  Switch,
  Table,
  Text,
  TextInput,
  VisuallyHidden,
} from '@mantine/core'
import { IconTrash } from '../../lib/icons'
import { notifications } from '../../lib/toast'
import { SettingsSection } from './SettingsSection'
import { activeSeries } from '../../lib/series'
import { ConfirmDialog } from '../../components/ui/ConfirmDialog'
import { MonoValue } from '../../components/ui/MonoValue'
import { RecommendationModelSwitch } from '../../components/RecommendationModelSwitch'
import { NamingFormatInput } from '../../components/NamingFormatInput'
import { CONTENT_RATINGS } from '../../components/ContentRatingCards'
import { useIncognitoOptions, type IncognitoMode } from '../../components/ui/incognito'
import {
  useAddRootFolder,
  useDeleteRootFolder,
  useLibrarySettings,
  useNameParsingSettings,
  useNamingPreview,
  useSaveLibrarySettings,
  useSaveNameParsingSettings,
  useDumpProgress,
  useMetadataSettings,
  useMonitoringSettings,
  useRecommendationIndex,
  useRefreshMetadataDump,
  useRootFolders,
  useSaveMonitoringSettings,
  useSetEmbeddingModel,
  useRenameManySeries,
  useSeries,
  CONTENT_RATING_LABELS,
  type FolderNamingMode,
  type LibrarySettings,
} from '../../api/hooks'
import { SettingsHelp } from '../../components/settings/SettingsHelp'
import { FolderBrowserModal } from '../../components/settings/FolderBrowserModal'
import { SUPPORTED_LOCALES } from '../../i18n'
import { MONITOR_OPTIONS } from '../../components/series/SeriesActionsMenu'
import { DumpProgressBar } from '../../components/MetadataDumpProgress'
import { formatBytes, formatDateTime, formatNumber } from '../../format'
import { useAuth } from '../../auth/AuthProvider'
import { SettingsNumberInput } from '../../components/settings/SettingsNumberInput'
import { useRecycleBin, useSetRecycleBinRetention } from '../../api/recycleBin'

export function RootFoldersSection() {
  const { t } = useLingui()
  const [newPath, setNewPath] = useState('')
  const [browsing, setBrowsing] = useState(false)
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
    >
      <Stack>
        {rootFolders && rootFolders.length > 0 && (
          <Table.ScrollContainer minWidth={420}>
            <Table className="panel-table ops-table">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th><Trans>Path</Trans></Table.Th>
                  <Table.Th><Trans>Free space</Trans></Table.Th>
                  <Table.Th><VisuallyHidden><Trans>Actions</Trans></VisuallyHidden></Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {rootFolders.map((f) => (
                  <Table.Tr key={f.id}>
                    <Table.Td>
                      <MonoValue value={f.path} copy minWidth="10rem" />
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
          </Table.ScrollContainer>
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
            style={{ flex: '1 1 16rem' }}
          />
          <Button variant="default" onClick={() => setBrowsing(true)}>
            <Trans>Browse</Trans>
          </Button>
          <Button type="submit" loading={addFolder.isPending}>
            <Trans>Add</Trans>
          </Button>
        </Group>
      </Stack>

      <FolderBrowserModal
        opened={browsing}
        onClose={() => setBrowsing(false)}
        onSelect={setNewPath}
        initialPath={newPath}
      />

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
  const { data: settings } = useMetadataSettings()
  const { data: progress } = useDumpProgress()
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
    >
      <Stack gap="sm">
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
    >
      <RecommendationModelSwitch
        status={status}
        busy={setModel.isPending}
        onSelect={selectModel}
        description={t`Uses about 150 MB of RAM. Needs the local MangaBaka database above.`}
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
    >
      <Select
        mb="md"
        styles={{ wrapper: { maxWidth: 320 } }}
        label={t`Monitoring`}
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
        description={t`Decimal chapters like 10.5 stay listed but are never downloaded.`}
        checked={monitoring?.unmonitorSpecials ?? false}
        onChange={(e) => saveMonitoring.mutate({ unmonitorSpecials: e.currentTarget.checked })}
      />

      <Text fw={500} size="sm" mb={4}>
        <Trans>Incognito by content rating</Trans>
      </Text>
      <SettingsHelp mb="sm">
        <Trans>"No scrobble" keeps a series off your trackers. "Full" also keeps it out of stats and history.</Trans>
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
    >
      <Switch
        mb="lg"
        label={t`Write ComicInfo.xml into imported files`}
        checked={settings?.writeComicInfo ?? true}
        onChange={(e) => patch({ writeComicInfo: e.currentTarget.checked })}
      />
      <Switch
        label={t`Save a cover.jpg into each series' library folder`}
        checked={settings?.writeCoverToFolder ?? false}
        onChange={(e) => patch({ writeCoverToFolder: e.currentTarget.checked })}
      />
    </SettingsSection>
  )
}

export function NamingSection() {
  const { t } = useLingui()
  const { settings, patch } = useLibraryPatch()
  const { data: everySeries } = useSeries()
  const allSeries = useMemo(() => activeSeries(everySeries ?? []), [everySeries])
  const seriesCount = allSeries.length
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
      description={<Trans>Files already on disk keep their names until you rename them.</Trans>}
    >
      <Stack gap="md" mb="md">
        <NamingFormatInput
          label={t`Series folder format`}
          value={folderFormat}
          example={preview.data?.seriesFolder}
          error={folderError}
          onChange={setFolderDraft}
          onCommit={saveFormats}
        />
        <NamingFormatInput
          label={t`Chapter file format`}
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
        disabled={!allSeries.length}
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
            Renames the folders and files of all{' '}
            <Plural value={seriesCount} one="# series" other="# series" /> on disk to match the formats
            above. This can take a while.
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
              renameMany.mutate(allSeries.map((s) => s.id), {
                onSuccess: (results) => {
                  const renamed = results.filter((r) => r.applied).length
                  const failed = results.filter((r) => r.error).length
                  const failedCount = formatNumber(failed)
                  notifications.show({
                    // A plural even though "series" does not inflect in English: the count still
                    // drives the verb in Polish and Russian, and only an ICU plural gives them the
                    // categories to do it.
                    message:
                      failed > 0
                        ? plural(renamed, {
                            one: `Renamed # series, ${failedCount} failed`,
                            other: `Renamed # series, ${failedCount} failed`,
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
      <Switch
        label={t`Rename imported files to the chapter file format`}
        checked={settings?.renameImportedFiles ?? true}
        onChange={(e) => patch({ renameImportedFiles: e.currentTarget.checked })}
      />
    </SettingsSection>
  )
}

/** Which languages' volume and chapter words the release and file name parsers read besides English. */
export function NameParsingSection() {
  const { t } = useLingui()
  const { data: settings } = useNameParsingSettings()
  const save = useSaveNameParsingSettings()

  const apply = (languages: string[]) =>
    save.mutate(languages, {
      onSuccess: () => notifications.show({ message: now`Saved`, color: 'var(--ok)' }),
    })

  return (
    <SettingsSection
      id="name-parsing"
      title={<Trans>File name parsing</Trans>}
      description={<Trans>English markers like v01 and Ch. 12 are always read. Turn on a language to read its markers too.</Trans>}
    >
      <Checkbox.Group
        aria-label={t`Languages to read markers in`}
        value={settings?.languages ?? []}
        onChange={apply}
      >
        <Stack gap="sm">
          {settings?.available.map((language) => {
            const name = SUPPORTED_LOCALES.find((l) => l.code === language.code)?.label ?? language.code
            const volumes = language.volumeWords.join(', ')
            const chapters = language.chapterWords.join(', ')
            return (
              <Checkbox
                key={language.code}
                value={language.code}
                label={name}
                disabled={save.isPending}
                description={
                  <>
                    {volumes && <span style={{ display: 'block' }}>{t`Volumes: ${volumes}`}</span>}
                    {chapters && <span style={{ display: 'block' }}>{t`Chapters: ${chapters}`}</span>}
                  </>
                }
              />
            )
          })}
        </Stack>
      </Checkbox.Group>
    </SettingsSection>
  )
}

/**
 * How long deleted files wait, and the way into the bin itself. The list lives on its own page:
 * one removed series can put hundreds of files in it.
 */
export function RecycleBinSection() {
  const { t } = useLingui()
  const { me } = useAuth()
  const isAdmin = me?.isAdmin ?? false
  const { data: bin } = useRecycleBin()
  const setRetention = useSetRecycleBinRetention()
  const [retention, setRetentionDraft] = useState<number | null>(null)

  const count = bin?.entries.length ?? 0
  const days = bin?.retentionDays ?? 14
  const totalSize = formatBytes(bin?.totalBytes)
  const dirty = retention !== null && retention !== days

  return (
    <SettingsSection
      id="recycle-bin"
      title={<Trans>Recycle bin</Trans>}
      description={<Trans>Deleted files wait here before they are removed for good.</Trans>}
      dirty={dirty}
      saving={setRetention.isPending}
      onSave={() => {
        if (retention === null) return
        setRetention.mutate(retention, {
          onSuccess: () => {
            setRetentionDraft(null)
            notifications.show({ message: now`Saved`, color: 'var(--ok)' })
          },
        })
      }}
      onDiscard={() => setRetentionDraft(null)}
    >
      <Stack>
        <Group justify="space-between" wrap="wrap" gap="sm">
          <Text size="sm" c={count === 0 ? 'var(--ink-3)' : undefined}>
            {count === 0 ? (
              <Trans>The recycle bin is empty.</Trans>
            ) : (
              <Plural value={count} one={`# file, ${totalSize}`} other={`# files, ${totalSize}`} />
            )}
          </Text>
          <Button
            component={Link}
            to="/library/recycle-bin"
            variant="default"
            size="xs"
            leftSection={<IconTrash size={16} />}
          >
            <Trans>Open recycle bin</Trans>
          </Button>
        </Group>
        {isAdmin && (
          <SettingsNumberInput
            label={t`Keep deleted files (days)`}
            description={t`0 deletes them for good on the next housekeeping pass.`}
            min={0}
            max={365}
            value={retention ?? days}
            onChange={setRetentionDraft}
          />
        )}
      </Stack>
    </SettingsSection>
  )
}
