import { useState } from 'react'
import {
  Badge,
  Button,
  Card,
  Center,
  Group,
  Image,
  Loader,
  Modal,
  Select,
  Stack,
  Switch,
  Text,
  TextInput,
  Title,
} from '@mantine/core'
import { useDebouncedValue } from '@mantine/hooks'
import { notifications } from '@mantine/notifications'
import { useNavigate } from 'react-router-dom'
import { useAddSeries, useMetadataSearch, useRootFolders } from '../api/hooks'
import type { MetadataSearchResult } from '../api/types'

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
        onError: (err) => {
          notifications.show({ message: String(err), color: 'red' })
        },
      },
    )
  }

  return (
    <>
      <Title order={2} mb="md">
        Add Series
      </Title>
      <TextInput
        placeholder="Search MangaBaka for a series..."
        value={query}
        onChange={(e) => setQuery(e.currentTarget.value)}
        size="md"
        mb="md"
        rightSection={isFetching ? <Loader size="xs" /> : null}
      />

      <Stack>
        {results?.map((r) => (
          <Card
            key={r.providerId}
            withBorder
            radius="md"
            padding="sm"
            style={{ cursor: 'pointer' }}
            onClick={() => {
              setSelected(r)
              if (rootFolders && rootFolders.length > 0 && !rootFolderId) {
                setRootFolderId(String(rootFolders[0].id))
              }
            }}
          >
            <Group wrap="nowrap" align="flex-start">
              {r.coverUrl && (
                <Image src={r.coverUrl} w={60} h={90} radius="sm" fit="cover" alt="" />
              )}
              <div>
                <Group gap="xs">
                  <Text fw={600}>{r.title}</Text>
                  {r.year && (
                    <Text size="sm" c="dimmed">
                      ({r.year})
                    </Text>
                  )}
                  <Badge size="xs" variant="light">
                    {r.status}
                  </Badge>
                </Group>
                <Text size="sm" c="dimmed" lineClamp={2}>
                  {r.description}
                </Text>
              </div>
            </Group>
          </Card>
        ))}
        {debounced.trim().length > 1 && results?.length === 0 && !isFetching && (
          <Center py="lg">
            <Text c="dimmed">No results for “{debounced}”</Text>
          </Center>
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
          <Button onClick={submit} loading={addSeries.isPending} disabled={!rootFolderId}>
            Add series
          </Button>
        </Stack>
      </Modal>
    </>
  )
}
