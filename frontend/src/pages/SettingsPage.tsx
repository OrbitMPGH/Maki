import { useEffect, useMemo, useRef, useState, type DragEvent, type ReactNode } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useDebouncedValue } from '@mantine/hooks'
import {
  ActionIcon,
  Alert,
  Badge,
  Button,
  Card,
  Checkbox,
  Code,
  Divider,
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
import { INCOGNITO_OPTIONS, type IncognitoMode } from '../components/ui/incognito'
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
import { NotificationsSection } from '../components/NotificationsSection'
import { TrackerSyncControls } from '../components/TrackerSyncControls'
import { useThemeChoice } from '../theme-context'

function formatBytes(bytes: number | null): string {
  if (bytes === null) return '-'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit++
  }
  return `${value.toFixed(1)} ${units[unit]}`
}

function RootFoldersSection() {
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
        Root Folders
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        Library folders where series are stored (point Kavita at the same location).
      </Text>
      <Stack>
        {rootFolders && rootFolders.length > 0 && (
          <Table>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Path</Table.Th>
                <Table.Th>Free space</Table.Th>
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
                        (inaccessible)
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
                      aria-label="Delete root folder"
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
            placeholder="C:\Manga or /library"
            value={newPath}
            onChange={(e) => setNewPath(e.currentTarget.value)}
            style={{ flex: 1 }}
          />
          <Button onClick={add} loading={addFolder.isPending}>
            Add
          </Button>
        </Group>
      </Stack>
    </Card>
  )
}

function SourcePrioritySection() {
  const { data: sources } = useSources()
  const { data: priority } = useSourcePriority()
  const save = useSaveSourcePriority()
  const [order, setOrder] = useState<string[] | null>(null)
  const [disabled, setDisabled] = useState<string[] | null>(null)
  // The real order only changes on drop. While dragging, rows are shifted purely
  // visually (transform) to open a gap; reordering the DOM mid-drag made rows
  // slide past the stationary cursor and re-trigger, causing a feedback loop.
  const [dragFromIndex, setDragFromIndex] = useState<number | null>(null)
  const [hoverIndex, setHoverIndex] = useState<number | null>(null)
  const [rowHeight, setRowHeight] = useState(0)
  const containerRef = useRef<HTMLDivElement>(null)

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

  function handleContainerDragOver(e: DragEvent) {
    e.preventDefault()
    if (dragFromIndex === null || !order || !containerRef.current || rowHeight === 0) return
    const rect = containerRef.current.getBoundingClientRect()
    const rawIndex = Math.floor((e.clientY - rect.top) / rowHeight)
    const clamped = Math.min(Math.max(rawIndex, 0), order.length - 1)
    setHoverIndex(clamped)
  }

  function commitDrag() {
    if (order && dragFromIndex !== null && hoverIndex !== null && dragFromIndex !== hoverIndex) {
      const next = [...order]
      const [moved] = next.splice(dragFromIndex, 1)
      next.splice(hoverIndex, 0, moved)
      setOrder(next)
    }
    setDragFromIndex(null)
    setHoverIndex(null)
  }

  // A source stays in the order while switched off, so turning it back on returns it to
  // exactly the rank it had, and per-series mappings for it are never rewritten.
  function toggle(name: string, on: boolean) {
    setDisabled((current) =>
      on ? (current ?? []).filter((n) => n !== name) : [...(current ?? []), name],
    )
  }

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        Sources
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        When a series auto-matches multiple sources, chapters download from the highest-priority
        enabled source first. Applies to new auto-matches and manual "Auto-match" runs; existing
        series mappings keep their current priorities. Drag to reorder.
      </Text>
      <Text size="sm" c="dimmed" mb="md">
        Switching a source off skips it when auto-matching and stops every series from using it,
        without changing the per-series toggles: turn it back on and each series picks up exactly
        where it was.
      </Text>
      <Stack gap={0} mb="md" ref={containerRef} onDragOver={handleContainerDragOver}>
        {order?.map((name, i) => {
          let shift = 0
          if (dragFromIndex !== null && hoverIndex !== null && i !== dragFromIndex) {
            if (dragFromIndex < hoverIndex && i > dragFromIndex && i <= hoverIndex) shift = -1
            else if (dragFromIndex > hoverIndex && i >= hoverIndex && i < dragFromIndex) shift = 1
          }
          return (
            <div
              key={name}
              style={{
                position: 'relative',
                transform: shift ? `translateY(${shift * rowHeight}px)` : undefined,
                transition: 'transform 150ms ease',
                pointerEvents: dragFromIndex !== null && i !== dragFromIndex ? 'none' : undefined,
              }}
            >
              <Group
                justify="space-between"
                align="center"
                wrap="nowrap"
                py={12}
                px={4}
                draggable
                onDragStart={(e) => {
                  // setDragImage on the live node still tracks it, so the ghost goes
                  // invisible along with the row once opacity flips to 0. Use a detached
                  // clone instead, it's an independent snapshot.
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
                  setDragFromIndex(i)
                  setHoverIndex(i)
                  setRowHeight(original.getBoundingClientRect().height)
                }}
                onDragEnd={commitDrag}
                style={{
                  cursor: 'grab',
                  borderRadius: 4,
                  opacity: dragFromIndex === i ? 0 : 1,
                }}
              >
                <Group gap="sm" wrap="nowrap">
                  <IconGripVertical size={14} opacity={0.5} />
                  <Text size="sm" c="dimmed" w={20}>
                    {i + 1}
                  </Text>
                  <Text size="sm" fw={500} c={disabled?.includes(name) ? 'dimmed' : undefined}>
                    {displayName(name)}
                  </Text>
                  <Text size="xs" c="dimmed">
                    {sources?.find((s) => s.name === name)?.baseUrl}
                  </Text>
                  {sources?.find((s) => s.name === name)?.needsFlareSolverr && (
                    <Badge size="sm" color="orange" variant="light">
                      Needs FlareSolverr
                    </Badge>
                  )}
                </Group>
                <Switch
                  size="xs"
                  checked={!disabled?.includes(name)}
                  onChange={(e) => toggle(name, e.currentTarget.checked)}
                  aria-label={`Enable ${displayName(name)}`}
                  // The row is draggable; without this a drag started on the switch swallows the click.
                  onMouseDown={(e) => e.stopPropagation()}
                  draggable={false}
                />
              </Group>
              {i < (order?.length ?? 0) - 1 && <Divider />}
            </div>
          )
        })}
      </Stack>
      <Button
        variant="default"
        disabled={!dirty}
        loading={save.isPending}
        onClick={() =>
          order &&
          disabled &&
          save.mutate(
            { order, disabled },
            { onSuccess: () => notifications.show({ message: 'Saved', color: 'green' }) },
          )
        }
      >
        Save
      </Button>
    </Card>
  )
}

