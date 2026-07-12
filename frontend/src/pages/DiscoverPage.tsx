import { useState } from 'react'
import {
  Alert,
  Badge,
  Button,
  Card,
  Center,
  Group,
  Image,
  Loader,
  Modal,
  Select,
  SimpleGrid,
  Stack,
  Switch,
  Text,
  Title,
} from '@mantine/core'
import { notifications } from '@mantine/notifications'
import {
  useAddSeries,
  useRecommendations,
  useRootFolders,
  useSeries,
  type RecommendationItem,
} from '../api/hooks'

function reasonFor(item: RecommendationItem): string {
  if (item.relationKind) {
    return `${item.relationKind} of ${item.relatedToTitle}`
  }
  const parts: string[] = []
  if (item.authorMatch) parts.push('same author')
  const because = [...item.matchedGenres, ...item.matchedTags].slice(0, 4)
  if (because.length > 0) parts.push(because.join(', '))
  return parts.length > 0 ? `Because: ${parts.join(' · ')}` : ''
}

function RecommendationCard({
  item,
  inLibrary,
  onAdd,
}: {
  item: RecommendationItem
  inLibrary: boolean
  onAdd: (item: RecommendationItem) => void
}) {
  return (
    <Card withBorder radius="md" padding="sm">
      <Group wrap="nowrap" align="flex-start">
        {item.coverUrl && (
          <Image src={item.coverUrl} w={70} h={105} radius="sm" fit="cover" alt="" />
        )}
        <Stack gap={4} style={{ flex: 1, minWidth: 0 }}>
          <Group gap="xs" wrap="nowrap">
            <Text fw={600} size="sm" lineClamp={1} style={{ flex: 1 }}>
              {item.title}
            </Text>
            {item.rating != null && (
              <Badge size="xs" variant="light" color="yellow">
                ★ {(item.rating / 10).toFixed(1)}
              </Badge>
            )}
          </Group>
          <Group gap="xs">
            {item.year && (
              <Text size="xs" c="dimmed">
                {item.year}
              </Text>
            )}
            <Badge size="xs" variant="light">
              {item.status}
            </Badge>
            {item.totalChapters && (
              <Text size="xs" c="dimmed">
                {item.totalChapters} ch
              </Text>
            )}
          </Group>
          <Text size="xs" c="indigo.4" lineClamp={1}>
            {reasonFor(item)}
          </Text>
          <Text size="xs" c="dimmed" lineClamp={2}>
            {item.description}
          </Text>
          <div>
            {inLibrary ? (
              <Badge size="sm" variant="light" color="green">
                In library
              </Badge>
            ) : (
              <Button size="compact-xs" variant="light" onClick={() => onAdd(item)}>
                Add
              </Button>
            )}
          </div>
        </Stack>
      </Group>
    </Card>
  )
}

export default function DiscoverPage() {
  const [refreshRequested, setRefreshRequested] = useState(false)
  const { data, isFetching, error } = useRecommendations(refreshRequested)
  const { data: library } = useSeries()
  const { data: rootFolders } = useRootFolders()
  const addSeries = useAddSeries()

  const [selected, setSelected] = useState<RecommendationItem | null>(null)
  const [rootFolderId, setRootFolderId] = useState<string | null>(null)
  const [monitored, setMonitored] = useState(true)

  // MangaBaka ids present in the library, so freshly added items flip to "In library".
  const libraryIds = new Set(
    (library ?? []).map((s) => s.mangaBakaId).filter((id): id is number => id != null),
  )

  const openAdd = (item: RecommendationItem) => {
    setSelected(item)
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
        },
        onError: (err) => notifications.show({ message: String(err), color: 'red' }),
      },
    )
  }

  return (
    <>
      <Group justify="space-between" mb="md">
        <Title order={2}>Discover</Title>
        <Button
          variant="default"
          size="xs"
          loading={isFetching}
          onClick={() => setRefreshRequested(true)}
        >
          Refresh
        </Button>
      </Group>

      {error && (
        <Alert color="yellow" variant="light">
          {String(error)}
        </Alert>
      )}
      {isFetching && !data && (
        <Center py="xl">
          <Loader />
          <Text ml="sm" c="dimmed" size="sm">
            Scanning the MangaBaka database for matches — first run takes a moment…
          </Text>
        </Center>
      )}

      {data && data.related.length === 0 && data.similar.length === 0 && (
        <Text c="dimmed">
          Nothing to recommend yet — add some series to the library first.
        </Text>
      )}

      {data && data.related.length > 0 && (
        <>
          <Title order={4} mb="sm">
            Related to your library
          </Title>
          <SimpleGrid cols={{ base: 1, md: 2, xl: 3 }} mb="lg">
            {data.related.map((item) => (
              <RecommendationCard
                key={item.providerId}
                item={item}
                inLibrary={libraryIds.has(Number(item.providerId))}
                onAdd={openAdd}
              />
            ))}
          </SimpleGrid>
        </>
      )}

      {data && data.similar.length > 0 && (
        <>
          <Title order={4} mb="sm">
            Because of what you collect
          </Title>
          <SimpleGrid cols={{ base: 1, md: 2, xl: 3 }}>
            {data.similar.map((item) => (
              <RecommendationCard
                key={item.providerId}
                item={item}
                inLibrary={libraryIds.has(Number(item.providerId))}
                onAdd={openAdd}
              />
            ))}
          </SimpleGrid>
        </>
      )}

      <Modal
        opened={selected !== null}
        onClose={() => setSelected(null)}
        title={`Add "${selected?.title}"`}
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
