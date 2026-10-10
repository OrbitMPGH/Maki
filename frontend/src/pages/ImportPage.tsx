import { useRef, useState } from 'react'
import {
  ActionIcon,
  Badge,
  Button,
  Checkbox,
  Group,
  Image,
  Modal,
  Progress,
  Select,
  Stack,
  Switch,
  Table,
  Text,
  Tooltip,
} from '@mantine/core'
import { IconEyeOff, IconFolderSearch, IconPackageImport, IconSearch } from '@tabler/icons-react'
import { notifications } from '@mantine/notifications'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { msg, plural } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { useMutation } from '@tanstack/react-query'
import { api } from '../api/client'
import { useLibrarySettings, useRootFolders } from '../api/hooks'
import { useIgnoreImportFolder, useIgnoredImportFolders } from '../api/libraryImport'
import { useHubEvent } from '../api/signalr'
import { useLabel } from '../i18n-context'
import { randomUUID } from '../lib/uuid'
import type { MetadataSearchResult } from '../api/types'
import { IgnoredFoldersModal } from '../components/import/IgnoredFoldersModal'
import { ImportMatchFinder } from '../components/import/ImportMatchFinder'
import { EmptyState } from '../components/ui/EmptyState'
import { PageHeader } from '../components/ui/PageHeader'
import { Panel } from '../components/ui/Panel'
import { okButtonVars } from '../components/ui/status'
import { SurfaceFrame } from '../components/ui/SurfaceFrame'

/** Must not exceed LibraryImportController.MaxItemsPerRequest. */
const IMPORT_BATCH_SIZE = 50

/**
 * Words the import stage keys the server sends (`Maki.Api.Services.ImportStage`). The broadcast
 * reaches every admin connection at once and they don't share a language, so the server sends a
 * machine key rather than prose and this is where it becomes words. Descriptors, not rendered
 * strings: this list is built once when the module loads, see {@link useIncognitoOptions} for why.
 */
const STAGE_LABELS: Record<string, MessageDescriptor> = {
  fetchingMetadata: msg`Fetching metadata`,
  renamingFolder: msg`Renaming folder`,
  mergingFolder: msg`Merging folder`,
  downloadingCover: msg`Downloading cover`,
  addingFiles: msg`Adding files`,
  updatingComicInfo: msg`Updating ComicInfo`,
  linkingFiles: msg`Linking files`,
  imported: msg`Imported`,
  failed: msg`Failed`,
}

/** Why a file stayed out of the series (`Maki.Api.Services.ImportSkipReason`). */
const SKIP_REASONS: Record<string, MessageDescriptor> = {
  unreadable: msg`could not be read, the archive is corrupt or truncated`,
  noMatchingChapter: msg`no chapter with this number in the series`,
  unrecognized: msg`no chapter or volume number in the name`,
}

interface ScanCandidate {
  folderName: string
  cleanedTitle: string
  comicCount: number
  recognizedCount: number
  matches: MetadataSearchResult[]
  /** Set when the folder belongs to a series already in the library that has no files yet. */
  existingSeriesId: number | null
}

interface ImportResultDto {
  folderName: string
  success: boolean
  error: string | null
  seriesId: number | null
  newFolderName: string | null
  filesLinked: number
  filesUnrecognized: number
  warnings?: string[] | null
  skipped?: { name: string; reason: string }[] | null
  /** Files registered but not linked yet; the background source match links them. */
  filesAdded: number
  linkPending: boolean
}

/** `seriesFilesLinked` hub event: the background link stage finished one imported series. */
interface FilesLinkedEvent {
  seriesId: number
  linked: number
  total: number
}

interface ImportProgressEvent {
  folderName: string
  stage: string
  current: number | null
  total: number | null
  done: boolean
  success: boolean
  error: string | null
  /** The client-generated id of the import run this event belongs to; absent from older payloads. */
  operationId?: string | null
}

