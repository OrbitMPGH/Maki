import { useState } from 'react'
import {
  Badge,
  Box,
  Button,
  Center,
  Group,
  Image,
  Loader,
  Modal,
  Paper,
  Select,
  Stack,
  Switch,
  Text,
  TextInput,
} from '@mantine/core'
import { IconPlus, IconSearch } from '@tabler/icons-react'
import { useDebouncedValue } from '@mantine/hooks'
import { notifications } from '@mantine/notifications'
import { useNavigate } from 'react-router-dom'
import { useAddSeries, useMetadataSearch, useRootFolders } from '../api/hooks'
import type { MetadataSearchResult } from '../api/types'
import { EmptyState } from '../components/ui/EmptyState'
import { PageHeader } from '../components/ui/PageHeader'
import { seriesStatusVisual } from '../components/ui/status'

export default function AddSeriesPage() {
  const [query, setQuery] = useState('')
  const [debounced] = useDebouncedValue(query, 400)
  const [selected, setSelected] = useState<MetadataSearchResult | null>(null)
  const [rootFolderId, setRootFolderId] = useState<string | null>(null)
  const [monitored, setMonitored] = useState(true)

  const navigate = useNavigate()
  const { data: results, isFetching } = useMetadataSearch(debounced)
  const { data: rootFolders } = useRootFolders()
  const addSeries = useAddSeries()

  const openAdd = (r: MetadataSearchResult) => {
    setSelected(r)
    if (rootFolders && rootFolders.length > 0 && !rootFolderId) {
      setRootFolderId(String(rootFolders[0].id))
    }
  }

  const submit = () => {
    if (!selected || !rootFolderId) return
    addSeries.mutate(
      {
        metadataProviderId: selected.providerId,
        rootFolderId: Number(rootFolderId),
        monitored,
        monitorNewItems: monitored ? 'All' : 'None',
      },
      {
        onSuccess: () => {
          notifications.show({ message: `Added ${selected.title}`, color: 'green' })
          setSelected(null)
          navigate('/')
        },
        onError: (err) => notifications.show({ message: String(err), color: 'red' }),
      },
    )
  }

  return (
    <>
      <PageHeader
        title="Add series"
        description="Search MangaBaka, pick a title, choose where it lives — Mangarr handles the rest."
      />

      <TextInput
        placeholder="Search MangaBaka for a series…"
        leftSection={<IconSearch size={18} />}
        rightSection={isFetching ? <Loader size="xs" /> : null}
        value={query}
        onChange={(e) => setQuery(e.currentTarget.value)}
        size="md"
        mb="lg"
        maw={640}
      />

      <Stack gap="xs">
        {results?.map((r) => {
          const status = seriesStatusVisual(r.status)
          return (
            <Paper
              key={r.providerId}
              withBorder
              radius="lg"
              p="sm"
              className="hover-raise"
              style={{ cursor: 'pointer' }}
              onClick={() => openAdd(r)}
            >
              <Group wrap="nowrap" align="flex-start">
                <Box
                  style={{
                    width: 56,
                    height: 84,
                    flexShrink: 0,
                    borderRadius: 8,
                    overflow: 'hidden',
                    background: 'var(--surface-2)',
                  }}
                >
                  {r.coverUrl && (
                    <Image src={r.coverUrl} w={56} h={84} fit="cover" alt="" />
                  )}
                </Box>
                <div style={{ flex: 1, minWidth: 0 }}>
                  <Group gap="xs" wrap="nowrap">
                    <Text fw={650} lineClamp={1}>
                      {r.title}
                    </Text>
                    {r.year && (
                      <Text size="sm" c="dimmed" className="tnum">
                        {r.year}
                      </Text>
                    )}
                    <Badge size="sm" variant="light" color={status.color} leftSection={<status.Icon size={11} />}>
                      {status.label}
                    </Badge>
                  </Group>
                  <Text size="sm" c="dimmed" lineClamp={2} mt={4}>
                    {r.description}
                  </Text>
                </div>
                <Button
                  variant="light"
                  size="xs"
                  leftSection={<IconPlus size={15} />}
                  onClick={(e) => {
                    e.stopPropagation()
                    openAdd(r)
                  }}
                >
                  Add
                </Button>
              </Group>
            </Paper>
          )
        })}
        {debounced.trim().length > 1 && results?.length === 0 && !isFetching && (
          <Center py="xl">
            <Text c="dimmed">No results for “{debounced}”</Text>
          </Center>
        )}
        {debounced.trim().length <= 1 && (
          <EmptyState
            icon={IconSearch}
            title="Search for a series"
            description="Type at least two characters to search the MangaBaka catalogue."
          />
        )}
      </Stack>

      <Modal
        opened={selected !== null}
        onClose={() => setSelected(null)}
        title={`Add “${selected?.title}”`}
      >
        <Stack>
          {rootFolders && rootFolders.length === 0 && (
            <Text c="orange" size="sm">
              No root folders configured. Add one in Settings first.
            </Text>
          )}
          <Select
            label="Root folder"
            data={rootFolders?.map((f) => ({ value: String(f.id), label: f.path })) ?? []}
            value={rootFolderId}
            onChange={setRootFolderId}
            required
          />
          <Switch
            label="Monitor new chapters"
            checked={monitored}
            onChange={(e) => setMonitored(e.currentTarget.checked)}
          />
          <Button
            onClick={submit}
            loading={addSeries.isPending}
            disabled={!rootFolderId}
            leftSection={<IconPlus size={16} />}
          >
            Add series
          </Button>
        </Stack>
      </Modal>
    </>
  )
}