function MetadataSection() {
  const { data: settings } = useMetadataSettings()
  const save = useSaveMetadataSettings()
  const refresh = useRefreshMetadataDump()

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        Metadata
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        Series metadata comes from MangaBaka. With the local database enabled, Maki keeps a
        nightly snapshot on disk (~3 GB) so searches and library imports are instant instead of
        rate-limited. Until the first download finishes, the API is used automatically.
      </Text>
      <Stack gap="sm">
        <Switch
          label="Use local MangaBaka database"
          checked={settings?.useLocalDb ?? true}
          onChange={(e) =>
            save.mutate(e.currentTarget.checked, {
            })
          }
        />
        <Group justify="space-between">
          <Text size="sm" c="dimmed">
            {settings === undefined
              ? '...'
              : settings.dumpPresent
                ? `Snapshot on disk: ${formatBytes(settings.dumpSizeBytes)}, refreshed ${
                    settings.dumpRefreshedAt
                      ? new Date(settings.dumpRefreshedAt).toLocaleString()
                      : 'at an unknown time'
                  }`
              : 'No snapshot downloaded yet'}
          </Text>
          <Button
            variant="default"
            size="xs"
            loading={refresh.isPending}
            onClick={() =>
              refresh.mutate(undefined, {
                onSuccess: () =>
                  notifications.show({
                    message: 'Refresh started, downloading in the background if a new snapshot is available',
                    color: 'green',
                  }),
              })
            }
          >
            Refresh now
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
              ? 'Turning embeddings off…'
              : `Switching to ${kind}: downloading the model and index…`
            : r.reason,
          color: r.switching ? 'blue' : 'gray',
        }),
      onError: (e) => notifications.show({ message: String(e), color: 'red' }),
    })

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        Recommendations
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        Discover recommends by semantic "feel" and searches by description, using a local embedding
        model. The vectors download prebuilt, so this normally needs no attention; search falls back to titles and
        recommendations to genres whenever it's off or still downloading.
      </Text>

      <RecommendationModelCards status={status} busy={setModel.isPending} onSelect={selectModel} />
    </Card>
  )
}

function MonitoringSection() {
  const { data: settings } = useMonitoringSettings()
  const save = useSaveMonitoringSettings()

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        Monitoring
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        Specials are decimal chapters (10.5 omake, x.1/x.2 splits). When enabled, specials on
        newly added or imported series are marked "not wanted": they stay listed, but they never
        download and they don't count toward the series' chapter total. Applies as each chapter is
        discovered, so specials released later are covered too. Existing chapters are unaffected;
        change them on the series page or in bulk from its Chapters tab.
      </Text>
      <Switch
        label="Don't want specials on new series"
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
        Highest content rating shown in "Add Series" search results, everything up to and
        including it is allowed. Discover and recommendations never surface pornographic titles
        regardless of this setting.
      </Text>
      <ContentRatingCards
        value={settings?.maxContentRating ?? 'erotica'}
        onChange={(rating) => save.mutate(rating)}
      />
    </Card>
  )
}