export default function ImportPage() {
  const { t } = useLingui()
  const label = useLabel()
  const { data: rootFolders } = useRootFolders()
  const { data: librarySettings } = useLibrarySettings()
  const [rootFolderId, setRootFolderId] = useState<string | null>(null)
  const [candidates, setCandidates] = useState<ScanCandidate[] | null>(null)
  // The root folder `candidates` was scanned from, so switching the Select above can't send the
  // new root paired with folder names that were only ever scanned from the old one.
  const [scannedRootFolderId, setScannedRootFolderId] = useState<string | null>(null)
  const [selection, setSelection] = useState<Record<string, string>>({}) // folderName -> providerId ('' = skip)
  // Matches picked by hand (search or pasted id), shown ahead of the scan's own candidates.
  const [pickedMatches, setPickedMatches] = useState<Record<string, MetadataSearchResult[]>>({})
  const [finderFor, setFinderFor] = useState<ScanCandidate | null>(null)
  const [ignoredOpen, setIgnoredOpen] = useState(false)
  const scannedRoot = scannedRootFolderId === null ? null : Number(scannedRootFolderId)
  const { data: ignoredFolders } = useIgnoredImportFolders(scannedRoot)
  const ignoreFolder = useIgnoreImportFolder()
  const [results, setResults] = useState<ImportResultDto[] | null>(null)
  const [progress, setProgress] = useState<Record<string, ImportProgressEvent>>({})
  // Keyed by series id. Recorded whatever import they belong to: a quick series can finish linking
  // before the batch that imported it has returned its results.
  const [linkedSeries, setLinkedSeries] = useState<Record<number, FilesLinkedEvent>>({})
  const [confirmOpen, setConfirmOpen] = useState(false)
  const [updateComicInfo, setUpdateComicInfo] = useState(true)
  const [showInLibrary, setShowInLibrary] = useState(false)
  // Read (not rendered) inside the hub handler below to drop events from a run this tab isn't
  // showing, e.g. another admin's import in progress at the same time.
  const operationIdRef = useRef<string | null>(null)
  const keepResultsRef = useRef(false)

  useHubEvent<ImportProgressEvent>('importProgress', (evt) => {
    if (evt.operationId != null && evt.operationId !== operationIdRef.current) return
    setProgress((p) => ({ ...p, [evt.folderName]: evt }))
  })

  useHubEvent<FilesLinkedEvent>('seriesFilesLinked', (evt) => {
    setLinkedSeries((l) => ({ ...l, [evt.seriesId]: evt }))
  })

  const clearScan = () => {
    setCandidates(null)
    setScannedRootFolderId(null)
    setResults(null)
    setSelection({})
    setPickedMatches({})
    setProgress({})
    setLinkedSeries({})
  }

  const matchesOf = (c: ScanCandidate) => {
    const picked = pickedMatches[c.folderName] ?? []
    return [...picked, ...c.matches.filter((m) => !picked.some((p) => p.providerId === m.providerId))]
  }

  const ignore = (folderName: string) => {
    if (scannedRoot === null) return
    ignoreFolder.mutate(
      { rootFolderId: scannedRoot, folderName },
      {
        onSuccess: () => {
          setCandidates((list) => list?.filter((c) => c.folderName !== folderName) ?? null)
          setSelection((s) => ({ ...s, [folderName]: '' }))
        },
      },
    )
  }

  const pickMatch = (folderName: string, match: MetadataSearchResult) => {
    setPickedMatches((p) => ({
      ...p,
      [folderName]: [match, ...(p[folderName] ?? []).filter((m) => m.providerId !== match.providerId)],
    }))
    setSelection((s) => ({ ...s, [folderName]: match.providerId }))
    setFinderFor(null)
  }

  const scan = useMutation({
    mutationFn: (folderId: number) =>
      api<ScanCandidate[]>(`/libraryimport/scan?rootFolderId=${folderId}`),
    onSuccess: (data, folderId) => {
      setCandidates(data)
      setScannedRootFolderId(String(folderId))
      if (!keepResultsRef.current) setResults(null)
      keepResultsRef.current = false
      const initial: Record<string, string> = {}
      for (const c of data) {
        if (c.matches.length > 0 && c.existingSeriesId === null) {
          initial[c.folderName] = c.matches[0].providerId
        }
      }
      setSelection(initial)
    },
  })

  const doImport = useMutation({
    mutationFn: async (payload: {
      rootFolderId: number
      items: { folderName: string; metadataProviderId: string }[]
      updateComicInfo: boolean
      operationId: string
    }) => {
      // The server caps a batch at IMPORT_BATCH_SIZE so one request can't run long enough to hit
      // a proxy timeout. Send sequentially: imports touch the same root folder, and per-row
      // progress arrives over SignalR regardless of how the batches are split.
      // Results are recorded as each batch lands, so a later batch failing outright (network
      // drop, server error) never discards the successes already reported.
      const results: ImportResultDto[] = []
      for (let i = 0; i < payload.items.length; i += IMPORT_BATCH_SIZE) {
        const batch = await api<ImportResultDto[]>('/libraryimport/import', {
          method: 'POST',
          body: JSON.stringify({ ...payload, items: payload.items.slice(i, i + IMPORT_BATCH_SIZE) }),
        })
        results.push(...batch)
        setResults([...results])
      }
      return results
    },
    onMutate: (payload) => {
      operationIdRef.current = payload.operationId
      setResults(null)
      setLinkedSeries({})
      // Every selected row starts out queued; SignalR events overwrite per row.
      const queued: Record<string, ImportProgressEvent> = {}
      for (const item of payload.items) {
        queued[item.folderName] = {
          folderName: item.folderName,
          stage: 'Queued',
          current: null,
          total: null,
          done: false,
          success: false,
          error: null,
        }
      }
      setProgress(queued)
    },
    onSuccess: (data) => {
      const ok = data.filter((r) => r.success).length
      const total = data.length
      notifications.show({
        message: plural(total, {
          one: `Imported ${ok}/# folder`,
          other: `Imported ${ok}/# folders`,
        }),
        color: ok === data.length ? 'var(--ok)' : 'var(--warn)',
      })
      setProgress({})
      if (rootFolderId) {
        keepResultsRef.current = true
        scan.mutate(Number(rootFolderId))
      }
    },
    // Only the local cleanup; results-so-far were already recorded batch by batch above, and the
    // error toast comes from the global handler in main.tsx.
    onError: () => setProgress({}),
  })

  const linkingIds = (results ?? []).filter((r) => r.linkPending && r.seriesId !== null).map((r) => r.seriesId!)
  const linkingTotal = linkingIds.length
  const linkingDone = linkingIds.filter((id) => linkedSeries[id] !== undefined).length

  const inLibraryCount = candidates?.filter((c) => c.existingSeriesId !== null).length ?? 0
  const visibleCandidates = candidates?.filter((c) => showInLibrary || c.existingSeriesId === null) ?? null
  const visibleFolders = new Set(visibleCandidates?.map((c) => c.folderName))
  // A hidden row never imports, even if it was ticked while the toggle was on.
  const selectedItems = Object.entries(selection)
    .filter(([folderName, providerId]) => providerId !== '' && visibleFolders.has(folderName))
    .map(([folderName, metadataProviderId]) => ({ folderName, metadataProviderId }))
  const selectedCount = selectedItems.length

  const importStep =
    Object.keys(progress).length > 0 || doImport.isPending
      ? 'import'
      : candidates === null
        ? 'scan'
        : 'review'
  const importSteps = [
    { key: 'scan', label: t`Scan`, detail: t`Choose a root folder` },
    { key: 'review', label: t`Review`, detail: t`Confirm metadata matches` },
    { key: 'import', label: t`Import`, detail: t`Link files and report results` },
  ] as const

  return (
    <SurfaceFrame className="import-surface" pageStyle="operational">
      <PageHeader
        compact
        title={t`Import library`}
        description={t`Scans a root folder for series Maki doesn't know yet, matches them to metadata, renames each folder to the English title, and links existing comic files to chapters. Files keep their original names; a PDF is kept as it is, never converted.`}
      />

      <div className="import-workspace">
        <ol className="import-step-rail" aria-label={t`Import progress`}>
          {importSteps.map((step) => (
            <li key={step.key} data-active={importStep === step.key}>
              <span className="import-step-dot" aria-hidden="true" />
              <span className="import-step-copy">
                <span>{step.label}</span>
                <small>{step.detail}</small>
              </span>
            </li>
          ))}
        </ol>

        <Group className="import-control-row" mb="lg" align="flex-end">
          <Select
            className="import-root-select"
            label={t`Root folder`}
            data={rootFolders?.map((f) => ({ value: String(f.id), label: f.path })) ?? []}
            disabled={doImport.isPending}
            value={rootFolderId}
            onChange={(v) => {
              setRootFolderId(v)
              // Candidates (and any selection/results/progress built on them) were scanned from
              // scannedRootFolderId; switching roots must not let Import send this new root
              // paired with folder names that only exist under the old one.
              if (v !== scannedRootFolderId) clearScan()
            }}
          />
          <Button
            leftSection={<IconFolderSearch size={16} />}
            onClick={() => rootFolderId && scan.mutate(Number(rootFolderId))}
            loading={scan.isPending}
            disabled={!rootFolderId || doImport.isPending}
          >
            <Trans>Scan</Trans>
          </Button>
          {candidates && candidates.length > 0 && (
            <Button
              color="var(--ok)"
              vars={okButtonVars}
              leftSection={<IconPackageImport size={16} />}
              loading={doImport.isPending}
              disabled={selectedCount === 0}
              onClick={() => {
                // Reopen defaulting to the global "write ComicInfo" setting (still overridable here).
                setUpdateComicInfo(librarySettings?.writeComicInfo ?? true)
                setConfirmOpen(true)
              }}
            >
              <Plural value={selectedCount} one="Import # selected" other="Import # selected" />
            </Button>
          )}
        </Group>
      </div>

      <Modal
        opened={confirmOpen}
        onClose={() => setConfirmOpen(false)}
        title={<Plural value={selectedCount} one="Import # folder?" other="Import # folders?" />}
        size="lg"
      >
        <Text size="sm" mb="xs">
          <Trans>
            Folders are renamed to the English title. Sources are then found in the background, and
            each series' comic files are linked to chapters as soon as its sources are in.
          </Trans>
        </Text>
        <Checkbox
          label={t`Standardize ComicInfo.xml inside the imported files (recommended)`}
          checked={updateComicInfo}
          onChange={(e) => setUpdateComicInfo(e.currentTarget.checked)}
          mb="xs"
        />
        <Text size="xs" c="var(--ink-3)" mb="lg">
          <Trans>
            Rewrites the metadata embedded in each CBZ (title, summary, authors, genres, chapter
            numbers) to Maki's standard so Kavita groups these files with future downloads and
            imports; PDFs are skipped, since there is nowhere in a PDF to put it. If Kavita
            already indexed this library, its existing entries may reshuffle; skipping keeps the
            files byte-for-byte untouched, but they may not group consistently with chapters Maki
            adds later.
          </Trans>
        </Text>
        <Group justify="flex-end">
          <Button variant="default" onClick={() => setConfirmOpen(false)}>
            <Trans>Cancel</Trans>
          </Button>
          <Button
            color="var(--ok)"
            vars={okButtonVars}
            onClick={() => {
              setConfirmOpen(false)
              // rootFolderId must still be the root candidates were scanned from; the Select's
              // onChange already clears candidates on any other change, this is just the guard.
              if (rootFolderId && rootFolderId === scannedRootFolderId) {
                doImport.mutate({
                  rootFolderId: Number(rootFolderId),
                  items: selectedItems,
                  updateComicInfo,
                  operationId: randomUUID(),
                })
              }
            }}
          >
            <Trans>Import</Trans>
          </Button>
        </Group>
      </Modal>

      {results && results.length > 0 && (
        <Stack className="import-results" gap={4} mb="md">
          {linkingTotal > 0 && (
            <Text size="sm" c="var(--ink-2)" mb={4}>
              {linkingDone < linkingTotal ? (
                <Trans>
                  Finding sources and linking files in the background: {linkingDone} of {linkingTotal}{' '}
                  series done
                </Trans>
              ) : (
                <Plural
                  value={linkingTotal}
                  one="Sources found and files linked for # series"
                  other="Sources found and files linked for # series"
                />
              )}
            </Text>
          )}
          {results.map((r) => {
            const folderLabel = r.newFolderName ?? r.folderName
            const { filesLinked, filesUnrecognized, filesAdded } = r
            const skipped = r.skipped ?? []
            const skippedCount = skipped.length
            const linkedEvent = r.linkPending && r.seriesId !== null ? linkedSeries[r.seriesId] : undefined
            const linkedCount = linkedEvent?.linked ?? 0
            const linkTotal = linkedEvent?.total ?? 0
            const notLinked = linkTotal - linkedCount
            const tone = !r.success
              ? 'var(--danger)'
              : skippedCount > 0 || notLinked > 0
                ? 'var(--warn)'
                : r.linkPending && !linkedEvent
                  ? 'var(--ink-2)'
                  : 'var(--ok)'
            return (
              <Stack key={r.folderName} gap={0}>
                <Text c={tone} size="sm">
                  {r.success && r.linkPending ? (
                    linkedEvent ? (
                      <Trans>
                        {folderLabel}: linked {linkedCount} of{' '}
                        <Plural value={linkTotal} one="# file" other="# files" />
                      </Trans>
                    ) : (
                      <Trans>
                        {folderLabel}: added <Plural value={filesAdded} one="# file" other="# files" />, linking
                        once sources are found
                      </Trans>
                    )
                  ) : r.success ? (
                    skippedCount > 0 ? (
                      <Trans>
                        {folderLabel}: linked <Plural value={filesLinked} one="# file" other="# files" />,{' '}
                        <Plural value={skippedCount} one="# not linked" other="# not linked" />
                      </Trans>
                    ) : filesUnrecognized > 0 ? (
                      <Trans>
                        {folderLabel}: linked <Plural value={filesLinked} one="# file" other="# files" />,{' '}
                        <Plural value={filesUnrecognized} one="# unrecognized" other="# unrecognized" />
                      </Trans>
                    ) : (
                      <Trans>
                        {folderLabel}: linked <Plural value={filesLinked} one="# file" other="# files" />
                      </Trans>
                    )
                  ) : (
                    <>
                      {r.folderName}: {r.error}
                    </>
                  )}
                </Text>
                {skipped.map((f) => {
                  const { name } = f
                  const reason = label(SKIP_REASONS[f.reason] ?? f.reason)
                  return (
                    <Text key={name} size="xs" c="dimmed" pl="md">
                      <Trans>
                        {name}: {reason}
                      </Trans>
                    </Text>
                  )
                })}
                {(r.warnings ?? []).map((w) => (
                  <Text key={w} size="xs" c="var(--warn)" pl="md">
                    {w}
                  </Text>
                ))}
              </Stack>
            )
          })}
        </Stack>
      )}

      {(ignoredFolders?.length ?? 0) > 0 && (
        <Button variant="subtle" size="xs" mb="xs" onClick={() => setIgnoredOpen(true)}>
          <Plural
            value={ignoredFolders?.length ?? 0}
            one="# ignored folder"
            other="# ignored folders"
          />
        </Button>
      )}

      {inLibraryCount > 0 && (
        <Switch
          mb="md"
          checked={showInLibrary}
          onChange={(e) => setShowInLibrary(e.currentTarget.checked)}
          label={
            <Plural
              value={inLibraryCount}
              one="Show # series already in the library"
              other="Show # series already in the library"
            />
          }
          description={t`Their folders have comics that are not linked yet. Importing links them into the existing series.`}
        />
      )}

      {rootFolders && rootFolders.length === 0 && (
        <EmptyState
          mood="asking"
          title={t`No root folders yet`}
          description={t`Add a root folder in Settings, then come back to scan it for series to import.`}
          actionLabel={t`Open settings`}
          actionTo="/settings?tab=library&s=root-folders"
        />
      )}

      {visibleCandidates && visibleCandidates.length === 0 && (
        <EmptyState
          mood="asleep"
          title={t`Nothing to import`}
          description={t`Every folder in this root is already in the library, ignored, or holds no comics.`}
        />
      )}

      {visibleCandidates && visibleCandidates.length > 0 && (
        <Panel p={0} className="table-panel">
          <Table.ScrollContainer minWidth={720}>
            <Table className="panel-table import-table">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th w={40} />
                  <Table.Th>
                    <Trans>Folder</Trans>
                  </Table.Th>
                  <Table.Th>
                    <Trans>Files</Trans>
                  </Table.Th>
                  <Table.Th w={420}>
                    <Trans>Match</Trans>
                  </Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {visibleCandidates.map((c) => {
                  const selected = selection[c.folderName] ?? ''
                  const matches = matchesOf(c)
                  const match = matches.find((m) => m.providerId === selected)
                  const rowProgress = progress[c.folderName]
                  const { cleanedTitle, comicCount, recognizedCount, folderName } = c
                  const unrecognizedCount = comicCount - recognizedCount
                  return (
                    <Table.Tr key={c.folderName}>
                      <Table.Td>
                        <Checkbox
                          aria-label={t`Import ${folderName}`}
                          checked={selected !== ''}
                          disabled={matches.length === 0 || doImport.isPending}
                          onChange={(e) => {
                            // Capture before setState: React nulls currentTarget after the handler.
                            const checked = e.currentTarget.checked
                            setSelection((s) => ({
                              ...s,
                              [c.folderName]: checked ? matches[0]?.providerId ?? '' : '',
                            }))
                          }}
                        />
                      </Table.Td>
                      <Table.Td>
                        <Group gap={4} wrap="nowrap" justify="space-between">
                          <Text size="sm" fw={600}>
                            {c.folderName}
                          </Text>
                          {c.existingSeriesId === null && (
                            <Tooltip label={t`Ignore this folder`}>
                              <ActionIcon
                                variant="subtle"
                                size="sm"
                                color="var(--ink-3)"
                                aria-label={t`Ignore ${folderName}`}
                                onClick={() => ignore(c.folderName)}
                                disabled={doImport.isPending || !!rowProgress}
                              >
                                <IconEyeOff size={14} />
                              </ActionIcon>
                            </Tooltip>
                          )}
                        </Group>
                        {c.existingSeriesId !== null ? (
                          <Badge size="xs" variant="light">
                            <Trans>In library</Trans>
                          </Badge>
                        ) : (
                          <Text size="xs" c="var(--ink-3)">
                            <Trans>searched as “{cleanedTitle}”</Trans>
                          </Text>
                        )}
                      </Table.Td>
                      <Table.Td>
                        <Text size="sm">
                          <Plural value={comicCount} one="# comic" other="# comics" />
                        </Text>
                        {recognizedCount < comicCount && (
                          <Badge size="xs" color="var(--warn)" variant="light">
                            <Plural value={unrecognizedCount} one="# unrecognized" other="# unrecognized" />
                          </Badge>
                        )}
                      </Table.Td>
                      <Table.Td>
                        <Group wrap="nowrap" gap="xs">
                          {match?.coverUrl && (
                            <Image src={match.coverUrl} w={32} h={48} radius="sm" fit="cover" alt="" />
                          )}
                          {rowProgress ? (
                            <Stack gap={4} style={{ flex: 1 }}>
                              <Progress
                                size="sm"
                                value={
                                  rowProgress.total
                                    ? (100 * (rowProgress.current ?? 0)) / rowProgress.total
                                    : rowProgress.stage === 'Queued'
                                      ? 0
                                      : 100
                                }
                                animated={!rowProgress.done && rowProgress.stage !== 'Queued'}
                                color={
                                  rowProgress.done
                                    ? rowProgress.success
                                      ? 'var(--ok)'
                                      : 'var(--danger)'
                                    : 'brand'
                                }
                              />
                              <Text
                                size="xs"
                                c={rowProgress.done && !rowProgress.success ? 'var(--danger)' : 'var(--ink-3)'}
                              >
                                {rowProgress.stage === 'Queued' ? (
                                  <Trans>Queued</Trans>
                                ) : (
                                  label(STAGE_LABELS[rowProgress.stage] ?? rowProgress.stage)
                                )}
                                {rowProgress.total
                                  ? ` (${rowProgress.current}/${rowProgress.total})`
                                  : ''}
                                {rowProgress.error ? ` - ${rowProgress.error}` : ''}
                              </Text>
                            </Stack>
                          ) : matches.length === 0 ? (
                            <Group gap="xs" wrap="nowrap" style={{ flex: 1 }}>
                              <Text size="sm" c="var(--danger)" style={{ flex: 1 }}>
                                <Trans>No metadata match found.</Trans>
                              </Text>
                              <Button
                                size="xs"
                                variant="light"
                                leftSection={<IconSearch size={14} />}
                                onClick={() => setFinderFor(c)}
                                disabled={doImport.isPending}
                              >
                                <Trans>Find match</Trans>
                              </Button>
                            </Group>
                          ) : (
                            <Group gap="xs" wrap="nowrap" style={{ flex: 1 }}>
                              <Select
                                aria-label={t`Metadata match for ${folderName}`}
                                data={[
                                  { value: '', label: t`- skip -` },
                                  ...matches.map((m) => ({
                                    value: m.providerId,
                                    label: `${m.title}${m.year ? ` (${m.year})` : ''}`,
                                  })),
                                ]}
                                value={selected}
                                onChange={(v) =>
                                  setSelection((s) => ({ ...s, [c.folderName]: v ?? '' }))
                                }
                                disabled={doImport.isPending}
                                style={{ flex: 1 }}
                              />
                              {c.existingSeriesId === null && (
                                <Tooltip label={t`Search or paste an id`}>
                                  <ActionIcon
                                    variant="subtle"
                                    aria-label={t`Find another match for ${folderName}`}
                                    onClick={() => setFinderFor(c)}
                                    disabled={doImport.isPending}
                                  >
                                    <IconSearch size={16} />
                                  </ActionIcon>
                                </Tooltip>
                              )}
                            </Group>
                          )}
                        </Group>
                      </Table.Td>
                    </Table.Tr>
                  )
                })}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        </Panel>
      )}
      {ignoredOpen && scannedRoot !== null && (
        <IgnoredFoldersModal rootFolderId={scannedRoot} onClose={() => setIgnoredOpen(false)} />
      )}
      {finderFor && (
        <ImportMatchFinder
          folderName={finderFor.folderName}
          initialQuery={finderFor.cleanedTitle}
          onPick={(m) => pickMatch(finderFor.folderName, m)}
          onClose={() => setFinderFor(null)}
        />
      )}
    </SurfaceFrame>
  )
}
