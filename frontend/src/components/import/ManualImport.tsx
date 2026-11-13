import { useEffect, useMemo, useState } from 'react'
import {
  Badge,
  Button,
  Checkbox,
  Group,
  Loader,
  SegmentedControl,
  Select,
  Stack,
  Table,
  Text,
  UnstyledButton,
} from '@mantine/core'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { useSeries } from '../../api/hooks'
import { useAuth } from '../../auth/AuthProvider'
import { useDropFolderStatus, type DropFolderPending, type DropFolderPendingReason } from '../../api/dropFolder'
import {
  useManualImport,
  useManualImportBrowse,
  useManualImportPlan,
  type ManualImportFile,
} from '../../api/manualImport'
import { errorText } from '../../api/errorText'
import { formatBytes } from '../../format'
import { IconArrowUp, IconFileImport, IconFileZip, IconFolder, IconInbox } from '../../lib/icons'
import { notifications } from '../../lib/toast'
import { EmptyState } from '../ui/EmptyState'
import { Panel } from '../ui/Panel'
import { SectionHeader } from '../ui/SectionHeader'
import { okButtonVars } from '../ui/status'

type Mode = 'SkipExisting' | 'Replace'

function PendingReason({ reason }: { reason: DropFolderPendingReason }) {
  switch (reason) {
    case 'ambiguous':
      return <Trans>names more than one series</Trans>
    case 'failed':
      return <Trans>could not be imported</Trans>
    case 'nothingNew':
      return <Trans>brings nothing new</Trans>
    default:
      return <Trans>no matching series</Trans>
  }
}

/** What the drop folder left for a person: each opens in the picker above. */
function DropFolderWaiting({ onOpen }: { onOpen: (entry: DropFolderPending) => void }) {
  const { t } = useLingui()
  const { me } = useAuth()
  const { data: status } = useDropFolderStatus(me?.isAdmin ?? false)
  if (!status?.enabled || status.pending.length === 0) return null
  return (
    <Panel p="md">
      <Stack gap="xs">
        <SectionHeader icon={IconInbox} title={t`Waiting in the drop folder`} count={status.pending.length} />
        {status.pending.map((p) => (
          <Group key={p.path} justify="space-between" wrap="nowrap" gap="sm">
            <Stack gap={0} style={{ minWidth: 0 }}>
              <Text size="sm" style={{ overflowWrap: 'anywhere' }}>
                {p.name}
              </Text>
              <Text size="xs" c={p.reason === 'failed' ? 'var(--danger)' : 'var(--ink-3)'}>
                <PendingReason reason={p.reason} />
                {p.seriesTitles.length > 0 && `: ${p.seriesTitles.join(', ')}`}
                {p.detail && ` (${p.detail})`}
              </Text>
            </Stack>
            <Button size="xs" variant="light" onClick={() => onOpen(p)}>
              <Trans>Review</Trans>
            </Button>
          </Group>
        ))}
      </Stack>
    </Panel>
  )
}

function fileLabel(f: ManualImportFile) {
  const volume =
    f.volume === null ? null : f.volumeEnd !== null && f.volumeEnd !== f.volume ? `${f.volume}-${f.volumeEnd}` : `${f.volume}`
  return [volume && `Vol.${volume}`, f.chapter && `Ch.${f.chapter}`].filter(Boolean).join(' ')
}

/**
 * Import comics from a folder or file anywhere the caller may read into a series they pick. The
 * server plans it with the same rules a parked torrent uses, so the review shows which chapters each
 * file covers and which existing files it would replace before anything is touched.
 */
