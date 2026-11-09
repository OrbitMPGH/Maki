import { useState } from 'react'
import { ActionIcon, Badge, Button, Checkbox, Group, Paper, Skeleton, Stack, Table, Text, Title, Tooltip, VisuallyHidden } from '@mantine/core'
import {
  IconFileTypePdf,
  IconFileUnknown,
  IconFileZip,
  IconLink,
  IconLock,
  IconLockOpen,
  IconRefresh,
  IconWand,
  IconListCheck,
  IconTrash,
  IconX,
} from '../lib/icons'
import { notifications } from '../lib/toast'
import { useSeriesFiles, useDeleteSeriesFiles } from '../api/hooks'
import { offersPermanentDelete, useRecycleBinDays } from '../api/recycleBin'
import { PermanentDeleteFallback } from './PermanentDeleteFallback'
import { useSetFileTrusted } from '../api/upgrades'
import type { SeriesFileDto } from '../api/types'
import { formatBytes, serverChapterLabel } from '../format'
import { FileQualityBadge } from './series/FileQualityBadge'
import { Trans, Plural, useLingui } from '@lingui/react/macro'
import { plural, t as now } from '@lingui/core/macro'
import { useLabel } from '../i18n-context'
import { isPdfFile } from '../lib/files'
import { fileStatusVisual, statusToken } from './ui/status'
import { StatusDot } from './ui/StatusDot'
import { MonoValue, sharedPrefixLength, splitForTruncation } from './ui/MonoValue'
import { Panel } from './ui/Panel'
import { useAuth } from '../auth/AuthProvider'
import { LinkFileToChaptersModal } from './LinkFileToChaptersModal'
import { RelinkFilesModal } from './RelinkFilesModal'
import { MetaList } from './ui/MetaList'
import { ConfirmDialog } from './ui/ConfirmDialog'

/** ["1", ..., "8", "8.5", "10"] → "1–8, 8.5, 10": whole-number runs collapse, so a long list never cuts mid-number. */
export function chapterRuns(numbers: string[]): string {
  const parts: string[] = []
  let start: string | null = null
  let prev: string | null = null
  const close = () => {
    if (start === null || prev === null) return
    parts.push(start === prev ? start : `${start}–${prev}`)
  }
  for (const n of numbers) {
    const value = Number(n)
    const follows = prev !== null && Number.isInteger(value) && Number.isInteger(Number(prev)) && value === Number(prev) + 1
    if (!follows) {
      close()
      start = n
    }
    prev = n
  }
  close()
  return parts.join(', ')
}

/** "21" → "Ch. 21"; ["21","22","23","25"] → "Ch. 21–23, 25". */
function mappedLabel(file: SeriesFileDto): string {
  const { mappedChapters } = file
  if (mappedChapters.length === 0) {
    // A linked volume with no chapter numbers backs volume entries, which carry its own label.
    if (file.status !== 'linked') return '-'
    return file.isVolume && file.parsedLabel !== null ? serverChapterLabel(file.parsedLabel) : now`One-shot`
  }
  const chapters = chapterRuns(mappedChapters)
  return now`Ch. ${chapters}`
}

const hasLinks = (file: SeriesFileDto) => file.mappedChapters.length > 0 || file.status === 'linked'
const MAPPED_TOOLTIP_EVENTS = { hover: true, focus: true, touch: true }
// The fixed columns take 752px; this leaves the file name about 200px, so a phone sees it and the
// parsed label before the table scrolls.
const FILE_TABLE_MIN_WIDTH = 950

