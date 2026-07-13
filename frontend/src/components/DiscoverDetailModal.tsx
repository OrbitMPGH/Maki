import { useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import {
  Alert,
  Anchor,
  Badge,
  Button,
  Divider,
  Group,
  Image,
  Loader,
  Modal,
  Paper,
  Select,
  SimpleGrid,
  Skeleton,
  Spoiler,
  Stack,
  Switch,
  Text,
  Title,
  Tooltip,
} from '@mantine/core'
import { IconExternalLink, IconPlus, IconStar } from '@tabler/icons-react'
import { notifications } from '@mantine/notifications'
import {
  useAddSeries,
  useMangaReviews,
  useRecommendationDetail,
  type RecommendationItem,
} from '../api/hooks'
import type { RootFolder } from '../api/types'
import { MetadataLinks } from './MetadataLinks'

/** MangaBaka tag relevance buckets → colour, most-relevant first. */
const TAG_WEIGHTS: { key: string; label: string; color: string }[] = [
  { key: 'core', label: 'Core', color: 'red' },
  { key: 'defining', label: 'Defining', color: 'grape' },
  { key: 'recurrent', label: 'Recurrent', color: 'teal' },
  { key: 'incidental', label: 'Incidental', color: 'gray' },
]

// Some series (e.g. One Piece) carry ~90 low-relevance tags in a single bucket; cap each
// bucket so the modal stays readable, noting the remainder rather than dumping a wall.
const MAX_TAGS_PER_BUCKET = 18

function ratingColor(rating: number): string {
  if (rating >= 80) return 'green'
  if (rating >= 65) return 'lime'
  if (rating >= 50) return 'yellow'
  return 'orange'
}

export function DiscoverDetailModal({
  item,
  inLibrarySeriesId,
  rootFolders,
  onClose,
}: {
  /** The card that was clicked; null closes the modal. Used for an instant header while detail loads. */
  item: RecommendationItem | null
  /** Library series id if already owned (enables "View in library"); null/undefined otherwise. */
  inLibrarySeriesId: number | null | undefined
  rootFolders: RootFolder[] | undefined
  onClose: () => void
}) {
  const navigate = useNavigate()
  const { data: detail, isLoading } = useRecommendationDetail(item?.providerId ?? null)
  const { data: reviews, isLoading: reviewsLoading } = useMangaReviews(detail?.malId ?? null)
  const addSeries = useAddSeries()

  const [rootFolderId, setRootFolderId] = useState<string | null>(null)
  const [monitored, setMonitored] = useState(true)

  useEffect(() => {
    if (rootFolders && rootFolders.length > 0 && !rootFolderId) {
      setRootFolderId(String(rootFolders[0].id))
    }
  }, [rootFolders, rootFolderId])

  const title = detail?.title ?? item?.title ?? ''
  const cover = detail?.coverUrl ?? item?.coverUrl ?? null
  const genres = detail?.genres ?? item?.matchedGenres ?? []

  const goToLibrary = () => {
    if (inLibrarySeriesId != null) {
      onClose()
      navigate(`/series/${inLibrarySeriesId}`)
    }
  }

  const add = () => {
    if (!item || !rootFolderId) return
    addSeries.mutate(
      {
        metadataProviderId: item.providerId,
        rootFolderId: Number(rootFolderId),
        monitored,
        monitorNewItems: monitored ? 'All' : 'None',
      },
      {
        onSuccess: (series) => {
          notifications.show({
            title: `Added ${title}`,
            message: 'Now in your library — click “View in library” to open it.',
            color: 'green',
          })
          onClose()
          navigate(`/series/${series.id}`)
        },
        onError: (err) => notifications.show({ message: String(err), color: 'red' }),
      },
    )
  }

  return (
    <Modal opened={item !== null} onClose={onClose} size="xl" title={null} padding="lg">
      {item === null ? null : (
        <Stack gap="md">
          <Group wrap="nowrap" align="flex-start" gap="lg">
            {cover ? (
              <Image
                src={cover}
                w={180}
                h={270}
                radius="md"
                fit="cover"
                alt=""
                style={{ flexShrink: 0 }}
              />
            ) : (
              <Skeleton w={180} h={270} radius="md" style={{ flexShrink: 0 }} />
            )}

            <Stack gap="xs" style={{ flex: 1, minWidth: 0 }}>
              <div>
                <Title order={3} lh={1.2}>
                  {title}
                </Title>
                {(detail?.nativeTitle || detail?.romanizedTitle) && (
                  <Text size="sm" c="dimmed">
                    {[detail?.romanizedTitle, detail?.nativeTitle].filter(Boolean).join(' · ')}
                  </Text>
                )}
              </div>

              <Group gap="xs">
                {detail?.type && (
                  <Badge variant="light" tt="capitalize">
                    {detail.type}
                  </Badge>
                )}
                <Badge variant="light" tt="capitalize">
                  {detail?.status ?? item.status}
                </Badge>
                {detail?.year && (
                  <Text size="sm" c="dimmed">
                    {detail.year}
                  </Text>
                )}
                {(detail?.totalChapters ?? item.totalChapters) && (
                  <Text size="sm" c="dimmed">
                    {detail?.totalChapters ?? item.totalChapters} ch
                  </Text>
                )}
                {detail?.finalVolume && (
                  <Text size="sm" c="dimmed">
                    {detail.finalVolume} vol
                  </Text>
                )}
                {detail?.contentRating && detail.contentRating !== 'safe' && (
                  <Badge variant="light" color="pink" tt="capitalize">
                    {detail.contentRating}
                  </Badge>
                )}
              </Group>

              {(detail?.rating ?? item.rating) != null && (
                <Group gap="xs" align="center">
                  <Badge
                    size="lg"
                    color={ratingColor(detail?.rating ?? item.rating ?? 0)}
                    leftSection={<IconStar size={13} />}
                  >
                    {((detail?.rating ?? item.rating ?? 0) / 10).toFixed(1)}
                  </Badge>
                  {detail?.sourceRatings.map((r) => (
                    <Tooltip key={r.source} label={r.source} withArrow>
                      <Badge size="sm" variant="outline" color="gray">
                        {r.source.slice(0, 2).toUpperCase()} {(r.rating / 10).toFixed(1)}
                      </Badge>
                    </Tooltip>
                  ))}
                </Group>
              )}

              <MetadataLinks links={detail?.links ?? []} />

              <Group gap="sm" mt="xs">
                {inLibrarySeriesId != null ? (
                  <Button color="teal" variant="light" onClick={goToLibrary}>
                    View in library
                  </Button>
                ) : (
                  <>
                    <Select
                      placeholder="Root folder"
                      data={rootFolders?.map((f) => ({ value: String(f.id), label: f.path })) ?? []}
                      value={rootFolderId}
                      onChange={setRootFolderId}
                      size="sm"
                      w={200}
                    />
                    <Switch
                      label="Monitor"
                      checked={monitored}
                      onChange={(e) => setMonitored(e.currentTarget.checked)}
                    />
                    <Button
                      leftSection={<IconPlus size={16} />}
                      onClick={add}
                      loading={addSeries.isPending}
                      disabled={!rootFolderId}
                    >
                      Add
                    </Button>
                  </>
                )}
              </Group>
            </Stack>
          </Group>

          {isLoading && !detail && (
            <Stack gap="xs">
              <Skeleton h={12} />
              <Skeleton h={12} />
              <Skeleton h={12} w="70%" />
            </Stack>
          )}

          {(detail?.description || item.description) && (
            <Spoiler maxHeight={120} showLabel="Show more" hideLabel="Show less">
              <Text size="sm" style={{ whiteSpace: 'pre-line' }}>
                {detail?.description ?? item.description}
              </Text>
            </Spoiler>
          )}

          {genres.length > 0 && (
            <div>
              <Text size="xs" fw={700} c="dimmed" tt="uppercase" mb={6}>
                Genres
              </Text>
              <Group gap={6}>
                {genres.map((g) => (
                  <Badge key={g} variant="dot" color="blue">
                    {g}
                  </Badge>
                ))}
              </Group>
            </div>
          )}

          {detail && detail.tags.length > 0 && (
            <div>
              <Text size="xs" fw={700} c="dimmed" tt="uppercase" mb={6}>
                Tags
              </Text>
              <Stack gap={8}>
                {TAG_WEIGHTS.map((bucket) => {
                  const tags = detail.tags.filter((t) => t.weight === bucket.key)
                  if (tags.length === 0) return null
                  const shown = tags.slice(0, MAX_TAGS_PER_BUCKET)
                  const overflow = tags.length - shown.length
                  return (
                    <Group key={bucket.key} gap={6} align="flex-start" wrap="nowrap">
                      <Text size="xs" c="dimmed" w={72} mt={3} style={{ flexShrink: 0 }}>
                        {bucket.label}
                      </Text>
                      <Group gap={6}>
                        {shown.map((t) => {
                          const badge = (
                            <Badge variant="light" color={bucket.color}>
                              {t.name}
                            </Badge>
                          )
                          return t.description ? (
                            <Tooltip
                              key={t.name}
                              label={t.description}
                              withArrow
                              multiline
                              maw={320}
                              openDelay={200}
                            >
                              {badge}
                            </Tooltip>
                          ) : (
                            <span key={t.name}>{badge}</span>
                          )
                        })}
                        {overflow > 0 && (
                          <Text size="xs" c="dimmed" mt={3}>
                            +{overflow} more
                          </Text>
                        )}
                      </Group>
                    </Group>
                  )
                })}
              </Stack>
            </div>
          )}

          {detail && (detail.authors.length > 0 || detail.artists.length > 0 || detail.publishers.length > 0) && (
            <SimpleGrid cols={{ base: 1, sm: 3 }} spacing="sm">
              {detail.authors.length > 0 && (
                <Credit label="Story" values={detail.authors} />
              )}
              {detail.artists.length > 0 && <Credit label="Art" values={detail.artists} />}
              {detail.publishers.length > 0 && (
                <Credit label="Publishers" values={detail.publishers} />
              )}
            </SimpleGrid>
          )}

          {detail?.malId != null && (
            <>
              <Divider label="MyAnimeList reviews" labelPosition="center" />
              {reviewsLoading && (
                <Group justify="center" py="sm">
                  <Loader size="sm" />
                </Group>
              )}
              {!reviewsLoading && reviews && reviews.length === 0 && (
                <Text size="sm" c="dimmed" ta="center">
                  No reviews found.
                </Text>
              )}
              {reviews?.map((review, i) => (
                <Paper key={i} withBorder radius="md" p="sm">
                  <Group justify="space-between" mb={4}>
                    <Group gap="xs">
                      <Text size="sm" fw={600}>
                        {review.author}
                      </Text>
                      {review.score != null && (
                        <Badge size="sm" color={ratingColor(review.score * 10)} leftSection={<IconStar size={11} />}>
                          {review.score}
                        </Badge>
                      )}
                      {review.tags.map((t) => (
                        <Badge key={t} size="xs" variant="light" color="gray">
                          {t}
                        </Badge>
                      ))}
                    </Group>
                    {review.url && (
                      <Anchor href={review.url} target="_blank" rel="noopener noreferrer" size="xs">
                        <Group gap={2}>
                          Full <IconExternalLink size={12} />
                        </Group>
                      </Anchor>
                    )}
                  </Group>
                  <Spoiler maxHeight={90} showLabel="Show more" hideLabel="Show less">
                    <Text size="sm" c="dimmed" style={{ whiteSpace: 'pre-line' }}>
                      {review.text}
                    </Text>
                  </Spoiler>
                </Paper>
              ))}
            </>
          )}

          {addSeries.isError && (
            <Alert color="red" variant="light">
              {String(addSeries.error)}
            </Alert>
          )}
        </Stack>
      )}
    </Modal>
  )
}

function Credit({ label, values }: { label: string; values: string[] }) {
  return (
    <div>
      <Text size="xs" fw={700} c="dimmed" tt="uppercase" mb={2}>
        {label}
      </Text>
      <Text size="sm">{values.join(', ')}</Text>
    </div>
  )
}
