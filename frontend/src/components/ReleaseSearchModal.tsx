import { Alert, Badge, Button, Center, Loader, Modal, Table, Text } from '@mantine/core'
import { notifications } from '@mantine/notifications'
import { useGrabRelease, useReleaseSearch } from '../api/hooks'

function formatSize(bytes: number): string {
  const units = ['B', 'KB', 'MB', 'GB']
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit++
  }
  return `${value.toFixed(1)} ${units[unit]}`
}

export function ReleaseSearchModal({
  seriesId,
  opened,
  onClose,
}: {
  seriesId: number
  opened: boolean
  onClose: () => void
}) {
  const { data: releases, isFetching, error } = useReleaseSearch(seriesId, opened)
  const grab = useGrabRelease()

  return (
    <Modal opened={opened} onClose={onClose} title="Search releases (Prowlarr)" size="xl">
      {isFetching && (
        <Center py="lg">
          <Loader />
          <Text ml="sm" c="dimmed" size="sm">
            Searching indexers…
          </Text>
        </Center>
      )}
      {error && (
        <Alert color="red" variant="light">
          {String(error)}
        </Alert>
      )}
      {releases && releases.length === 0 && !isFetching && (
        <Text c="dimmed">No releases found.</Text>
      )}
      {releases && releases.length > 0 && (
        <Table striped highlightOnHover>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Title</Table.Th>
              <Table.Th>Indexer</Table.Th>
              <Table.Th>Size</Table.Th>
              <Table.Th>Seeds</Table.Th>
              <Table.Th />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {releases.map((r) => (
              <Table.Tr key={r.guid}>
                <Table.Td maw={420}>
                  <Text size="sm" lineClamp={2}>
                    {r.infoUrl ? (
                      <a href={r.infoUrl} target="_blank" rel="noreferrer">
                        {r.title}
                      </a>
                    ) : (
                      r.title
                    )}
                  </Text>
                </Table.Td>
                <Table.Td>
                  <Badge size="sm" variant="light">
                    {r.indexer}
                  </Badge>
                </Table.Td>
                <Table.Td>
                  <Text size="sm">{formatSize(r.size)}</Text>
                </Table.Td>
                <Table.Td>
                  <Text size="sm" c={(r.seeders ?? 0) > 0 ? 'green' : 'red'}>
                    {r.seeders ?? '?'}
                  </Text>
                </Table.Td>
                <Table.Td>
                  <Button
                    size="compact-xs"
                    variant="light"
                    loading={grab.isPending}
                    onClick={() =>
                      grab.mutate(
                        { seriesId, release: r },
                        {
                          onSuccess: () => {
                            notifications.show({
                              message: `Sent to qBittorrent: ${r.title}`,
                              color: 'green',
                            })
                            onClose()
                          },
                          onError: (err) =>
                            notifications.show({ message: String(err), color: 'red' }),
                        },
                      )
                    }
                  >
                    Grab
                  </Button>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}
    </Modal>
  )
}