function LibrarySection() {
  const { data: settings } = useLibrarySettings()
  const save = useSaveLibrarySettings()
  const { data: allSeries } = useSeries()
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
      { onSuccess: () => notifications.show({ message: 'Saved', color: 'green' }) },
    )
  }

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        Library files
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        Maki writes a standardized <Code>ComicInfo.xml</Code> into each CBZ so Kavita groups and
        names chapters consistently. Turn this off to leave imported files (torrent grabs and
        manual imports) exactly as they came; chapters Maki downloads itself from a source still
        get a ComicInfo, since Maki builds those files. You can always standardize a single series
        later with the "Update ComicInfo" bulk action on its page.
      </Text>
      <Switch
        mb="lg"
        label="Write ComicInfo.xml into imported files"
        checked={settings?.writeComicInfo ?? true}
        onChange={(e) =>
          save.mutate(
            {
              writeComicInfo: e.currentTarget.checked,
              folderNamingMode: settings?.folderNamingMode ?? 'rename',
              writeCoverToFolder: settings?.writeCoverToFolder ?? false,
            },
            { onSuccess: () => notifications.show({ message: 'Saved', color: 'green' }) },
          )
        }
      />

      <Switch
        mb="lg"
        label="Save a cover.jpg into each series' library folder"
        description="For other readers (Komga, Kavita) that read a poster placed directly in the folder. Will run immidietly when switched on."
        checked={settings?.writeCoverToFolder ?? false}
        onChange={(e) =>
          save.mutate(
            {
              writeComicInfo: settings?.writeComicInfo ?? true,
              folderNamingMode: settings?.folderNamingMode ?? 'rename',
              writeCoverToFolder: e.currentTarget.checked,
            },
            { onSuccess: () => notifications.show({ message: 'Saved', color: 'green' }) },
          )
        }
      />

      <Text fw={500} size="sm" mb={4}>
        Naming
      </Text>
      <Text size="sm" c="dimmed" mb="sm">
        How Maki names a series' folder and the chapter files it downloads. Both take tokens; the
        "?" button lists every one with an example, and its dialog is directly editable too. A
        change applies to series added and chapters downloaded from here on — nothing already on
        disk moves until you rename it, either from a series' page or with the button below for
        the whole library.
      </Text>
      <Stack gap="md" mb="md">
        <NamingFormatInput
          label="Series Folder Format"
          description="Used when adding a series, importing one, or renaming its folder"
          value={folderFormat}
          example={preview.data?.seriesFolder}
          error={folderError?.replace('Series folder format: ', '')}
          onChange={setFolderDraft}
          onCommit={saveFormats}
        />
        <NamingFormatInput
          label="Chapter Format"
          description="Used for chapters Maki downloads. Files imported from disk keep their own names"
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
        Rename every series to current format
      </Button>

      <Modal
        opened={confirmRenameAll}
        onClose={() => setConfirmRenameAll(false)}
        title="Rename every series"
      >
        <Text size="sm" mb="md">
          Applies the Series Folder Format and Chapter Format above to all {allSeries?.length ?? 0}{' '}
          series in the library, renaming folders and files on disk to match. Series already
          matching the format are left alone. This can take a while for a large library.
        </Text>
        <Group justify="flex-end">
          <Button variant="default" onClick={() => setConfirmRenameAll(false)}>
            Cancel
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
                    message: failed > 0
                      ? `Renamed ${renamed}, ${failed} failed`
                      : `Renamed ${renamed} series`,
                    color: failed > 0 ? 'yellow' : 'green',
                  })
                  setConfirmRenameAll(false)
                },
              })
            }
          >
            Rename all
          </Button>
        </Group>
      </Modal>

      <Text fw={500} size="sm" mb={4}>
        Folder naming on import
      </Text>
      <Text size="sm" c="dimmed" mb="sm">
        Only affects importing an existing series from disk: whether Maki renames its current
        folder to match the Series Folder Format above, or leaves it as found.
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
            { onSuccess: () => notifications.show({ message: 'Saved', color: 'green' }) },
          )
        }
      >
        <Stack gap="xs" mt="xs">
          <Radio value="rename" label="Rename folder to Maki standard" />
          <Radio
            value="keep-new-standard"
            label="Keep folder name, but put new downloads in a Maki standard folder"
          />
          <Radio value="keep-original" label="Keep folder name, and put new downloads there too" />
        </Stack>
      </Radio.Group>

      <Text fw={500} size="sm" mt="lg" mb={4}>
        Incognito by content rating
      </Text>
      <Text size="sm" c="dimmed" mb="sm">
        What the incognito setting is pre-filled with when a series of each rating is added.
        "No scrobble" keeps it off your trackers; "Full" also keeps it out of stats and reading
        history. The add form still shows the value, so any single add can override it, and
        changing a rule here never touches a series already in the library.
      </Text>
      <Stack gap="xs">
        {CONTENT_RATINGS.map((rating) => (
          <Group key={rating} gap="sm" wrap="nowrap">
            <Text size="sm" tt="capitalize" w={110} style={{ flexShrink: 0 }}>
              {rating}
            </Text>
            <Select
              aria-label={`Incognito for ${rating}`}
              data={INCOGNITO_OPTIONS}
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
                  { onSuccess: () => notifications.show({ message: 'Saved', color: 'green' }) },
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
      { onSuccess: () => notifications.show({ message: 'Saved', color: 'green' }) },
    )

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        Reader
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        The fallback for Maki's built-in reader: what a series gets when no reading profile covers
        its type and nothing is pinned or overridden on the series itself.
      </Text>

      <Stack gap="md">
        <Radio.Group
          label="Layout"
          value={defaults.mode}
          onChange={(value) => saveWith({ mode: value as ReaderPrefs['mode'] })}
        >
          <Stack gap="xs" mt="xs">
            <Radio value="paged" label="Single page" />
            <Radio value="double" label="Two pages side by side" />
            <Radio value="vertical" label="Continuous vertical (webtoon)" />
          </Stack>
        </Radio.Group>

        <Radio.Group
          label="Reading direction"
          value={defaults.direction}
          onChange={(value) => saveWith({ direction: value as ReaderPrefs['direction'] })}
        >
          <Stack gap="xs" mt="xs">
            <Radio value="rtl" label="Right to left (manga)" />
            <Radio value="ltr" label="Left to right" />
          </Stack>
        </Radio.Group>

        <Radio.Group
          label="Page fit"
          value={defaults.fit}
          onChange={(value) => saveWith({ fit: value as ReaderPrefs['fit'] })}
        >
          <Stack gap="xs" mt="xs">
            <Radio value="height" label="Fit height" />
            <Radio value="width" label="Fit width" />
            <Radio value="screen" label="Fit screen" />
            <Radio value="original" label="Original size" />
          </Stack>
        </Radio.Group>

        {defaults.fit === 'original' && (
          <div>
            <Text size="sm" fw={500} mb={4}>
              Scale ({scale}%)
            </Text>
            <Slider min={25} max={400} step={5} value={scale} onChange={setScale} onChangeEnd={(value) => saveWith({ scale: value })} />
          </div>
        )}

        <Switch
          label="Advance to the next chapter at the end"
          checked={defaults.autoNextChapter}
          onChange={(e) => saveWith({ autoNextChapter: e.currentTarget.checked })}
        />
        <Switch
          label="Tap zones (click the page edges to turn)"
          checked={defaults.tapZones}
          onChange={(e) => saveWith({ tapZones: e.currentTarget.checked })}
        />
        <div>
          <Switch
            label="Flash the chapter name on chapter change"
            checked={defaults.chapterBanner}
            onChange={(e) => saveWith({ chapterBanner: e.currentTarget.checked })}
          />
          <Text size="xs" c="dimmed" mt={4}>
            Credit pages and the next chapter's opening pages often look the same, so a chapter turn
            can pass unnoticed. This shows the chapter name over the page for a couple of seconds
            when you enter one.
          </Text>
        </div>

        <div>
          <Switch
            label="Mark chapters read in Kavita too"
            checked={settings?.pushToKavita ?? false}
            disabled={!ownsKavita}
            onChange={(e) => saveWith({}, e.currentTarget.checked)}
          />
          <Text size="xs" c="dimmed" mt={4}>
            Off by default. When on, finishing a chapter in Maki's reader also marks it read for
            your Kavita user, so the two stay in step. Only applies to series Maki has matched to a
            Kavita series, reading stats are never counted twice either way.
          </Text>
          {ownsKavita ? null : (
            <Text size="xs" c="dimmed" mt={4}>
              Kavita is one server behind one API key, so its reading belongs to a single Maki
              account, and it isn't yours. An admin picks which one under Settings → Kavita.
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
          notifications.show({ message: 'Saved', color: 'green' })
        },
      },
    )

  const copy = () => {
    if (!feedUrl) return
    void navigator.clipboard
      .writeText(feedUrl)
      .then(() => notifications.show({ message: 'Feed URL copied', color: 'green' }))
  }

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        OPDS
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        Serves the library as an OPDS catalogue so reading apps (Panels, Chunky, KOReader,
        Mihon/Tachiyomi's OPDS extensions) connect straight to Maki, with no Kavita in between.
        Chapters can be downloaded whole or streamed a page at a time.
      </Text>

      <Stack gap="md">
        <div>
          <Switch
            label="Enable the OPDS catalogue"
            checked={enabled}
            onChange={(e) => saveWith({ enabled: e.currentTarget.checked })}
          />
          <Text size="xs" c="dimmed" mt={4}>
            The feed URL carries its own token and is the only credential a reading app needs, so
            anyone holding it can read the whole library. It is deliberately not your API key:
            revoking it below breaks configured readers and nothing else.
          </Text>
        </div>

        {enabled && (
          <div>
            <Text size="sm" fw={500} mb={4}>
              Feed URL
            </Text>
            {feedUrl ? (
              <>
                <Group gap="xs" wrap="nowrap">
                  <Code style={{ overflowWrap: 'anywhere' }}>{feedUrl}</Code>
                  <Tooltip label="Copy feed URL">
                    <ActionIcon variant="light" onClick={copy}>
                      <IconCopy size={16} />
                    </ActionIcon>
                  </Tooltip>
                </Group>
                <Alert color="yellow" variant="light" mt="xs">
                  Copy this now, it is shown only once. Maki stores a fingerprint of the token, not
                  the token, so it cannot be displayed again. Lose it and you regenerate.
                </Alert>
                <Text size="xs" c="dimmed" mt={4}>
                  Paste it into your reading app as an OPDS catalogue. If you reach Maki from outside
                  your network, swap the host for the address you use there.
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
                  Regenerate
                </Button>
              </Group>
            )}
          </div>
        )}

        {enabled && (
          <div>
            <Switch
              label="Track reading progress from OPDS"
              checked={trackProgress}
              onChange={(e) => saveWith({ trackProgress: e.currentTarget.checked })}
            />
            <Text size="xs" c="dimmed" mt={4}>
              Pages fetched by a streaming reader count as read, so OPDS reading shows up in your
              library, Rewind and your trackers. Turn it off if an app reports progress you didn't
              make: some fetch pages ahead, or grab the last page to size their page bar.
            </Text>
          </div>
        )}
      </Stack>

      <Modal
        opened={rotateModalOpen}
        onClose={() => setRotateModalOpen(false)}
        title="Regenerate OPDS token"
        centered
      >
        <Stack>
          <Text size="sm">
            The current feed URL stops working immediately. Every reading app you've set up with it
            will need the new URL.
          </Text>
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setRotateModalOpen(false)}>
              Cancel
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
                    notifications.show({ message: 'New OPDS feed URL generated', color: 'green' })
                  },
                })
              }
            >
              Regenerate
            </Button>
          </Group>
        </Stack>
      </Modal>
    </Card>
  )
}

