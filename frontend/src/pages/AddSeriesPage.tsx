import { useEffect, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import {
  Badge,
  Box,
  Button,
  Center,
  Group,
  Image,
  Loader,
  Paper,
  Stack,
  Text,
  TextInput,
} from '@mantine/core'
import { IconPlus, IconSearch, IconSend } from '@tabler/icons-react'
import { useDebouncedValue } from '@mantine/hooks'
import { useMetadataSearch, useRootFolders, type RecommendationItem } from '../api/hooks'
import { useAuth } from '../auth/AuthProvider'
import type { MetadataSearchResult } from '../api/types'
import { DiscoverDetailModal } from '../components/DiscoverDetailModal'
import { EmptyState } from '../components/ui/EmptyState'
import { PageHeader } from '../components/ui/PageHeader'
import { seriesStatusVisual } from '../components/ui/status'

/** Search results only carry a subset of a Discover recommendation's fields, so pad the rest
 *  the shared detail modal (which expects a RecommendationItem) can render it. */
function toRecommendationItem(result: MetadataSearchResult): RecommendationItem {
  return {
    ...result,
    matchedGenres: [],
    matchedTags: [],
    authorMatch: false,
    relationKind: null,
    relatedToTitle: null,
    becauseOfTitle: null,
    rating: null,
    // A source search hit carries one cover URL and no size variants; the modal falls back to it.
    thumbUrl: null,
    thumbUrlHiDpi: null,
  }
}

export default function AddSeriesPage() {
  // The command palette sends the text you typed there here as ?q= when the library holds no
  // match. Seeded rather than controlled: the param is a starting point, and typing over it
  // must not fight the URL. Synced on change too, since arriving from the palette while already
  // on this page re-renders instead of remounting.
  const [searchParams] = useSearchParams()
  const seeded = searchParams.get('q')
  const [query, setQuery] = useState(seeded ?? '')
  const [debounced] = useDebouncedValue(query, 400)

  useEffect(() => {
    if (seeded !== null) setQuery(seeded)
  }, [seeded])
  const [selected, setSelected] = useState<MetadataSearchResult | null>(null)

  const { can } = useAuth()
  const { data: results, isFetching } = useMetadataSearch(debounced)
  const { data: rootFolders } = useRootFolders()

  // Same search, same results, same detail modal: only the verb changes. Someone without
  // AddSeries files a request an admin actions instead of adding the series themselves.
  const canAdd = can('AddSeries')

  return (
    <>
      <PageHeader
        title={canAdd ? 'Add series' : 'Request series'}
        description={
          canAdd
            ? 'Search MangaBaka, pick a title, choose where it lives, and Maki handles the rest.'
            : 'Search MangaBaka and ask an admin for a title. You can ask for a chapter range too.'
        }
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
              onClick={() => setSelected(r)}
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
                  leftSection={canAdd ? <IconPlus size={15} /> : <IconSend size={15} />}
                  onClick={(e) => {
                    e.stopPropagation()
                    setSelected(r)
                  }}
                >
                  {canAdd ? 'Add' : 'Request'}
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

      <DiscoverDetailModal
        item={selected ? toRecommendationItem(selected) : null}
        inLibrarySeriesId={null}
        rootFolders={rootFolders}
        onClose={() => setSelected(null)}
      />
    </>
  )
}
