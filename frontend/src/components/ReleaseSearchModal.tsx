import { Alert, Badge, Button, Center, Group, Loader, Modal, Table, Text, TextInput } from '@mantine/core'
import { notifications } from '@mantine/notifications'
import { useEffect, useState } from 'react'
import { Trans, useLingui } from '@lingui/react/macro'
import { t as now } from '@lingui/core/macro'
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
  const { t } = useLingui()
  const [input, setInput] = useState('')
  const [manualQuery, setManualQuery] = useState<string | undefined>(undefined)
  const { data, isFetching, error } = useReleaseSearch(seriesId, opened, manualQuery)
  const releases = data?.releases
  const grab = useGrabRelease()

  // Start each open with an automatic search; mirror whatever query was actually used
  // (the backend may have loosened it) into the input so the user can tweak it.
  useEffect(() => {
    if (opened) {
      setManualQuery(undefined)
      setInput('')
    }
  }, [opened, seriesId])
  useEffect(() => {
    if (data) {
      setInput(data.query)
    }
  }, [data])

  const search = () => {
    const q = input.trim()
    if (q) {
      setManualQuery(q)
    }
  }

  return (
    <Modal opened={opened} onClose={onClose} title={t`Search releases (Prowlarr)`} size="min(1180px, calc(100vw - 3rem))">
      <Group gap="xs" mb="md" wrap="nowrap">
        <TextInput
          style={{ flex: 1 }}
          placeholder={t`Search query`}
          value={input}
          onChange={(e) => setInput(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter') {
              search()
            }
          }}
          disabled={isFetching}
        />
        <Button variant="light" onClick={search} disabled={isFetching || !input.trim()}>
          <Trans>Search</Trans>
        </Button>
      </Group>
      {isFetching && (
        <Center py="lg">
          <Loader />
          <Text ml="sm" c="var(--ink-3)" size="sm">
            <Trans>Searching indexers…</Trans>
          </Text>
        </Center>
      )}
      {error && (
        <Alert color="var(--danger)" variant="light">
          {String(error)}
        </Alert>
      )}
      {releases && releases.length === 0 && !isFetching && (
        <Text c="var(--ink-3)">
          <Trans>No releases found.</Trans> <Trans>Try a shorter or alternative query.</Trans>
        </Text>
      )}
      {releases && releases.length > 0 && (
        <Table highlightOnHover>
          <Table.Thead>
            <Table.Tr>
              <Table.Th><Trans>Title</Trans></Table.Th>
              <Table.Th><Trans>Indexer</Trans></Table.Th>
              <Table.Th><Trans>Size</Trans></Table.Th>
              <Table.Th><Trans>Seeds</Trans></Table.Th>
              <Table.Th />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {releases.map((r) => {
              const { title, indexer, infoUrl, size, seeders } = r
              return (
                <Table.Tr key={r.guid}>
                  <Table.Td>
                    <Text size="sm" style={{ wordBreak: 'break-word' }}>
                      {infoUrl ? (
                        <a href={infoUrl} target="_blank" rel="noreferrer">
                          {title}
                        </a>
                      ) : (
                        title
                      )}
                    </Text>
                  </Table.Td>
                  <Table.Td>
                    <Badge size="sm" variant="light">
                      {indexer}
                    </Badge>
                  </Table.Td>
                  <Table.Td>
                    <Text size="sm">{formatSize(size)}</Text>
                  </Table.Td>
                  <Table.Td>
                    <Text size="sm" c={(seeders ?? 0) > 0 ? 'var(--ok)' : 'var(--danger)'}>
                      {seeders ?? '?'}
                    </Text>
                  </Table.Td>
                  <Table.Td>
                    <Button
                      size="compact-xs"
                      variant="light"
                      loading={grab.isPending && grab.variables?.release.guid === r.guid}
                      onClick={() =>
                        grab.mutate(
                          { seriesId, release: r },
                          {
                            onSuccess: () => {
                              notifications.show({
                                message: now`Sent to qBittorrent: ${title}`,
                                color: 'var(--ok)',
                              })
                              onClose()
                            },
                          },
                        )
                      }
                    >
                      <Trans>Grab</Trans>
                    </Button>
                  </Table.Td>
                </Table.Tr>
              )
            })}
          </Table.Tbody>
        </Table>
      )}
    </Modal>
  )
}
