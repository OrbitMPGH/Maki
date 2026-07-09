import { useState } from 'react'
import {
  Badge,
  Button,
  Checkbox,
  Group,
  Image,
  Select,
  Stack,
  Table,
  Text,
  Title,
} from '@mantine/core'
import { notifications } from '@mantine/notifications'
import { useMutation } from '@tanstack/react-query'
import { api } from '../api/client'
import { useRootFolders } from '../api/hooks'
import type { MetadataSearchResult } from '../api/types'

interface ScanCandidate {
  folderName: string
  cleanedTitle: string
  cbzCount: number
  recognizedCount: number
  matches: MetadataSearchResult[]
}

interface ImportResultDto {
  folderName: string
  success: boolean
  error: string | null
  seriesId: number | null
  newFolderName: string | null
  filesLinked: number
  filesUnrecognized: number
}

export default function ImportPage() {
  const { data: rootFolders } = useRootFolders()
  const [rootFolderId, setRootFolderId] = useState<string | null>(null)
  const [candidates, setCandidates] = useState<ScanCandidate[] | null>(null)
  const [selection, setSelection] = useState<Record<string, string>>({}) // folderName -> providerId ('' = skip)
  const [results, setResults] = useState<ImportResultDto[] | null>(null)

  const scan = useMutation({
    mutationFn: (folderId: number) =>
      api<ScanCandidate[]>(`/libraryimport/scan?rootFolderId=${folderId}`),
    onSuccess: (data) => {
      setCandidates(data)
      setResults(null)
      const initial: Record<string, string> = {}
      for (const c of data) {
        if (c.matches.length > 0) initial[c.folderName] = c.matches[0].providerId
      }
      setSelection(initial)
    },
    onError: (err) => notifications.show({ message: String(err), color: 'red' }),
  })

  const doImport = useMutation({
    mutationFn: (payload: {
      rootFolderId: number
      items: { folderName: string; metadataProviderId: string }[]
    }) => api<ImportResultDto[]>('/libraryimport/import', {
      method: 'POST',
      body: JSON.stringify(payload),
    }),
    onSuccess: (data) => {
      setResults(data)
      const ok = data.filter((r) => r.success).length
      notifications.show({
        message: `Imported ${ok}/${data.length} folder(s)`,
        color: ok === data.length ? 'green' : 'yellow',
      })
      if (rootFolderId) scan.mutate(Number(rootFolderId))
    },
    onError: (err) => notifications.show({ message: String(err), color: 'red' }),
  })

  const selectedItems = Object.entries(selection)
    .filter(([, providerId]) => providerId !== '')
    .map(([folderName, metadataProviderId]) => ({ folderName, metadataProviderId }))

  return (
    <>
      <Title order={2} mb="md">
        Import Existing Library
      </Title>
      <Text c="dimmed" size="sm" mb="md" maw={720}>
        Scans a root folder for series folders Mangarr doesn't know yet, matches them to
        metadata, renames each folder to the English title, and links the existing CBZ files
        to chapters. Files keep their original names.
      </Text>

      <Group mb="lg" align="flex-end">
        <Select
          label="Root folder"
          data={rootFolders?.map((f) => ({ value: String(f.id), label: f.path })) ?? []}
          value={rootFolderId}
          onChange={setRootFolderId}
          w={380}
        />
        <Button
          onClick={() => rootFolderId && scan.mutate(Number(rootFolderId))}
          loading={scan.isPending}
          disabled={!rootFolderId}
        >
          Scan
        </Button>
        {candidates && candidates.length > 0 && (
          <Button
            color="teal"
            loading={doImport.isPending}
            disabled={selectedItems.length === 0}
            onClick={() =>
              rootFolderId &&
              doImport.mutate({ rootFolderId: Number(rootFolderId), items: selectedItems })
            }
          >
            Import {selectedItems.length} selected
          </Button>
        )}
      </Group>

      {results && results.some((r) => !r.success) && (
        <Stack gap={4} mb="md">
          {results
            .filter((r) => !r.success)
            .map((r) => (
              <Text key={r.folderName} c="red" size="sm">
                {r.folderName}: {r.error}
              </Text>
            ))}
        </Stack>
      )}

      {candidates && candidates.length === 0 && (
        <Text c="dimmed">Nothing to import — every folder is already claimed.</Text>
      )}

      {candidates && candidates.length > 0 && (
        <Table striped>
          <Table.Thead>
            <Table.Tr>
              <Table.Th w={40} />
              <Table.Th>Folder</Table.Th>
              <Table.Th>Files</Table.Th>
              <Table.Th w={420}>Match</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {candidates.map((c) => {
              const selected = selection[c.folderName] ?? ''
              const match = c.matches.find((m) => m.providerId === selected)
              return (
                <Table.Tr key={c.folderName}>
                  <Table.Td>
                    <Checkbox
                      checked={selected !== ''}
                      disabled={c.matches.length === 0}
                      onChange={(e) =>
                        setSelection((s) => ({
                          ...s,
                          [c.folderName]: e.currentTarget.checked
                            ? c.matches[0]?.providerId ?? ''
                            : '',
                        }))
                      }
                    />
                  </Table.Td>
                  <Table.Td>
                    <Text size="sm" fw={600}>
                      {c.folderName}
                    </Text>
                    <Text size="xs" c="dimmed">
                      searched as “{c.cleanedTitle}”
                    </Text>
                  </Table.Td>
                  <Table.Td>
                    <Text size="sm">{c.cbzCount} CBZ</Text>
                    {c.recognizedCount < c.cbzCount && (
                      <Badge size="xs" color="yellow" variant="light">
                        {c.cbzCount - c.recognizedCount} unrecognized
                      </Badge>
                    )}
                  </Table.Td>
                  <Table.Td>
                    <Group wrap="nowrap" gap="xs">
                      {match?.coverUrl && (
                        <Image src={match.coverUrl} w={32} h={48} radius="sm" fit="cover" alt="" />
                      )}
                      {c.matches.length === 0 ? (
                        <Text size="sm" c="red">
                          No metadata match — rename the folder closer to the title and rescan.
                        </Text>
                      ) : (
                        <Select
                          data={[
                            { value: '', label: '— skip —' },
                            ...c.matches.map((m) => ({
                              value: m.providerId,
                              label: `${m.title}${m.year ? ` (${m.year})` : ''}`,
                            })),
                          ]}
                          value={selected}
                          onChange={(v) =>
                            setSelection((s) => ({ ...s, [c.folderName]: v ?? '' }))
                          }
                          style={{ flex: 1 }}
                        />
                      )}
                    </Group>
                  </Table.Td>
                </Table.Tr>
              )
            })}
          </Table.Tbody>
        </Table>
      )}
    </>
  )
}
