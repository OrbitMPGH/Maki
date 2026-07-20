import { type ReactNode, useMemo, useState } from 'react'
import {
  Button,
  Center,
  Checkbox,
  Group,
  Loader,
  Modal,
  Paper,
  Radio,
  SegmentedControl,
  Select,
  SimpleGrid,
  Stack,
  Text,
  TextInput,
} from '@mantine/core'
import {
  IconCircleCheck,
  IconClock,
  IconDownload,
  IconEye,
  IconFileText,
  IconFolderSymlink,
  IconLibrary,
  IconListCheck,
  IconPhoto,
  IconPlus,
  IconRefresh,
  IconSearch,
  IconTrash,
  IconX,
} from '@tabler/icons-react'
import { notifications } from '@mantine/notifications'
import { useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { api } from '../api/client'
import { useConnectionSettings, useRootFolders, useSeries } from '../api/hooks'
import { CoverCard } from '../components/ui/CoverCard'
import { EmptyState } from '../components/ui/EmptyState'
import { PageHeader } from '../components/ui/PageHeader'
import { StatTile } from '../components/ui/StatTile'

const SORTS = [
  { value: 'added', label: 'Recently added' },
  { value: 'title', label: 'Title A–Z' },
  { value: 'incomplete', label: 'Most missing' },
  { value: 'status', label: 'Status' },
]

export default function LibraryPage() {
  const { data: series, isLoading, error } = useSeries()
  const { data: rootFolders } = useRootFolders()
  // Read progress only ever gets reported through Kavita, so cards shouldn't show a read ring
  // (even a stale one, from a Kavita connection that's since been removed) when it's unconfigured.
  const { data: kavitaSettings } = useConnectionSettings<{ url: string | null; apiKey: string | null }>('kavita')
  const kavitaConfigured = Boolean(kavitaSettings?.url && kavitaSettings?.apiKey)
  const queryClient = useQueryClient()

  const [query, setQuery] = useState('')
  const [sort, setSort] = useState('added')
  const [statusFilter, setStatusFilter] = useState('all')

  const [selectMode, setSelectMode] = useState(false)
  const [selected, setSelected] = useState<Set<number>>(new Set())
  const [busy, setBusy] = useState<string | null>(null)
  const [deleteModalOpen, setDeleteModalOpen] = useState(false)
  const [deleteFiles, setDeleteFiles] = useState(false)
  const [monitorModalOpen, setMonitorModalOpen] = useState(false)
  const [monitorMode, setMonitorMode] = useState('All')
  const [moveModalOpen, setMoveModalOpen] = useState(false)
  const [moveTarget, setMoveTarget] = useState<string | null>(null)
  const [moveFiles, setMoveFiles] = useState(true)

  const stats = useMemo(() => {
    const list = series ?? []
    let downloaded = 0
    let missing = 0
    let monitored = 0
    let inQueue = 0
    for (const s of list) {
      downloaded += s.chapterFileCount
      if (s.chapterCount > s.chapterFileCount) missing += s.chapterCount - s.chapterFileCount
      if (s.monitored) monitored++
      inQueue += s.queuedCount + s.downloadingCount
    }
    return { total: list.length, monitored, downloaded, missing, inQueue }
  }, [series])

  const visible = useMemo(() => {
    let list = [...(series ?? [])]
    const q = query.trim().toLowerCase()
    if (q) {
      list = list.filter(
        (s) =>
          s.title.toLowerCase().includes(q) ||
          (s.originalTitle?.toLowerCase().includes(q) ?? false),
      )
    }
    if (statusFilter !== 'all') list = list.filter((s) => s.status === statusFilter)
    list.sort((a, b) => {
      switch (sort) {
        case 'title':
          return a.sortTitle.localeCompare(b.sortTitle)
        case 'incomplete':
          return (
            (b.chapterCount - b.chapterFileCount) - (a.chapterCount - a.chapterFileCount)
          )
        case 'status':
          return a.status.localeCompare(b.status)
        default:
          return new Date(b.added).getTime() - new Date(a.added).getTime()
      }
    })
    return list
  }, [series, query, statusFilter, sort])

  const statusOptions = useMemo(() => {
    const set = new Set((series ?? []).map((s) => s.status))
    return ['all', ...[...set].sort()]
  }, [series])

  const toggle = (id: number) =>
    setSelected((s) => {
      const next = new Set(s)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })

  const exitSelectMode = () => {
    setSelectMode(false)
    setSelected(new Set())
  }

  /** Runs an action against every selected series sequentially with a live progress notification. */
  const runBulk = async (action: string, fn: (id: number) => Promise<unknown>) => {
    const ids = [...selected]
    setBusy(action)
    notifications.show({
      id: 'bulk-action',
      loading: true,
      message: `${action}: 0/${ids.length}`,
      autoClose: false,
      withCloseButton: false,
    })
    let ok = 0
    const errors: string[] = []
    for (const id of ids) {
      try {
        await fn(id)
        ok++
      } catch (err) {
        errors.push(String(err))
      }
      notifications.update({
        id: 'bulk-action',
        loading: true,
        message: `${action}: ${ok + errors.length}/${ids.length}`,
        autoClose: false,
        withCloseButton: false,
      })
    }
    notifications.update({
      id: 'bulk-action',
      loading: false,
      color: errors.length ? 'yellow' : 'green',
      message: `${action}: ${ok}/${ids.length} succeeded${errors.length ? ` — first error: ${errors[0]}` : ''}`,
      autoClose: 8000,
      withCloseButton: true,
    })
    setBusy(null)
    void queryClient.invalidateQueries({ queryKey: ['series'] })
    void queryClient.invalidateQueries({ queryKey: ['chapters'] })
  }

  const bulkBtn = (label: string, icon: ReactNode, run: () => void, color?: string) => (
    <Button
      size="xs"
      variant="light"
      color={color}
      leftSection={icon}
      disabled={selected.size === 0 || (busy !== null && busy !== label)}
      loading={busy === label}
      onClick={run}
    >
      {label}
    </Button>
  )

  const allSelected = selected.size > 0 && selected.size === series?.length

  return (
    <>
      <PageHeader
        title="Library"
        description="Every series Maki watches — cover art, download progress and status at a glance."
        actions={
          series && series.length > 0 && !selectMode ? (
            <>
              <Button
                variant="default"
                leftSection={<IconListCheck size={16} />}
                onClick={() => setSelectMode(true)}
              >
                Select
              </Button>
              <Button component={Link} to="/add" leftSection={<IconPlus size={16} />}>
                Add series
              </Button>
            </>
          ) : undefined
        }
      />

      {series && series.length > 0 && (
        <SimpleGrid cols={{ base: 2, sm: stats.inQueue > 0 ? 5 : 4 }} spacing="sm" mb="lg">
          <StatTile label="Series" value={stats.total} icon={IconLibrary} accent="brand" />
          <StatTile label="Monitored" value={stats.monitored} icon={IconEye} accent="info" />
          <StatTile label="On disk" value={stats.downloaded} icon={IconCircleCheck} accent="ok" />
          <StatTile label="Missing" value={stats.missing} icon={IconDownload} accent="warn" />
          {stats.inQueue > 0 && (
            <StatTile label="In queue" value={stats.inQueue} icon={IconClock} accent="brand" />
          )}
        </SimpleGrid>
      )}

      {/* Toolbar / selection bar */}
      {series && series.length > 0 &&
        (selectMode ? (
          <Paper withBorder p="xs" mb="lg" radius="lg">
            <Group justify="space-between" wrap="wrap" gap="xs">
              <Group gap="xs">
                <Text size="sm" c="dimmed" className="tnum">
                  {selected.size} selected
                </Text>
                <Button
                  size="xs"
                  variant="subtle"
                  onClick={() =>
                    setSelected(allSelected ? new Set() : new Set(series.map((s) => s.id)))
                  }
                >
                  {allSelected ? 'Clear all' : 'Select all'}
                </Button>
              </Group>
              <Group gap="xs">
                {bulkBtn('Search missing', <IconSearch size={15} />, () =>
                  runBulk('Search missing', (id) =>
                    api(`/series/${id}/searchmissing`, { method: 'POST' }),
                  ),
                )}
                {bulkBtn('Refresh', <IconRefresh size={15} />, () =>
                  runBulk('Refresh', (id) => api(`/series/${id}/refresh`, { method: 'POST' })),
                )}
                {bulkBtn('Metadata', <IconPhoto size={15} />, () =>
                  runBulk('Metadata', (id) =>
                    api(`/series/${id}/refreshmetadata`, { method: 'POST' }),
                  ),
                )}
                {bulkBtn('ComicInfo', <IconFileText size={15} />, () =>
                  runBulk('ComicInfo', (id) =>
                    api(`/series/${id}/updatecomicinfo`, { method: 'POST' }),
                  ),
                )}
                {bulkBtn('Monitoring', <IconEye size={15} />, () => setMonitorModalOpen(true))}
                {bulkBtn('Move', <IconFolderSymlink size={15} />, () => {
                  setMoveTarget(null)
                  setMoveFiles(true)
                  setMoveModalOpen(true)
                })}
                {bulkBtn('Delete', <IconTrash size={15} />, () => setDeleteModalOpen(true), 'red')}
                <Button
                  size="xs"
                  variant="default"
                  leftSection={<IconX size={15} />}
                  disabled={busy !== null}
                  onClick={exitSelectMode}
                >
                  Done
                </Button>
              </Group>
            </Group>
          </Paper>
        ) : (
          <Group mb="lg" gap="sm" wrap="wrap">
            <TextInput
              placeholder="Filter library…"
              leftSection={<IconSearch size={16} />}
              value={query}
              onChange={(e) => setQuery(e.currentTarget.value)}
              style={{ flex: '1 1 240px' }}
            />
            <Select
              data={statusOptions.map((s) => ({ value: s, label: s === 'all' ? 'All statuses' : s }))}
              value={statusFilter}
              onChange={(v) => setStatusFilter(v ?? 'all')}
              w={160}
              comboboxProps={{ withinPortal: true }}
            />
            <Select
              data={SORTS}
              value={sort}
              onChange={(v) => setSort(v ?? 'added')}
              w={170}
              comboboxProps={{ withinPortal: true }}
            />
          </Group>
        ))}

      <Modal
        opened={deleteModalOpen}
        onClose={() => setDeleteModalOpen(false)}
        title={`Delete ${selected.size} series?`}
      >
        <Text size="sm" mb="md">
          The selected series will be removed from Maki and stop being monitored.
        </Text>
        <Checkbox
          label="Also delete the folders and files on disk"
          checked={deleteFiles}
          onChange={(e) => setDeleteFiles(e.currentTarget.checked)}
          mb="lg"
        />
        <Group justify="flex-end">
          <Button variant="default" onClick={() => setDeleteModalOpen(false)}>
            Cancel
          </Button>
          <Button
            color="red"
            leftSection={<IconTrash size={16} />}
            onClick={() => {
              setDeleteModalOpen(false)
              void runBulk('Delete', (id) =>
                api(`/series/${id}?deleteFiles=${deleteFiles}`, { method: 'DELETE' }),
              ).then(exitSelectMode)
            }}
          >
            Delete
          </Button>
        </Group>
      </Modal>

      <Modal
        opened={monitorModalOpen}
        onClose={() => setMonitorModalOpen(false)}
        title={`Set monitoring for ${selected.size} series`}
      >
        <Text size="sm" mb="md">
          Applies to every existing chapter and to chapters released later. "Main" skips specials
          (decimal chapters like 10.5).
        </Text>
        <SegmentedControl
          fullWidth
          value={monitorMode}
          onChange={setMonitorMode}
          data={[
            { value: 'All', label: 'All chapters' },
            { value: 'MainOnly', label: 'Main (no specials)' },
            { value: 'None', label: 'None' },
          ]}
          mb="lg"
        />
        <Group justify="flex-end">
          <Button variant="default" onClick={() => setMonitorModalOpen(false)}>
            Cancel
          </Button>
          <Button
            onClick={() => {
              setMonitorModalOpen(false)
              void runBulk('Set monitoring', (id) =>
                api(`/series/${id}/monitormode`, {
                  method: 'POST',
                  body: JSON.stringify({ mode: monitorMode }),
                }),
              )
            }}
          >
            Apply
          </Button>
        </Group>
      </Modal>

      <Modal
        opened={moveModalOpen}
        onClose={() => setMoveModalOpen(false)}
        title={`Move ${selected.size} series`}
      >
        <Stack gap="md">
          <Text size="sm" c="dimmed">
            Re-triggers a Kavita scan of both locations either way. Series already in the
            destination root folder are skipped. A file move is blocked for any series with an
            active download.
          </Text>
          <Select
            label="Destination root folder"
            placeholder="Pick a root folder"
            data={(rootFolders ?? []).map((f) => ({ value: String(f.id), label: f.path }))}
            value={moveTarget}
            onChange={setMoveTarget}
            comboboxProps={{ withinPortal: true }}
          />
          <Radio.Group
            label="Files"
            value={moveFiles ? 'move' : 'already-moved'}
            onChange={(v) => setMoveFiles(v === 'move')}
          >
            <Stack gap={6} mt={6}>
              <Radio value="move" label="Move the files on disk to the new root folder" />
              <Radio
                value="already-moved"
                label="Just point the series at the new root folder — I already moved the files"
              />
            </Stack>
          </Radio.Group>
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setMoveModalOpen(false)}>
              Cancel
            </Button>
            <Button
              disabled={!moveTarget}
              onClick={() => {
                if (!moveTarget) return
                setMoveModalOpen(false)
                void runBulk('Move', (id) =>
                  api(`/series/${id}/move`, {
                    method: 'POST',
                    body: JSON.stringify({ rootFolderId: Number(moveTarget), moveFiles }),
                  }),
                )
              }}
            >
              Move
            </Button>
          </Group>
        </Stack>
      </Modal>

      {isLoading && (
        <Center py={80}>
          <Loader />
        </Center>
      )}
      {error && (
        <Text c="red" ta="center" py="xl">
          Failed to load library: {String(error)}
        </Text>
      )}
      {series && series.length === 0 && (
        <EmptyState
          icon={IconLibrary}
          title="Your library is empty"
          description="Search MangaBaka and add your first series — Maki will monitor for new chapters and download them automatically."
          actionLabel="Add a series"
          actionTo="/add"
        />
      )}
      {series && series.length > 0 && visible.length === 0 && (
        <EmptyState
          icon={IconSearch}
          title="No matches"
          description="No series match the current filter. Try clearing the search or status filter."
        />
      )}
      {visible.length > 0 && (
        <SimpleGrid cols={{ base: 2, xs: 3, sm: 4, md: 5, lg: 6, xl: 8 }} spacing="md">
          {visible.map((s) => (
            <CoverCard
              key={s.id}
              series={s}
              selectMode={selectMode}
              selected={selected.has(s.id)}
              kavitaConfigured={kavitaConfigured}
              onToggle={() => toggle(s.id)}
            />
          ))}
        </SimpleGrid>
      )}
    </>
  )
}