function KavitaReadImportControl() {
  const { status, start } = useKavitaReadImport()
  const result = status?.result

  return (
    <div>
      <Text fw={500} size="sm" mb={4}>
        Import read status from Kavita
      </Text>
      <Text size="xs" c="dimmed" mb="sm">
        Marks every chapter you've already finished in Kavita as read in Maki, so the built-in
        reader and the library's progress bars don't start from zero. Safe to run more than once:
        it never un-marks anything. These chapters are deliberately left out of Rewind: Kavita
        doesn't say when they were read, and dating them today would pile your whole back
        catalogue onto one day of the year in review. Rewind keeps counting only the reading Maki
        sees happen, through the scrobble sync and its own reader.
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
          Import read status
        </Button>
        {status?.running && (
          <Text size="xs" c="dimmed">
            Reading progress from Kavita…
          </Text>
        )}
        {!status?.running && status?.error && (
          <Text size="xs" c="red">
            {status.error}
          </Text>
        )}
        {!status?.running && !status?.error && result && (
          <Text size="xs" c="dimmed">
            {result.chaptersMarked} chapter(s) marked read across {result.seriesMatched} series
            {result.seriesUnmatched > 0 && `, ${result.seriesUnmatched} Kavita series unmatched`}
          </Text>
        )}
      </Group>
    </div>
  )
}

function DownloadSection() {
  const { data: settings } = useDownloadSettings()
  const save = useSaveDownloadSettings()
  const [concurrentChapters, setConcurrentChapters] = useState<number | string>(2)
  const [retryEnabled, setRetryEnabled] = useState(true)
  const [retryMaxAttempts, setRetryMaxAttempts] = useState<number | string>(5)
  const [smartDownloadChaptersLeft, setSmartDownloadChaptersLeft] = useState<number | string>(5)
  const [smartDownloadChapters, setSmartDownloadChapters] = useState<number | string>(10)
  const [itemTimeoutMinutes, setItemTimeoutMinutes] = useState<number | string>(120)

  useEffect(() => {
    if (settings) {
      setConcurrentChapters(settings.concurrentChapters)
      setRetryEnabled(settings.retryEnabled)
      setRetryMaxAttempts(settings.retryMaxAttempts)
      setSmartDownloadChaptersLeft(settings.smartDownloadChaptersLeft)
      setSmartDownloadChapters(settings.smartDownloadChapters)
      setItemTimeoutMinutes(settings.itemTimeoutMinutes)
    }
  }, [settings])

  const dirty =
    settings !== undefined &&
    (Number(concurrentChapters) !== settings.concurrentChapters ||
      retryEnabled !== settings.retryEnabled ||
      Number(retryMaxAttempts) !== settings.retryMaxAttempts ||
      Number(smartDownloadChaptersLeft) !== settings.smartDownloadChaptersLeft ||
      Number(smartDownloadChapters) !== settings.smartDownloadChapters ||
      Number(itemTimeoutMinutes) !== settings.itemTimeoutMinutes)

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        Downloads
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        How many chapters download at once from scraper sources. Higher isn't always faster:
        each worker is a live connection to the same site, and tripping its rate limit pauses
        every download. Torrent releases aren't affected. Takes effect after a restart.
      </Text>
      <NumberInput
        label="Concurrent chapter downloads"
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
        Automatically downloads the next chapters of a series when you have only a few unread chapters left. 
        The settings below control how many unread chapters trigger the download and how many chapters are downloaded at once.
        Runs every five minutes, based on reading progress from Kavita or the built-in reader. Enabled per series as a monitoring option.
      </Text>
      <Group align="flex-end" mb="md">
        <NumberInput
        label="Chapters unread before trigger"
        min={1}
        max={10}
        clampBehavior="strict"
        value={smartDownloadChaptersLeft}
        onChange={setSmartDownloadChaptersLeft}
        w={220}
        mb="md"
      />
      <NumberInput
        label="Chapters to download at once"
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
        Stuck downloads
      </Text>
      <Text size="sm" c="dimmed" mb="xs">
        A chapter that never finishes holds a worker for as long as the app runs, and with only a
        couple of workers that stops the whole queue: everything else sits on "Queued" with nothing
        wrong with it. Past this many minutes the download is abandoned and marked failed, so retry
        handling takes over. Set 0 to remove the limit. Takes effect after a restart.
      </Text>
      <NumberInput
        label="Give up on a chapter after (minutes)"
        min={0}
        max={1440}
        clampBehavior="strict"
        value={itemTimeoutMinutes}
        onChange={setItemTimeoutMinutes}
        w={220}
        mb="md"
      />
      <Text fw={500} size="sm" mb={4}>
        Retry Handling
      </Text>
      <Text size="sm" c="dimmed" mb="xs">
        Failed downloads are automatically retried on an escalating backoff (5m, 10m, 20m, ...) up
        to the attempt cap below. A manual retry from the Activity page doesn't count against it.
      </Text>
      <Group align="flex-end" mb="md">
        <Switch
          label="Automatically retry failed downloads"
          checked={retryEnabled}
          onChange={(e) => setRetryEnabled(e.currentTarget.checked)}
        />
        <NumberInput
          label="Max attempts"
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
            },
            {
              onSuccess: () =>
                notifications.show({ message: 'Saved', color: 'green' }),
            },
          )
        }
      >
        Save
      </Button>
    </Card>
  )
}

