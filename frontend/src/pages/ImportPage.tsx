import { useState } from 'react'
import {
  Badge,
  Button,
  Checkbox,
  Group,
  Image,
  Modal,
  Progress,
  Select,
  Stack,
  Table,
  Text,
} from '@mantine/core'
import { IconFolderSearch, IconPackageImport } from '@tabler/icons-react'
import { notifications } from '@mantine/notifications'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { msg, plural } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { useMutation } from '@tanstack/react-query'
import { api } from '../api/client'
import { useLibrarySettings, useRootFolders } from '../api/hooks'
import { useHubEvent } from '../api/signalr'
import { useLabel } from '../i18n-context'
import type { MetadataSearchResult } from '../api/types'
import { EmptyState } from '../components/ui/EmptyState'
import { PageHeader } from '../components/ui/PageHeader'
import { Panel } from '../components/ui/Panel'
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
  findingSources: msg`Finding sources`,
  syncingChapters: msg`Syncing chapters`,
  updatingComicInfo: msg`Updating ComicInfo`,
  linkingFiles: msg`Linking files`,
  imported: msg`Imported`,
  failed: msg`Failed`,
}

interface ScanCandidate {
  folderName: string
  cleanedTitle: string
  comicCount: number
  recognizedCount: number
  matches: MetadataSearchResult[]
}

interface ImportResultDto {
  folderName: string
  success: boolean
  error: string | null
  seriesId: number | null
  newFolderName: string | null
  filesLinked: number
  filesUnrecognized: number
}

interface ImportProgressEvent {
  folderName: string
  stage: string
  current: number | null
  total: number | null
  done: boolean
  success: boolean
  error: string | null
}

export default function ImportPage() {
  const { t } = useLingui()
  const label = useLabel()
  const { data: rootFolders } = useRootFolders()
  const { data: librarySettings } = useLibrarySettings()
  const [rootFolderId, setRootFolderId] = useState<string | null>(null)
  const [candidates, setCandidates] = useState<ScanCandidate[] | null>(null)
  const [selection, setSelection] = useState<Record<string, string>>({}) // folderName -> providerId ('' = skip)
  const [results, setResults] = useState<ImportResultDto[] | null>(null)
  const [progress, setProgress] = useState<Record<string, ImportProgressEvent>>({})
  const [confirmOpen, setConfirmOpen] = useState(false)
  const [updateComicInfo, setUpdateComicInfo] = useState(true)

  useHubEvent<ImportProgressEvent>('importProgress', (evt) => {
    setProgress((p) => ({ ...p, [evt.folderName]: evt }))
  })

  const scan = useMutation({
    mutationFn: (folderId: number) =>
      api<ScanCandidate[]>(`/libraryimport/scan?rootFolderId=${folderId}`),
    onSuccess: (data) => {
      setCandidates(data)
      setResults(null)
      const initial: Record<string, string> = {}
      for (const c of data) {
        if (c.matches.length > 0) initial[c.folderName] = c.matches[0].providerId
      }
      setSelection(initial)
    },
  })

  const doImport = useMutation({
    mutationFn: async (payload: {
      rootFolderId: number
      items: { folderName: string; metadataProviderId: string }[]
      updateComicInfo: boolean
    }) => {
      // The server caps a batch at IMPORT_BATCH_SIZE so one request can't run long enough to hit
      // a proxy timeout. Send sequentially: imports touch the same root folder, and per-row
      // progress arrives over SignalR regardless of how the batches are split.
      const results: ImportResultDto[] = []
      for (let i = 0; i < payload.items.length; i += IMPORT_BATCH_SIZE) {
        const batch = await api<ImportResultDto[]>('/libraryimport/import', {
          method: 'POST',
          body: JSON.stringify({ ...payload, items: payload.items.slice(i, i + IMPORT_BATCH_SIZE) }),
        })
        results.push(...batch)
      }
      return results
    },
    onMutate: (payload) => {
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
      setResults(data)
      const ok = data.filter((r) => r.success).length
      const total = data.length
      notifications.show({
        message: plural(total, {
          one: `Imported ${ok}/# folder`,
          other: `Imported ${ok}/# folders`,
        }),
        color: ok === data.length ? 'green' : 'yellow',
      })
      setProgress({})
      if (rootFolderId) scan.mutate(Number(rootFolderId))
    },
    // Only the local cleanup; the error toast comes from the global handler in main.tsx.
    onError: () => setProgress({}),
  })

  const selectedItems = Object.entries(selection)
    .filter(([, providerId]) => providerId !== '')
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
            value={rootFolderId}
            onChange={setRootFolderId}
          />
          <Button
            leftSection={<IconFolderSearch size={16} />}
            onClick={() => rootFolderId && scan.mutate(Number(rootFolderId))}
            loading={scan.isPending}
            disabled={!rootFolderId}
          >
            <Trans>Scan</Trans>
          </Button>
          {candidates && candidates.length > 0 && (
            <Button
              color="var(--ok)"
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
          <Trans>Folders are renamed to the English title and their comic files are linked to chapters.</Trans>
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
            onClick={() => {
              setConfirmOpen(false)
              if (rootFolderId) {
                doImport.mutate({
                  rootFolderId: Number(rootFolderId),
                  items: selectedItems,
                  updateComicInfo,
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
          {results.map((r) => {
            const folderLabel = r.newFolderName ?? r.folderName
            const { filesLinked, filesUnrecognized } = r
            return (
              <Text key={r.folderName} c={r.success ? 'var(--ok)' : 'var(--danger)'} size="sm">
                {r.success ? (
                  filesUnrecognized > 0 ? (
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
            )
          })}
        </Stack>
      )}

      {candidates && candidates.length === 0 && (
        <EmptyState
          title={t`Nothing to import`}
          description={t`Every folder in this root is already claimed by a series in the library.`}
        />
      )}

      {candidates && candidates.length > 0 && (
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
                {candidates.map((c) => {
                  const selected = selection[c.folderName] ?? ''
                  const match = c.matches.find((m) => m.providerId === selected)
                  const rowProgress = progress[c.folderName]
                  const { cleanedTitle, comicCount, recognizedCount } = c
                  const unrecognizedCount = comicCount - recognizedCount
                  return (
                    <Table.Tr key={c.folderName}>
                      <Table.Td>
                        <Checkbox
                          checked={selected !== ''}
                          disabled={c.matches.length === 0 || doImport.isPending}
                          onChange={(e) => {
                            // Capture before setState: React nulls currentTarget after the handler.
                            const checked = e.currentTarget.checked
                            setSelection((s) => ({
                              ...s,
                              [c.folderName]: checked ? c.matches[0]?.providerId ?? '' : '',
                            }))
                          }}
                        />
                      </Table.Td>
                      <Table.Td>
                        <Text size="sm" fw={600}>
                          {c.folderName}
                        </Text>
                        <Text size="xs" c="var(--ink-3)">
                          <Trans>searched as “{cleanedTitle}”</Trans>
                        </Text>
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
                          ) : c.matches.length === 0 ? (
                            <Text size="sm" c="var(--danger)">
                              <Trans>No metadata match, rename the folder closer to the title and rescan.</Trans>
                            </Text>
                          ) : (
                            <Select
                              data={[
                                { value: '', label: t`- skip -` },
                                ...c.matches.map((m) => ({
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
    </SurfaceFrame>
  )
}