export function ManualImport({ initialPath = null }: { initialPath?: string | null }) {
  const { t } = useLingui()
  const [path, setPath] = useState<string | null>(initialPath)
  const [picked, setPicked] = useState<string | null>(initialPath)
  const [seriesId, setSeriesId] = useState<string | null>(null)
  const [mode, setMode] = useState<Mode>('SkipExisting')
  const [move, setMove] = useState(false)
  const [skipped, setSkipped] = useState<Set<string>>(new Set())
  const browse = useManualImportBrowse(path)
  const { data: series } = useSeries()
  const plan = useManualImportPlan(picked, seriesId === null ? null : Number(seriesId))
  const doImport = useManualImport()

  useEffect(() => setSkipped(new Set()), [plan.data])

  const seriesOptions = useMemo(
    () =>
      (series ?? [])
        .map((s) => ({ value: String(s.id), label: s.displayTitle ?? s.title }))
        .sort((a, b) => a.label.localeCompare(b.label)),
    [series],
  )

  const listing = browse.data
  const open = (next: string | null) => {
    setPath(next)
    setPicked(next || null)
  }

  const imported = (plan.data?.files ?? []).filter(
    (f) => !skipped.has(f.fileName) && (mode === 'Replace' || f.replaces.length === 0 || f.newChapters.length > 0),
  )

  const run = () => {
    if (!plan.data || seriesId === null) return
    doImport.mutate(
      {
        path: plan.data.path,
        seriesId: Number(seriesId),
        mode,
        move,
        skipFiles: [...skipped],
        parsingFingerprint: plan.data.parsingFingerprint,
      },
      {
        onSuccess: (r) => {
          const { imported: count, linked } = r
          notifications.show({
            color: 'var(--ok)',
            message: t`Imported ${count} files, ${linked} linked to chapters`,
          })
          if (listing) setPath(listing.path || null)
        },
      },
    )
  }

  return (
    <Stack gap="xl">
      <DropFolderWaiting
        onOpen={(entry) => {
          if (entry.isFolder) {
            open(entry.path)
          } else {
            setPath(entry.folder)
            setPicked(entry.path)
          }
        }}
      />
      <Panel p="md">
        <Stack gap="sm">
          <SectionHeader icon={IconFolder} title={t`Pick a folder or file`} />
          <Group gap="xs" wrap="nowrap">
            <Button
              size="xs"
              variant="default"
              leftSection={<IconArrowUp size={16} />}
              disabled={!listing || listing.parent === null}
              onClick={() => open(listing?.parent ?? null)}
            >
              <Trans>Up</Trans>
            </Button>
            <Text size="sm" className="modal-mono-text" style={{ overflowWrap: 'anywhere' }}>
              {listing?.path || t`Top`}
            </Text>
            {browse.isFetching && <Loader size="xs" />}
          </Group>
          {browse.isError && (
            <Text size="sm" c="var(--danger)">
              {errorText(browse.error)}
            </Text>
          )}
          <Stack gap={2} className="manual-import-browser" style={{ maxHeight: 320, overflowY: 'auto' }}>
            {listing?.folders.map((f) => (
              <UnstyledButton key={f.path} onClick={() => open(f.path)} className="manual-import-entry">
                <Group gap="xs" wrap="nowrap">
                  <IconFolder size={16} color="var(--brand)" />
                  <Text size="sm">{f.name}</Text>
                </Group>
              </UnstyledButton>
            ))}
            {listing?.files.map((f) => (
              <UnstyledButton
                key={f.path}
                onClick={() => setPicked(f.path)}
                className="manual-import-entry"
                data-active={picked === f.path || undefined}
              >
                <Group gap="xs" wrap="nowrap" justify="space-between">
                  <Group gap="xs" wrap="nowrap">
                    <IconFileZip size={16} color="var(--ink-3)" />
                    <Text size="sm" fw={picked === f.path ? 600 : undefined}>
                      {f.name}
                    </Text>
                  </Group>
                  <Group gap="xs" wrap="nowrap">
                    {fileLabel(f) ? (
                      <Badge size="sm" variant="light">
                        {fileLabel(f)}
                      </Badge>
                    ) : (
                      <Badge size="sm" variant="light" color="var(--warn)">
                        <Trans>unrecognized</Trans>
                      </Badge>
                    )}
                    <Text size="xs" c="var(--ink-3)">
                      {formatBytes(f.size)}
                    </Text>
                  </Group>
                </Group>
              </UnstyledButton>
            ))}
            {listing && listing.folders.length === 0 && listing.files.length === 0 && (
              <Text size="sm" c="var(--ink-3)">
                <Trans>Nothing here.</Trans>
              </Text>
            )}
          </Stack>
          <Select
            searchable
            aria-label={t`Series`}
            placeholder={t`Import into series`}
            data={seriesOptions}
            value={seriesId}
            onChange={setSeriesId}
            nothingFoundMessage={t`No series found`}
          />
        </Stack>
      </Panel>

      {picked === null || seriesId === null ? (
        <EmptyState
          compact
          framed
          mood="asking"
          title={t`Pick a path and a series`}
          description={t`Open a folder, or click a file in it, then choose the series the comics belong to.`}
        />
      ) : plan.isLoading ? (
        <Loader size="sm" />
      ) : plan.isError ? (
        <Text size="sm" c="var(--danger)">
          {errorText(plan.error)}
        </Text>
      ) : plan.data?.error ? (
        <Text size="sm" c="var(--danger)">
          {plan.data.error}
        </Text>
      ) : (
        plan.data && (
          <Panel p="md">
            <Stack gap="sm">
              <SectionHeader icon={IconFileImport} title={plan.data.seriesTitle} count={plan.data.files.length} />
              <Text size="sm" c="var(--ink-2)">
                <Plural
                  value={plan.data.newChapterCount}
                  one="Brings # chapter the series has no file for."
                  other="Brings # chapters the series has no file for."
                />{' '}
                {plan.data.replacedFileCount > 0 && (
                  <Plural
                    value={plan.data.replacedFileCount}
                    one="Replace would take chapters off # existing file."
                    other="Replace would take chapters off # existing files."
                  />
                )}
              </Text>
              <Table.ScrollContainer minWidth={560}>
                <Table className="panel-table">
                  <Table.Thead>
                    <Table.Tr>
                      <Table.Th w={40} />
                      <Table.Th>
                        <Trans>File</Trans>
                      </Table.Th>
                      <Table.Th>
                        <Trans>Chapters</Trans>
                      </Table.Th>
                      <Table.Th>
                        <Trans>Effect</Trans>
                      </Table.Th>
                    </Table.Tr>
                  </Table.Thead>
                  <Table.Tbody>
                    {plan.data.files.map((f) => {
                      const { fileName } = f
                      const newCount = f.newChapters.length
                      const replaceCount = f.replaces.length
                      return (
                        <Table.Tr key={fileName}>
                          <Table.Td>
                            <Checkbox
                              aria-label={t`Import ${fileName}`}
                              checked={!skipped.has(fileName)}
                              onChange={(e) => {
                                const checked = e.currentTarget.checked
                                setSkipped((s) => {
                                  const next = new Set(s)
                                  if (checked) next.delete(fileName)
                                  else next.add(fileName)
                                  return next
                                })
                              }}
                            />
                          </Table.Td>
                          <Table.Td>
                            <Text size="sm">{fileName}</Text>
                            {f.label && (
                              <Text size="xs" c="var(--ink-3)">
                                {f.label}
                              </Text>
                            )}
                          </Table.Td>
                          <Table.Td>
                            <Text size="sm">{f.chapters.join(', ') || '-'}</Text>
                          </Table.Td>
                          <Table.Td>
                            {f.chapters.length === 0 ? (
                              <Badge size="sm" variant="light" color="var(--warn)">
                                <Trans>no matching chapter</Trans>
                              </Badge>
                            ) : (
                              <Group gap={4}>
                                {newCount > 0 && (
                                  <Badge size="sm" variant="light" color="var(--ok)">
                                    <Plural value={newCount} one="# new" other="# new" />
                                  </Badge>
                                )}
                                {replaceCount > 0 && (
                                  <Badge size="sm" variant="light" color="var(--warn)">
                                    <Plural value={replaceCount} one="replaces # file" other="replaces # files" />
                                  </Badge>
                                )}
                              </Group>
                            )}
                          </Table.Td>
                        </Table.Tr>
                      )
                    })}
                  </Table.Tbody>
                </Table>
              </Table.ScrollContainer>
              <Group gap="md" wrap="wrap" align="center">
                {plan.data.hasConflicts && (
                  <SegmentedControl
                    size="xs"
                    value={mode}
                    onChange={(v) => setMode(v as Mode)}
                    data={[
                      { value: 'SkipExisting', label: t`Only missing chapters` },
                      { value: 'Replace', label: t`Replace existing files` },
                    ]}
                  />
                )}
                <Checkbox
                  label={t`Move the files instead of copying them`}
                  checked={move}
                  onChange={(e) => setMove(e.currentTarget.checked)}
                />
                <Button
                  ml="auto"
                  color="var(--ok)"
                  vars={okButtonVars}
                  leftSection={<IconFileImport size={16} />}
                  loading={doImport.isPending}
                  disabled={imported.length === 0}
                  onClick={run}
                >
                  <Plural value={imported.length} one="Import # file" other="Import # files" />
                </Button>
              </Group>
              {move && (
                <Text size="xs" c="var(--ink-3)">
                  <Trans>
                    Moved files leave the source folder. Archives that have to be converted are built in the series
                    folder and their originals stay where they are.
                  </Trans>
                </Text>
              )}
            </Stack>
          </Panel>
        )
      )}
    </Stack>
  )
}