type RestoreTarget = { kind: 'existing'; name: string } | { kind: 'upload'; file: File }

function BackupSection() {
  const { data: backups } = useBackups()
  const { data: retentionSettings } = useBackupSettings()
  const create = useCreateBackup()
  const remove = useDeleteBackup()
  const restore = useRestoreBackup()
  const upload = useUploadRestore()
  const saveRetention = useSaveBackupSettings()

  const [retention, setRetention] = useState<number | string>(5)
  const [target, setTarget] = useState<RestoreTarget | null>(null)

  useEffect(() => {
    if (retentionSettings) setRetention(retentionSettings.retention)
  }, [retentionSettings])

  const retentionDirty =
    retentionSettings !== undefined && Number(retention) !== retentionSettings.retention

  const restarting = () =>
    notifications.show({
      title: 'Restore staged',
      message: 'Maki is restarting to apply it. Reload in a moment.',
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
      notifications.show({ title: 'Restore failed', message: e.message, color: 'red' })

    if (target.kind === 'existing') restore.mutate(target.name, { onSuccess, onError })
    else upload.mutate(target.file, { onSuccess, onError })
  }

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        Backup &amp; Restore
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        A backup is a zip of your database and <Code>config.json</Code>, your whole library and all
        settings. Big, re-downloadable data (the MangaBaka dump, embeddings, covers, cache) is left
        out. One is taken automatically right before any upgrade migration runs. Restoring replaces
        the current data and restarts Maki.
      </Text>
      <Alert color="yellow" icon={<IconAlertTriangle size={16} />} mb="md" variant="light">
        Backup files contain your settings secrets (API keys, passwords) in plain text. Treat a
        downloaded backup like a password. Restore auto-recovers only under a supervisor (Docker /
        systemd); a bare process just stops and you restart it yourself.
      </Alert>

      <Stack>
        {backups && backups.length > 0 && (
          <Table>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Created</Table.Th>
                <Table.Th>Kind</Table.Th>
                <Table.Th>Version</Table.Th>
                <Table.Th>Size</Table.Th>
                <Table.Th />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {backups.map((b) => (
                <Table.Tr key={b.name}>
                  <Table.Td>{new Date(b.manifest.createdUtc).toLocaleString()}</Table.Td>
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
                        Restore
                      </Button>
                      <ActionIcon
                        variant="subtle"
                        onClick={() => void downloadBackup(b.name)}
                        aria-label="Download backup"
                      >
                        <IconDownload size={16} />
                      </ActionIcon>
                      <ActionIcon
                        variant="subtle"
                        color="red"
                        onClick={() => remove.mutate(b.name)}
                        aria-label="Delete backup"
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
                onSuccess: () => notifications.show({ message: 'Backup created', color: 'green' }),
              })
            }
            loading={create.isPending}
          >
            Back up now
          </Button>
          <FileButton onChange={(f) => f && setTarget({ kind: 'upload', file: f })} accept=".zip">
            {(props) => (
              <Button {...props} variant="default" leftSection={<IconUpload size={16} />}>
                Restore from file…
              </Button>
            )}
          </FileButton>
        </Group>

        <Group align="flex-end">
          <NumberInput
            label="Backups to keep (per kind)"
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
                { onSuccess: () => notifications.show({ message: 'Saved', color: 'green' }) },
              )
            }
          >
            Save
          </Button>
        </Group>
      </Stack>

      <Modal opened={target !== null} onClose={() => setTarget(null)} title="Restore backup" centered>
        <Stack>
          <Text size="sm">
            This replaces your current library and settings with{' '}
            {target?.kind === 'upload' ? (
              <b>{target.file.name}</b>
            ) : (
              <b>{target?.kind === 'existing' ? target.name : ''}</b>
            )}
            , then restarts Maki. The current data is not kept, take a backup first if you want a
            way back.
          </Text>
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setTarget(null)}>
              Cancel
            </Button>
            <Button color="red" loading={restore.isPending || upload.isPending} onClick={confirmRestore}>
              Restore &amp; restart
            </Button>
          </Group>
        </Stack>
      </Modal>
    </Card>
  )
}

