import { useState } from 'react'
import {
  AspectRatio,
  Badge,
  Button,
  Card,
  Center,
  Checkbox,
  Group,
  Image,
  Loader,
  Modal,
  SegmentedControl,
  SimpleGrid,
  Text,
  Title,
} from '@mantine/core'
import { notifications } from '@mantine/notifications'
import { useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { api } from '../api/client'
import { useSeries } from '../api/hooks'
import type { SeriesDto } from '../api/types'

const statusColor: Record<string, string> = {
  Ongoing: 'blue',
  Completed: 'green',
  Hiatus: 'yellow',
  Cancelled: 'red',
  Unknown: 'gray',
}

function SeriesCard({
  series,
  selectMode,
  selected,
  onToggle,
}: {
  series: SeriesDto
  selectMode: boolean
  selected: boolean
  onToggle: () => void
}) {
  return (
    <Card
      component={Link}
      to={`/series/${series.id}`}
      onClick={(e) => {
        if (selectMode) {
          e.preventDefault()
          onToggle()
        }
      }}
      shadow="sm"
      padding="xs"
      radius="md"
      withBorder
      style={{
        position: 'relative',
        outline: selected ? '2px solid var(--mantine-color-indigo-5)' : undefined,
      }}
    >
      {selectMode && (
        <Checkbox
          checked={selected}
          readOnly
          size="md"
          style={{ position: 'absolute', top: 8, left: 8, zIndex: 1, pointerEvents: 'none' }}
        />
      )}
      <Card.Section>
        <AspectRatio ratio={2 / 3}>
          {series.coverUrl ? (
            <Image src={series.coverUrl} alt={series.title} fit="cover" />
          ) : (
            <Center bg="dark.6">
              <Text size="sm" c="dimmed" ta="center" px="xs">
                {series.title}
              </Text>
            </Center>
          )}
        </AspectRatio>
      </Card.Section>
      <Text fw={600} size="sm" mt="xs" lineClamp={1} title={series.title}>
        {series.title}
      </Text>
      <Group justify="space-between" mt={4}>
        <Badge size="xs" color={statusColor[series.status] ?? 'gray'} variant="light">
          {series.status}
        </Badge>
        <Text size="xs" c="dimmed">
          {series.chapterFileCount}/{series.chapterCount || '?'}
        </Text>
      </Group>
    </Card>
  )
}

export default function LibraryPage() {
  const { data: series, isLoading, error } = useSeries()
  const queryClient = useQueryClient()
  const [selectMode, setSelectMode] = useState(false)
  const [selected, setSelected] = useState<Set<number>>(new Set())
  const [busy, setBusy] = useState<string | null>(null)
  const [deleteModalOpen, setDeleteModalOpen] = useState(false)
  const [deleteFiles, setDeleteFiles] = useState(false)
  const [monitorModalOpen, setMonitorModalOpen] = useState(false)
  const [monitorMode, setMonitorMode] = useState('All')

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

  const actionButton = (label: string, run: () => void, color?: string) => (
    <Button
      size="xs"
      variant="light"
      color={color}
      disabled={selected.size === 0 || (busy !== null && busy !== label)}
      loading={busy === label}
      onClick={run}
    >
      {label}
    </Button>
  )

  return (
    <>
      <Group justify="space-between" mb="md">
        <Title order={2}>Library</Title>
        {series && series.length > 0 && !selectMode && (
          <Button size="xs" variant="default" onClick={() => setSelectMode(true)}>
            Select
          </Button>
        )}
        {selectMode && (
          <Group gap="xs">
            <Text size="sm" c="dimmed">
              {selected.size} selected
            </Text>
            <Button
              size="xs"
              variant="default"
              onClick={() =>
                setSelected(
                  selected.size === series?.length
                    ? new Set()
                    : new Set(series?.map((s) => s.id)),
                )
              }
            >
              {selected.size === series?.length ? 'Clear all' : 'Select all'}
            </Button>
            {actionButton('Search missing', () =>
              runBulk('Search missing', (id) => api(`/series/${id}/searchmissing`, { method: 'POST' })),
            )}
            {actionButton('Refresh chapters', () =>
              runBulk('Refresh chapters', (id) => api(`/series/${id}/refresh`, { method: 'POST' })),
            )}
            {actionButton('Refresh metadata', () =>
              runBulk('Refresh metadata', (id) => api(`/series/${id}/refreshmetadata`, { method: 'POST' })),
            )}
            {actionButton('Update ComicInfo', () =>
              runBulk('Update ComicInfo', (id) => api(`/series/${id}/updatecomicinfo`, { method: 'POST' })),
            )}
            {actionButton('Monitoring', () => setMonitorModalOpen(true))}
            {actionButton('Delete', () => setDeleteModalOpen(true), 'red')}
            <Button size="xs" variant="default" disabled={busy !== null} onClick={exitSelectMode}>
              Done
            </Button>
          </Group>
        )}
      </Group>

      <Modal
        opened={deleteModalOpen}
        onClose={() => setDeleteModalOpen(false)}
        title={`Delete ${selected.size} series?`}
      >
        <Text size="sm" mb="md">
          The selected series will be removed from Mangarr and stop being monitored.
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
          Applies to every existing chapter and to chapters released later. "Main" skips
          specials (decimal chapters like 10.5).
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

      {isLoading && (
        <Center py="xl">
          <Loader />
        </Center>
      )}
      {error && <Text c="red">Failed to load library: {String(error)}</Text>}
      {series && series.length === 0 && (
        <Text c="dimmed">No series yet. Add one from the Add Series page.</Text>
      )}
      {series && series.length > 0 && (
        <SimpleGrid cols={{ base: 2, xs: 3, sm: 4, md: 5, lg: 6, xl: 8 }}>
          {series.map((s) => (
            <SeriesCard
              key={s.id}
              series={s}
              selectMode={selectMode}
              selected={selected.has(s.id)}
              onToggle={() => toggle(s.id)}
            />
          ))}
        </SimpleGrid>
      )}
    </>
  )
}
