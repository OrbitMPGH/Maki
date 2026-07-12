import { useState } from 'react'
import {
  ActionIcon,
  Anchor,
  Badge,
  Button,
  Card,
  Group,
  Image,
  Loader,
  Modal,
  Select,
  Stack,
  Switch,
  Table,
  Text,
  TextInput,
  Title,
  Tooltip,
} from '@mantine/core'
import { useDebouncedValue } from '@mantine/hooks'
import { notifications } from '@mantine/notifications'
import {
  useCreateMapping,
  useDeleteMapping,
  useResolveSourceUrl,
  useSourceMappings,
  useSources,
  useSourceSearch,
  useUpdateMapping,
} from '../api/hooks'

export function SourceMappingsSection({
  seriesId,
  seriesTitle,
}: {
  seriesId: number
  seriesTitle: string
}) {
  const { data: mappings } = useSourceMappings(seriesId)
  const { data: sources } = useSources()
  const updateMapping = useUpdateMapping()
  const deleteMapping = useDeleteMapping()
  const createMapping = useCreateMapping()

  const [modalOpen, setModalOpen] = useState(false)
  const [sourceName, setSourceName] = useState<string | null>(null)
  const [query, setQuery] = useState(seriesTitle)
  const [debounced] = useDebouncedValue(query, 400)
  // A pasted URL bypasses search: the backend maps it to a source + series id.
  const pastedUrl = /^https?:\/\//i.test(debounced.trim()) ? debounced.trim() : ''
  const { data: results, isFetching } = useSourceSearch(
    sourceName ?? '',
    pastedUrl ? '' : debounced,
  )
  const {
    data: resolved,
    isFetching: resolving,
    error: resolveError,
  } = useResolveSourceUrl(pastedUrl)

  const unmappedSources = sources?.filter(
    (s) => !mappings?.some((m) => m.sourceName === s.name),
  )

  const link = (name: string, sourceSeriesId: string, url: string) =>
    createMapping.mutate(
      { seriesId, sourceName: name, sourceSeriesId, url },
      {
        onSuccess: () => {
          notifications.show({ message: `Linked ${name}`, color: 'green' })
          setModalOpen(false)
        },
        onError: (err) => notifications.show({ message: String(err), color: 'red' }),
      },
    )

  return (
    <>
      <Group justify="space-between">
        <Title order={4}>Sources</Title>
        <Button
          size="xs"
          variant="light"
          disabled={!unmappedSources || unmappedSources.length === 0}
          onClick={() => {
            setSourceName(unmappedSources?.[0]?.name ?? null)
            setQuery(seriesTitle)
            setModalOpen(true)
          }}
        >
          Link source
        </Button>
      </Group>

      {!mappings || mappings.length === 0 ? (
        <Text c="dimmed" size="sm">
          No sources linked — chapters cannot be synced or downloaded.
        </Text>
      ) : (
        <Table>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Source</Table.Th>
              <Table.Th>Series</Table.Th>
              <Table.Th>Priority</Table.Th>
              <Table.Th>Enabled</Table.Th>
              <Table.Th>Last refresh</Table.Th>
              <Table.Th />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {mappings.map((m) => (
              <Table.Tr key={m.id}>
                <Table.Td>
                  <Text fw={600} size="sm">
                    {m.sourceName}
                  </Text>
                </Table.Td>
                <Table.Td>
                  <Anchor href={m.url} target="_blank" size="sm">
                    {m.sourceSeriesId}
                  </Anchor>
                </Table.Td>
                <Table.Td>{m.priority}</Table.Td>
                <Table.Td>
                  <Switch
                    size="xs"
                    checked={m.enabled}
                    onChange={(e) =>
                      updateMapping.mutate({ ...m, enabled: e.currentTarget.checked })
                    }
                  />
                </Table.Td>
                <Table.Td>
                  {m.lastError ? (
                    <Tooltip label={m.lastError} withArrow>
                      <Badge size="sm" color="red" variant="light">
                        Error
                      </Badge>
                    </Tooltip>
                  ) : (
                    <Text size="xs" c="dimmed">
                      {m.lastRefresh ? new Date(m.lastRefresh).toLocaleString() : 'never'}
                    </Text>
                  )}
                </Table.Td>
                <Table.Td>
                  <ActionIcon
                    variant="subtle"
                    color="red"
                    onClick={() =>
                      deleteMapping.mutate(
                        { id: m.id, seriesId },
                        {
                          onError: (err) =>
                            notifications.show({ message: String(err), color: 'red' }),
                        },
                      )
                    }
                    aria-label="Remove mapping"
                  >
                    ✕
                  </ActionIcon>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}

      <Modal
        opened={modalOpen}
        onClose={() => setModalOpen(false)}
        title="Link a source"
        size="lg"
      >
        <Stack>
          <Group grow>
            <Select
              label="Source"
              data={
                unmappedSources?.map((s) => ({ value: s.name, label: s.displayName })) ?? []
              }
              value={sourceName}
              onChange={setSourceName}
            />
            <TextInput
              label="Search or paste a series URL"
              value={query}
              onChange={(e) => setQuery(e.currentTarget.value)}
              rightSection={isFetching || resolving ? <Loader size="xs" /> : null}
            />
          </Group>
          <Stack gap="xs">
            {pastedUrl && resolved && (
              <Card
                withBorder
                padding="xs"
                style={{ cursor: 'pointer' }}
                onClick={() => link(resolved.sourceName, resolved.sourceSeriesId, resolved.url)}
              >
                <Group wrap="nowrap">
                  {resolved.coverUrl && (
                    <Image src={resolved.coverUrl} w={40} h={60} radius="sm" fit="cover" alt="" />
                  )}
                  <div style={{ flex: 1, minWidth: 0 }}>
                    <Group gap="xs">
                      <Text fw={600} size="sm">
                        {resolved.title}
                      </Text>
                      <Badge size="xs" variant="light">
                        {resolved.displayName}
                      </Badge>
                    </Group>
                    <Text size="xs" c="dimmed" lineClamp={1}>
                      {resolved.url}
                    </Text>
                  </div>
                </Group>
              </Card>
            )}
            {pastedUrl && resolveError && (
              <Text c="red" size="sm">
                {String(resolveError)}
              </Text>
            )}
            {!pastedUrl && results?.map((r) => (
              <Card
                key={r.sourceSeriesId}
                withBorder
                padding="xs"
                style={{ cursor: 'pointer' }}
                onClick={() => sourceName && link(sourceName, r.sourceSeriesId, r.url)}
              >
                <Group wrap="nowrap">
                  {r.coverUrl && (
                    <Image src={r.coverUrl} w={40} h={60} radius="sm" fit="cover" alt="" />
                  )}
                  <div style={{ flex: 1, minWidth: 0 }}>
                    <Text fw={600} size="sm">
                      {r.title}
                    </Text>
                    <Text size="xs" c="dimmed" lineClamp={1}>
                      {r.url}
                    </Text>
                  </div>
                  <Tooltip label="Open the page to check it's the right series" withArrow>
                    <ActionIcon
                      component="a"
                      href={r.url}
                      target="_blank"
                      rel="noreferrer"
                      variant="subtle"
                      onClick={(e) => e.stopPropagation()}
                      aria-label="Open source page"
                    >
                      ↗
                    </ActionIcon>
                  </Tooltip>
                </Group>
              </Card>
            ))}
            {sourceName && debounced.trim().length > 1 && results?.length === 0 && !isFetching && (
              <Text c="dimmed" size="sm">
                No results.
              </Text>
            )}
          </Stack>
        </Stack>
      </Modal>
    </>
  )
}