function ProwlarrOptionsSection() {
  const { data: connection } = useConnectionSettings<Record<string, string | null>>('prowlarr')
  const configured = Boolean(connection?.url && connection?.apiKey)
  const { data: indexers, error: indexersError } = useProwlarrIndexers(configured)
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
        Restrict release searches to specific indexers and Torznab categories. With nothing
        selected, every indexer and category is searched.
      </Text>
      )}
      {configured && indexersError != null && (
        <Text size="sm" c="red">
          Could not load indexers from Prowlarr: {String(indexersError)}
        </Text>
      )}
      {configured && indexers && (
        <Stack gap="sm">
          <Stack gap={6}>
            {indexers.map((indexer) => (
              <Checkbox
                key={indexer.id}
                label={`${indexer.name}${indexer.enable ? '' : ' (disabled in Prowlarr)'}`}
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
            ))}
            {indexers.length === 0 && (
              <Text size="sm" c="dimmed">
                No indexers configured in Prowlarr.
              </Text>
            )}
          </Stack>
          <MultiSelect
            label="Categories"
            placeholder={categories.length === 0 ? 'All categories' : undefined}
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
                    onSuccess: () => notifications.show({ message: 'Saved', color: 'green' }),
                  },
                )
              }
            >
              Save
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
        Required for Cloudflare-protected sources like MangaFire. Point this at a running
        FlareSolverr instance (e.g. http://localhost:8191).
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
                notifications.show({ message: 'FlareSolverr is reachable', color: 'green' }),
            })
          }
        >
          Test
        </Button>
        <Button
          loading={save.isPending}
          onClick={() =>
            save.mutate(url || null, {
              onSuccess: () => notifications.show({ message: 'Saved', color: 'green' }),
            })
          }
        >
          Save
        </Button>
      </Group>
    </Card>
  )
}

