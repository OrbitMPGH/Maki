import { useState } from 'react'
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
import { IconPlus, IconSearch } from '@tabler/icons-react'
import { useDebouncedValue } from '@mantine/hooks'
import { useMetadataSearch, useRootFolders, type RecommendationItem } from '../api/hooks'
import type { MetadataSearchResult } from '../api/types'
import { DiscoverDetailModal } from '../components/DiscoverDetailModal'
import { EmptyState } from '../components/ui/EmptyState'
import { PageHeader } from '../components/ui/PageHeader'
import { seriesStatusVisual } from '../components/ui/status'

/** Search results only carry a subset of a Discover recommendation's fields — pad the rest so
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
  }
}

export default function AddSeriesPage() {
  const [query, setQuery] = useState('')
  const [debounced] = useDebouncedValue(query, 400)
  const [selected, setSelected] = useState<MetadataSearchResult | null>(null)

  const { data: results, isFetching } = useMetadataSearch(debounced)
  const { data: rootFolders } = useRootFolders()

  return (
    <>
      <PageHeader
        title="Add series"
        description="Search MangaBaka, pick a title, choose where it lives — Maki handles the rest."
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
                  leftSection={<IconPlus size={15} />}
                  onClick={(e) => {
                    e.stopPropagation()
                    setSelected(r)
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

      <DiscoverDetailModal
        item={selected ? toRecommendationItem(selected) : null}
        inLibrarySeriesId={null}
        rootFolders={rootFolders}
        onClose={() => setSelected(null)}
      />
    </>
  )
}