export function SeriesFilesSection({ seriesId }: { seriesId: number }) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const { can } = useAuth()
  const canLink = can('EditMetadata')
  const canDownload = can('DownloadChapters')
  const [linkFile, setLinkFile] = useState<SeriesFileDto | null>(null)
  const [relinkOpen, setRelinkOpen] = useState(false)
  const [selectMode, setSelectMode] = useState(false)
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [confirmOpen, setConfirmOpen] = useState(false)
  const { data: files, isLoading, isFetching, refetch } = useSeriesFiles(seriesId)
  const deleteFiles = useDeleteSeriesFiles(seriesId)
  const binDays = useRecycleBinDays(confirmOpen)
  const [binRefused, setBinRefused] = useState(false)
  const closeConfirm = () => {
    setConfirmOpen(false)
    setBinRefused(false)
  }

  const deleteSelected = (permanent: boolean) =>
    deleteFiles.mutate(
      { relativePaths: [...selected], permanent },
      {
        onSuccess: (r) => {
          const done = permanent
            ? plural(r.deleted, { one: 'Deleted # file', other: 'Deleted # files' })
            : plural(r.deleted, { one: 'Moved # file to the recycle bin', other: 'Moved # files to the recycle bin' })
          notifications.show({
            color: r.failed > 0 ? 'var(--warn)' : 'var(--ok)',
            message:
              r.failed > 0
                ? `${done}, ${plural(r.failed, {
                    one: '# could not be moved (locked or permission denied)',
                    other: '# could not be moved (locked or permission denied)',
                  })}`
                : done,
          })
          closeConfirm()
          exitSelectMode()
        },
        onError: (error) => {
          if (offersPermanentDelete(error)) setBinRefused(true)
        },
      },
    )
  const setFileTrusted = useSetFileTrusted()

  const problems = files?.filter((f) => f.status !== 'linked').length ?? 0
  const unlinkedOnDisk = files?.filter((f) => f.onDisk && f.status !== 'linked').length ?? 0
  const missingCount = files?.filter((f) => f.status === 'missing').length ?? 0
  const fileCount = files?.length ?? 0
  const sharedPrefix = sharedPrefixLength((files ?? []).map((f) => f.fileName))
  const fileNameTailCh = Math.max(
    0,
    ...(files ?? []).map((f) =>
      sharedPrefix ? f.fileName.length - sharedPrefix : splitForTruncation(f.fileName)[1].length,
    ),
  )
  // When most of the folder is gone it is one problem, not twenty: one banner says so.
  const mostlyMissing = missingCount >= 2 && missingCount >= fileCount * 0.8

  const exitSelectMode = () => {
    setSelectMode(false)
    setSelected(new Set())
  }

  const toggleSelected = (path: string) =>
    setSelected((s) => {
      const next = new Set(s)
      if (next.has(path)) next.delete(path)
      else next.add(path)
      return next
    })

  const tableHead = (
    <Table.Thead>
      <Table.Tr>
        {selectMode && <Table.Th w={40}><VisuallyHidden><Trans>Select</Trans></VisuallyHidden></Table.Th>}
        <Table.Th><Trans>File</Trans></Table.Th>
        <Table.Th w={100}><Trans>Parsed</Trans></Table.Th>
        <Table.Th w={156}><Trans>Status</Trans></Table.Th>
        <Table.Th w={152}><Trans>Mapped to</Trans></Table.Th>
        <Table.Th w={84}><Trans>Size</Trans></Table.Th>
        <Table.Th w={148}><Trans>Quality</Trans></Table.Th>
        {!selectMode && <Table.Th w={52 + (canLink ? 34 : 0) + (canDownload ? 34 : 0)}><VisuallyHidden><Trans>Actions</Trans></VisuallyHidden></Table.Th>}
      </Table.Tr>
    </Table.Thead>
  )

  return (
    <div>
      <Group justify="space-between" wrap="wrap" gap="sm">
        <Group gap="xs" align="baseline">
          <Title order={2} size="h3">
            <Trans>Files</Trans>
          </Title>
          {files && (
            <Text component="div" size="sm" c="var(--ink-3)" className="tnum">
              <MetaList
                items={[
                  files.length,
                  problems > 0 && <Plural key="problems" value={problems} one="# needs attention" other="# need attention" />,
                ]}
              />
            </Text>
          )}
        </Group>
        <Group gap="xs">
            <Button
              size="xs"
              variant="default"
              leftSection={<IconRefresh size={16} />}
              loading={isFetching && !isLoading}
              onClick={() => void refetch()}
            >
              <Trans>Refresh</Trans>
            </Button>
            {/* While the missing-files banner shows, its button is the one place this lives. */}
            {files && files.length > 0 && !selectMode && canLink && !mostlyMissing && (
              <Tooltip label={t`Rebuild chapter links from the folder, volumes first`} withArrow>
                <Button
                  size="xs"
                  variant="default"
                  leftSection={<IconWand size={16} />}
                  onClick={() => setRelinkOpen(true)}
                >
                  <Trans>Relink files</Trans>
                </Button>
              </Tooltip>
            )}
            {files && files.length > 0 && !selectMode && (
              <Button
                size="xs"
                variant="default"
                leftSection={<IconListCheck size={16} />}
                onClick={() => setSelectMode(true)}
              >
                <Trans>Select</Trans>
              </Button>
            )}
          </Group>
      </Group>

      {(isLoading ? (
          <Panel p={0} className="table-panel" mt="sm">
            <VisuallyHidden role="status">
              <Trans>Scanning folder…</Trans>
            </VisuallyHidden>
            <Table.ScrollContainer minWidth={FILE_TABLE_MIN_WIDTH}>
              <Table className="panel-table ops-table" layout="fixed" aria-busy>
                {tableHead}
                <Table.Tbody>
                  <FileRowSkeletons />
                </Table.Tbody>
              </Table>
            </Table.ScrollContainer>
          </Panel>
        ) : !files || files.length === 0 ? (
          <Text c="var(--ink-3)" size="sm" py="sm">
            <Trans>No files in the series folder.</Trans>
          </Text>
        ) : (
        <>
          {mostlyMissing && !selectMode && (
            <Paper className="series-files-hint" withBorder p="xs" radius="lg" mt="sm" role="status">
              <Group gap="sm" wrap="wrap" justify="space-between">
                <Group gap="xs" wrap="nowrap" align="flex-start" style={{ flex: '1 1 20rem', minWidth: 0 }}>
                  <IconFileUnknown size={16} style={{ flexShrink: 0, marginTop: 2, color: 'var(--warn)' }} />
                  <Text size="sm" c="var(--ink-2)">
                    {missingCount === fileCount ? (
                      <Plural
                        value={missingCount}
                        one="The file listed for this series is missing from disk."
                        other="All # files listed for this series are missing from disk."
                      />
                    ) : (
                      <Trans>{missingCount} of {fileCount} files listed for this series are missing from disk.</Trans>
                    )}{' '}
                    {canLink && (
                      <Trans>If they are in the folder under other names, Relink files finds them.</Trans>
                    )}
                  </Text>
                </Group>
                {canLink && (
                  <Button
                    size="xs"
                    variant="default"
                    leftSection={<IconWand size={16} />}
                    onClick={() => setRelinkOpen(true)}
                  >
                    <Trans>Relink files</Trans>
                  </Button>
                )}
              </Group>
            </Paper>
          )}
          {unlinkedOnDisk > 0 && canLink && !selectMode && (
            <Paper className="series-files-hint" withBorder p="xs" radius="lg" mt="sm">
              <Group gap="xs" wrap="nowrap" align="flex-start">
                <IconLink size={16} style={{ flexShrink: 0, marginTop: 2, color: 'var(--warn)' }} />
                <Text size="sm" c="var(--ink-2)">
                  <Plural
                    value={unlinkedOnDisk}
                    one="# file in this folder isn't linked to any chapter, so it can't be read yet."
                    other="# files in this folder aren't linked to any chapter, so they can't be read yet."
                  />{' '}
                  <Trans>
                    That happens with omnibus volumes and names the matcher can't parse. Try Relink files
                    first, which reads each volume to find the chapters inside it; use the link button on
                    a row for anything it can't place.
                  </Trans>
                </Text>
              </Group>
            </Paper>
          )}
          {selectMode && (
            <Paper bg="var(--surface-sunken)" px="sm" py="xs" mt="sm" style={{ borderRadius: 'var(--radius-control)' }}>
              <Group gap="xs" justify="space-between">
                <Group gap="xs">
                  <Text size="sm" c="var(--ink-3)">
                    <Plural value={selected.size} one="# selected" other="# selected" />
                  </Text>
                  <Button
                    size="xs"
                    variant="subtle"
                    onClick={() =>
                      setSelected(new Set(files.filter((f) => f.onDisk).map((f) => f.relativePath)))
                    }
                  >
                    <Trans>Select all on disk</Trans>
                  </Button>
                </Group>
                <Group gap="xs">
                  <Button
                    size="xs"
                    variant="light"
                    color="var(--danger)"
                    leftSection={<IconTrash size={16} />}
                    disabled={selected.size === 0}
                    onClick={() => setConfirmOpen(true)}
                  >
                    <Trans>Delete selected</Trans>
                  </Button>
                  <Button
                    size="xs"
                    variant="default"
                    leftSection={<IconX size={16} />}
                    onClick={exitSelectMode}
                  >
                    <Trans>Done</Trans>
                  </Button>
                </Group>
              </Group>
            </Paper>
          )}

          <Panel p={0} className="table-panel series-files-panel" mt="sm">
            <Table.ScrollContainer minWidth={FILE_TABLE_MIN_WIDTH}>
              {/* Fixed, so the file name takes what the other columns leave and loses its middle
                  rather than pushing them into wrapping. */}
              <Table className="panel-table ops-table series-files-table" layout="fixed" highlightOnHover>
                {tableHead}
                <Table.Tbody>
                  {files.map((f) => {
                    const v = fileStatusVisual(f.status)
                    const { fileName } = f
                    return (
                      <Table.Tr key={f.relativePath} data-muted={f.status === 'missing' || undefined}>
                        {selectMode && (
                          <Table.Td className="files-cell-select">
                            <Checkbox
                              checked={selected.has(f.relativePath)}
                              onChange={() => toggleSelected(f.relativePath)}
                              disabled={!f.onDisk}
                              aria-label={t`Select ${fileName}`}
                            />
                          </Table.Td>
                        )}
                        <Table.Td className="files-cell-name">
                          <Group gap={6} wrap="nowrap">
                            {isPdfFile(f.fileName) ? (
                              <IconFileTypePdf size={16} style={{ flexShrink: 0 }} />
                            ) : (
                              <IconFileZip size={16} style={{ flexShrink: 0 }} />
                            )}
                            <MonoValue
                              value={f.fileName}
                              full={f.relativePath}
                              splitAt={sharedPrefix || undefined}
                              tailCh={fileNameTailCh}
                              copy
                            />
                          </Group>
                        </Table.Td>
                        <Table.Td className="files-cell-parsed">
                          {f.parsedLabel ? (
                            <Badge
                              size="sm"
                              variant="light"
                              color={f.isVolume ? 'var(--volume)' : 'var(--neutral)'}
                              className="tnum files-parsed-pill"
                            >
                              {serverChapterLabel(f.parsedLabel)}
                            </Badge>
                          ) : (
                            <Text size="sm" c="var(--ink-3)">
                              -
                            </Text>
                          )}
                        </Table.Td>
                        <Table.Td className="files-cell-status">
                          <StatusDot tone={statusToken(v.color)}>
                            {renderLabel(v.label)}
                          </StatusDot>
                        </Table.Td>
                        <Table.Td className="files-cell-mapped">
                          {f.isVolume && f.mappedChapters.length > 0 ? (
                            <Tooltip
                              label={
                                <>
                                  {plural(f.mappedChapters.length, {
                                    one: 'Volume file backing # chapter',
                                    other: 'Volume file backing # chapters',
                                  })}
                                  <br />
                                  {mappedLabel(f)}
                                </>
                              }
                              withArrow
                              multiline
                              maw={360}
                              events={MAPPED_TOOLTIP_EVENTS}
                            >
                              <Text size="sm" className="tnum mapped-to-label" truncate="end" tabIndex={0}>
                                {mappedLabel(f)}
                              </Text>
                            </Tooltip>
                          ) : !hasLinks(f) && f.onDisk && canLink && !selectMode ? (
                            <Button
                              size="compact-xs"
                              variant="light"
                              leftSection={<IconLink size={13} />}
                              onClick={() => setLinkFile(f)}
                            >
                              <Trans>Link chapters</Trans>
                            </Button>
                          ) : hasLinks(f) ? (
                            <Tooltip label={mappedLabel(f)} withArrow multiline maw={360} events={MAPPED_TOOLTIP_EVENTS}>
                              <Text size="sm" className="tnum mapped-to-label" truncate="end" tabIndex={0}>
                                {mappedLabel(f)}
                              </Text>
                            </Tooltip>
                          ) : (
                            <Text size="sm" c="var(--ink-3)" className="tnum" truncate="end">
                              {mappedLabel(f)}
                            </Text>
                          )}
                        </Table.Td>
                        <Table.Td className="files-cell-size">
                          <Text size="sm" c="var(--ink-3)" className="tnum" style={{ whiteSpace: 'nowrap' }}>
                            {formatBytes(f.size)}
                          </Text>
                        </Table.Td>
                        <Table.Td className="files-cell-quality">
                          <FileQualityBadge quality={f.quality} />
                        </Table.Td>
                        {!selectMode && (
                          <Table.Td className="files-cell-actions">
                            <Group gap={2} wrap="nowrap" justify="flex-end">
                            {canLink && (
                              <Tooltip
                                label={hasLinks(f) ? t`Change linked chapters` : t`Link chapters to this file`}
                                withArrow
                              >
                                <ActionIcon
                                  variant="subtle"
                                  color={hasLinks(f) ? 'gray' : 'brand'}
                                  data-row-action={hasLinks(f) ? '' : undefined}
                                  disabled={!f.onDisk}
                                  onClick={() => setLinkFile(f)}
                                  aria-label={t`Link chapters to ${fileName}`}
                                >
                                  <IconLink size={19} />
                                </ActionIcon>
                              </Tooltip>
                            )}
                            {canDownload && f.quality && (
                              <Tooltip
                                label={f.quality.trusted ? t`Allow upgrades` : t`Protect from upgrades`}
                                withArrow
                              >
                                <ActionIcon
                                  variant="subtle"
                                  color="var(--neutral)"
                                  data-row-action=""
                                  onClick={() =>
                                    setFileTrusted.mutate({
                                      fileId: f.quality!.fileId,
                                      trusted: !f.quality!.trusted,
                                    })
                                  }
                                  aria-label={
                                    f.quality.trusted
                                      ? t`Allow upgrades to ${fileName}`
                                      : t`Protect ${fileName} from upgrades`
                                  }
                                >
                                  {f.quality.trusted ? <IconLockOpen size={19} /> : <IconLock size={19} />}
                                </ActionIcon>
                              </Tooltip>
                            )}
                            <Tooltip label={f.onDisk ? t`Delete from disk` : t`File missing`} withArrow>
                              <ActionIcon
                                variant="subtle"
                                color="var(--danger)"
                                data-row-action="danger"
                                disabled={!f.onDisk}
                                onClick={() => {
                                  setSelected(new Set([f.relativePath]))
                                  setSelectMode(true)
                                  setConfirmOpen(true)
                                }}
                                aria-label={t`Delete ${fileName}`}
                              >
                                <IconTrash size={19} />
                              </ActionIcon>
                            </Tooltip>
                            </Group>
                          </Table.Td>
                        )}
                      </Table.Tr>
                    )
                  })}
                </Table.Tbody>
              </Table>
            </Table.ScrollContainer>
          </Panel>

          <ConfirmDialog
            opened={confirmOpen}
            onClose={closeConfirm}
            title={t`Delete files?`}
            confirmLabel={<Trans>Delete files</Trans>}
            onConfirm={() => deleteSelected(false)}
            loading={deleteFiles.isPending}
            confirmDisabled={binRefused}
          >
            <Stack gap="md">
              <Text size="sm" c="var(--ink-3)">
                <Plural
                  value={selected.size}
                  one="This moves # file to the recycle bin."
                  other="This moves # files to the recycle bin."
                />{' '}
                <Trans>Chapters that share a volume archive will also lose their file.</Trans>
              </Text>
              <Text size="sm" c="var(--ink-3)">
                <Plural
                  value={binDays}
                  one="Files can be restored from Settings for # day, then they are deleted for good."
                  other="Files can be restored from Settings for # days, then they are deleted for good."
                />
              </Text>
              {binRefused && (
                <PermanentDeleteFallback loading={deleteFiles.isPending} onConfirm={() => deleteSelected(true)} />
              )}
            </Stack>
          </ConfirmDialog>
        </>
      ))}

      <LinkFileToChaptersModal seriesId={seriesId} file={linkFile} onClose={() => setLinkFile(null)} />
      <RelinkFilesModal seriesId={seriesId} opened={relinkOpen} onClose={() => setRelinkOpen(false)} />
    </div>
  )
}

const SKELETON_NAME_WIDTHS = ['72%', '58%', '66%', '50%', '62%', '54%']

/** Stand-in rows while the folder is scanned, one bar per real cell so the columns keep their widths. */
function FileRowSkeletons() {
  return SKELETON_NAME_WIDTHS.map((nameWidth, i) => (
    <Table.Tr key={i} aria-hidden>
      <Table.Td>
        <Group gap={6} wrap="nowrap">
          <Skeleton h={15} w={15} radius="xs" />
          <Skeleton h={14} w={nameWidth} />
        </Group>
      </Table.Td>
      <Table.Td><Skeleton h={18} w={44} radius="sm" /></Table.Td>
      <Table.Td><Skeleton h={18} w={78} radius="sm" /></Table.Td>
      <Table.Td><Skeleton h={14} w={64} /></Table.Td>
      <Table.Td><Skeleton h={14} w={52} /></Table.Td>
      <Table.Td><Skeleton h={18} w={110} radius="sm" /></Table.Td>
      <Table.Td><Skeleton h={18} w={56} ml="auto" /></Table.Td>
    </Table.Tr>
  ))
}