function ScrobbleSection() {
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

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="xs">
        Scrobbling
      </Title>
      <Text size="sm" c="dimmed" mb="sm">
        Pushes your Kavita reading progress to AniList, MyAnimeList and MangaBaka (any
        combination, leave a site's credentials empty to disable it). Manage connections and
        review matches on the Scrobble page. Uses the Kavita connection configured above.
      </Text>
      <Stack gap="xs">
        <Text size="sm" fw={600}>
          AniList
        </Text>
        <Text size="xs" c="dimmed">
          Create an API client at anilist.co/settings/developer with redirect URL{' '}
          <Code>{origin}/api/v1/scrobble/oauth/anilist</Code>
        </Text>
        <Group grow>
          <TextInput
            label="Client ID"
            value={form?.aniListClientId ?? ''}
            onChange={(e) => set({ aniListClientId: e.currentTarget.value })}
          />
          <TextInput
            label="Client secret"
            type="password"
            value={form?.aniListClientSecret ?? ''}
            onChange={(e) => set({ aniListClientSecret: e.currentTarget.value })}
          />
        </Group>
        <TrackerSyncControls service="anilist" label="AniList" connection={conn('anilist')} />

        <Text size="sm" fw={600} mt="xs">
          MyAnimeList
        </Text>
        <Text size="xs" c="dimmed">
          Create an API client at myanimelist.net/apiconfig (App Type: web) with redirect URL{' '}
          <Code>{origin}/api/v1/scrobble/oauth/mal</Code>. Paste the <b>Client ID</b> (not the
          secret) exactly as shown there. If connecting opens a browser “sign in to
          myanimelist.net” popup and then <Code>invalid_client</Code>, MyAnimeList didn&apos;t
          recognise the Client ID: re-copy it and make sure the App Type is set.
        </Text>
        <Group grow>
          <TextInput
            label="Client ID"
            value={form?.malClientId ?? ''}
            onChange={(e) => set({ malClientId: e.currentTarget.value })}
          />
          <TextInput
            label="Client secret"
            type="password"
            value={form?.malClientSecret ?? ''}
            onChange={(e) => set({ malClientSecret: e.currentTarget.value })}
          />
        </Group>
        <TrackerSyncControls service="mal" label="MyAnimeList" connection={conn('mal')} />

        <Text size="sm" fw={600} mt="xs">
          MangaBaka
        </Text>
        <TextInput
          label="Personal Access Token"
          description="From MangaBaka settings, no OAuth needed, works immediately"
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
            label="Email"
            value={form?.kitsuEmail ?? ''}
            onChange={(e) => set({ kitsuEmail: e.currentTarget.value })}
          />
          <TextInput
            label="Password"
            type="password"
            value={form?.kitsuPassword ?? ''}
            onChange={(e) => set({ kitsuPassword: e.currentTarget.value })}
          />
        </Group>
        <TrackerSyncControls service="kitsu" label="Kitsu" connection={conn('kitsu')} />

        <Group grow mt="xs">
          <TextInput
            label="Sync interval (minutes)"
            value={form?.intervalMinutes?.toString() ?? '30'}
            onChange={(e) => {
              const parsed = parseInt(e.currentTarget.value, 10)
              set({ intervalMinutes: Number.isNaN(parsed) ? 30 : parsed })
            }}
          />
          <TextInput
            label="Kavita library ids"
            description="Comma-separated; empty = scrobble all libraries"
            value={form?.libraryIds ?? ''}
            onChange={(e) => set({ libraryIds: e.currentTarget.value })}
          />
        </Group>
        <Switch
          label="Add unread series as plan-to-read"
          description="Series in Kavita with no reading progress are added to the sites as 'plan to read'. Never modifies entries already on your lists."
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
                onSuccess: () => notifications.show({ message: 'Saved', color: 'green' }),
              })
            }
          >
            Save
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
  const { data: ui } = useUiSettings()
  const patch = useUiPatch()
  const { data: metadata } = useMetadataSettings()
  const discoverAvailable = Boolean(metadata?.useLocalDb && metadata?.dumpPresent)
  const homeEnabled = ui?.homeLayout.enabled ?? true

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb={4}>
        Start page
      </Title>
      <Text size="sm" c="dimmed" mb="sm">
        Which page Maki opens on. Stored on the server, so it applies on every device.
      </Text>
      <Select
        data={[
          // Disabled rather than hidden, mirroring how the nav drops these tabs: offering a
          // choice that silently degrades to somewhere else is worse than saying why it's out.
          { value: 'home', label: 'Home', disabled: !homeEnabled },
          { value: 'library', label: 'Library' },
          { value: 'discover', label: 'Discover', disabled: !discoverAvailable },
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
 * The two supplementary rails on a series page. Both are extras around the chapter list and both
 * cost a catalogue query, so somebody who never uses them can turn them off and stop paying for them.
 */
function SeriesPageSection() {
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
        Series page
      </Title>
      <Text size="sm" c="dimmed" mb="sm">
        Which rails appear below the chapter list. Turning one off also stops it being fetched.
      </Text>
      <Stack gap="sm">
        <Switch
          checked={related}
          disabled={!patch}
          onChange={(e) => write({ related: e.currentTarget.checked })}
          label="Related series"
          description="Sequels, prequels, spin-offs and side stories that MangaBaka has linked to this one."
        />
        <Switch
          checked={similar}
          disabled={!patch}
          onChange={(e) => write({ similar: e.currentTarget.checked })}
          label="More like this"
          description="Titles that read alike, matched on feel rather than on a declared relation. Needs the recommendation index."
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
            Home screen
          </Title>
          <Text size="sm" c="dimmed">
            Pick which sections appear and what order they run in. Turn Home off entirely if you
            don&apos;t read in Maki: the tab disappears and the library takes over as the start
            page.
          </Text>
        </div>
        <Switch
          checked={homeEnabled}
          disabled={!patch}
          onChange={(e) =>
            patch?.({ homeLayout: { enabled: e.currentTarget.checked, sections } })
          }
          aria-label="Enable the Home screen"
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
                  aria-label={`Move ${HOME_SECTION_LABELS[section.key]} up`}
                  onClick={() => move(index, -1)}
                >
                  <IconChevronUp size={15} />
                </ActionIcon>
                <ActionIcon
                  variant="subtle"
                  color="gray"
                  size="sm"
                  disabled={index === sections.length - 1 || !patch}
                  aria-label={`Move ${HOME_SECTION_LABELS[section.key]} down`}
                  onClick={() => move(index, 1)}
                >
                  <IconChevronDown size={15} />
                </ActionIcon>
                <Text size="sm" fw={550} style={{ flex: 1 }}>
                  {HOME_SECTION_LABELS[section.key]}
                </Text>
                <Switch
                  size="sm"
                  checked={section.enabled}
                  disabled={!patch}
                  onChange={(e) => toggle(index, e.currentTarget.checked)}
                  aria-label={`Show ${HOME_SECTION_LABELS[section.key]}`}
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
  const { themeId, setThemeId, presets } = useThemeChoice()

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb={4}>
        Appearance
      </Title>
      <Text size="sm" c="dimmed" mb="sm">
        Pick an accent colour, or switch to the light theme. Applies instantly and is remembered
        on this device.
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
                {p.label}
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
        General
      </Title>
      <Stack gap="xs">
        <Group>
          <Text size="sm" w={80}>
            Port
          </Text>
          <Code>{general?.port ?? '...'}</Code>
        </Group>
        {/* The instance API key used to live here, with a regenerate button. There is no instance
            key any more: credentials belong to accounts and are created under My account, where
            each one can be revoked without affecting anything else. */}
        <Group justify="space-between" mt="xs">
          <Text size="sm" c="dimmed">
            Re-open the first-time setup guide.
          </Text>
          <Button
            variant="default"
            size="xs"
            loading={completeSetup.isPending}
            onClick={() => completeSetup.mutate(false)}
          >
            Run setup guide
          </Button>
        </Group>
      </Stack>
    </Card>
  )
}

function UpdatesSection() {
  const { data: settings } = useUpdateSettings()
  const save = useSaveUpdateSettings()
  const { data: status } = useUpdateStatus()
  const checkNow = useCheckForUpdatesNow()

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        Updates
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        Checks GitHub daily for a newer release and raises a banner and a Notifications event
        when one is found.{' '}
        {status?.isDocker
          ? "Docker installs are notify-only, pull the new image and recreate the container."
          : 'Bare installs are notify-only, pull the latest code and rebuild.'}
      </Text>
      <Stack gap="sm">
        <Switch
          label="Check for updates"
          checked={settings?.checkForUpdates ?? true}
          onChange={(e) => save.mutate(e.currentTarget.checked)}
        />
        <Group justify="space-between">
          <Text size="sm" c="dimmed">
            {status?.isDevBuild
              ? 'Unofficial build, update checks are skipped.'
              : status?.updateAvailable
                ? `Update available: ${status.latestVersion}`
                : status?.checkedAt
                  ? `Up to date, last checked ${new Date(status.checkedAt).toLocaleString()}`
                  : 'Not checked yet'}
          </Text>
          <Button
            variant="default"
            size="xs"
            loading={checkNow.isPending}
            disabled={status?.isDevBuild}
            onClick={() =>
              checkNow.mutate(undefined, {
                onSuccess: (r) =>
                  notifications.show({
                    message: r.updateAvailable
                      ? `Maki ${r.latestVersion} is available`
                      : 'Already up to date',
                    color: r.updateAvailable ? 'yellow' : 'green',
                  }),
              })
            }
          >
            Check now
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
          message: r.started ? 'Rebuilding image cache' : (r.message ?? 'Already running'),
          color: r.started ? 'green' : 'yellow',
        })
      },
      onError: (e) => notifications.show({ message: String(e), color: 'red' }),
    })

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        Image cache
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        Clears the reader&apos;s page thumbnails and the source-comparison samples, drops poster
        folders for series that no longer exist, and re-downloads series posters from the metadata
        provider. Thumbnails come back on their own the next time a chapter is opened, so nothing is
        lost by clearing them.
      </Text>

      {usage && (
        <Stack gap={4} mb="md">
          <Text size="sm" c="dimmed">
            Posters: {usage.coverFiles.toLocaleString()} files, {formatBytes(usage.coverBytes)}
            {usage.coversMissing > 0
              ? ` - ${usage.coversMissing.toLocaleString()} of ${usage.seriesTotal.toLocaleString()} series have no usable poster`
              : ' - every series has one'}
          </Text>
          <Text size="sm" c="dimmed">
            Reader thumbnails: {usage.thumbnailFiles.toLocaleString()} files,{' '}
            {formatBytes(usage.thumbnailBytes)}
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
          {running
            ? status?.phase === 'clearing'
              ? 'Clearing cached images...'
              : `Rebuilding posters, ${status?.processed ?? 0} of ${status?.total ?? 0}`
            : status?.lastError
              ? `Last run failed: ${status.lastError}`
              : status?.finishedAt
                ? `Last run ${new Date(status.finishedAt).toLocaleString()}: ${status.downloaded.toLocaleString()} posters downloaded, ${status.failed.toLocaleString()} failed, ${status.thumbnailsCleared.toLocaleString()} cached images cleared`
                : 'Not run yet'}
        </Text>
        <Group gap="xs">
          <Button
            variant="default"
            size="xs"
            loading={rebuild.isPending}
            disabled={running}
            onClick={() => start(false)}
          >
            Rebuild missing
          </Button>
          <Button
            variant="default"
            size="xs"
            disabled={running || rebuild.isPending}
            onClick={() => setConfirmForce(true)}
          >
            Rebuild all
          </Button>
        </Group>
      </Group>

      <Modal
        opened={confirmForce}
        onClose={() => setConfirmForce(false)}
        title="Rebuild every poster"
        centered
      >
        <Stack>
          <Text size="sm">
            This re-downloads the poster for all {usage?.seriesTotal.toLocaleString() ?? ''} series,
            one metadata lookup and one image each. On a large library it runs for several minutes.
            Use &quot;Rebuild missing&quot; instead if you are only fixing covers that fail to load.
          </Text>
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setConfirmForce(false)}>
              Cancel
            </Button>
            <Button
              onClick={() => {
                setConfirmForce(false)
                start(true)
              }}
            >
              Rebuild all
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
  const { data: bound } = useKavitaUser()
  const { data: users } = useUsers()
  const save = useSetKavitaUser()

  const options = (users ?? [])
    .filter((u) => !u.disabled && !u.pendingSetup)
    .map((u) => ({ value: String(u.id), label: u.displayName || u.userName }))

  return (
    <Card withBorder radius="md" padding="md">
      <Title order={4} mb="sm">
        Kavita reading
      </Title>
      <Text size="sm" c="dimmed" mb="md">
        Whose reading history Kavita's progress is recorded as. Unset means the lowest-numbered admin,
        which is what a single-user instance wants. Only this account can import read status from
        Kavita or push its reads back.
      </Text>
      <Select
        label="Attribute Kavita's reading to"
        placeholder="Lowest-numbered admin"
        clearable
        data={options}
        value={bound?.userId != null ? String(bound.userId) : null}
        onChange={(value) =>
          save.mutate(value === null ? null : Number(value), {
            onSuccess: () => notifications.show({ message: 'Saved', color: 'green' }),
          })
        }
      />
    </Card>
  )
}

/**
 * Every card, keyed by its registry id. The registry decides order, tab and who may see it; this
 * only says how each id is built, so adding a setting is one entry there plus one line here.
 */
const SECTION_NODES: Record<string, ReactNode> = {
  account: <AccountSection />,
  'notification-prefs': <NotificationPrefsSection />,
  appearance: <AppearanceSection />,
  'start-page': <StartPageSection />,
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
  sources: <SourcePrioritySection />,
  flaresolverr: <FlareSolverrSection />,
  prowlarr: (
    <ConnectionSettingsCard
      name="prowlarr"
      title="Prowlarr"
      description="Search manga releases on your indexers. Uses Prowlarr's aggregated search API, no app sync needed."
      fields={[
        { key: 'url', label: 'URL', placeholder: 'http://localhost:9696' },
        { key: 'apiKey', label: 'API key', secret: true },
      ]}
    >
      <ProwlarrOptionsSection />
    </ConnectionSettingsCard>
  ),
  qbittorrent: (
    <ConnectionSettingsCard
      name="qbittorrent"
      title="qBittorrent"
      description="Download client for grabbed releases. Completed torrents are imported into the library automatically (category defaults to 'maki'). If qBittorrent reports download paths Maki can't reach (e.g. it runs in Docker and reports /downloads while Maki sees Z:\downloads), fill the optional path mapping to translate them."
      fields={[
        { key: 'url', label: 'URL', placeholder: 'http://localhost:8080' },
        { key: 'username', label: 'Username' },
        { key: 'password', label: 'Password', secret: true },
        { key: 'category', label: 'Category', placeholder: 'maki' },
        { key: 'pathMapFrom', label: 'Path mapping - qBittorrent side', placeholder: '/downloads (optional)' },
        { key: 'pathMapTo', label: 'Path mapping - Maki side', placeholder: 'Z:\\downloads (optional)' },
      ]}
    />
  ),

  'kavita-user': <KavitaUserSection />,
  kavita: (
    <ConnectionSettingsCard
      name="kavita"
      title="Kavita"
      description="When configured, Maki asks Kavita to scan the series folder right after new chapters download or imported files change, then pushes the series poster, web links and publication status into Kavita (covers you've set yourself in Kavita are never overwritten). Get the API key from Kavita under User Settings → 3rd Party Clients. If Kavita sees the library under a different path (e.g. it runs in Docker), fill the optional path mapping so Maki translates folder paths."
      fields={[
        { key: 'url', label: 'URL', placeholder: 'http://localhost:5000' },
        { key: 'apiKey', label: 'API key', secret: true },
        { key: 'pathMapFrom', label: 'Path mapping - Maki side', placeholder: 'C:\\Manga (optional)' },
        { key: 'pathMapTo', label: 'Path mapping - Kavita side', placeholder: '/manga (optional)' },
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
}

export default function SettingsPage() {
  const { me, can } = useAuth()
  const isAdmin = me?.isAdmin ?? false
  const [searchParams, setSearchParams] = useSearchParams()

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
        title="Settings"
        face="text"
        description={
          isAdmin
            ? 'Storage, metadata, download clients and integrations for your Maki instance.'
            : 'Your account and how Maki looks.'
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
              {tab.label}
            </Tabs.Tab>
          ))}
        </Tabs.List>

        {tabs.map((tab) => (
          <Tabs.Panel key={tab.key} value={tab.key}>
            <Stack maw={820}>
              <Text size="sm" c="dimmed">
                {tab.description}
              </Text>
              {visible
                .filter((entry) => entry.tab === tab.key)
                .map((entry) => (
                  <div key={entry.id} id={`setting-${entry.id}`} style={{ scrollMarginTop: 80 }}>
                    {SECTION_NODES[entry.id]}
                  </div>
                ))}
            </Stack>
          </Tabs.Panel>
        ))}
      </Tabs>
    </>
  )
}
