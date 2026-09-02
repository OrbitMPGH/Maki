import { useEffect, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import {
  Alert,
  Anchor,
  Badge,
  Button,
  Divider,
  Flex,
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
import {
  IconArrowRight,
  IconCheck,
  IconExternalLink,
  IconPlus,
  IconStar,
  IconDeviceTv,
  IconTrendingDown,
  IconTrendingUp,
} from '@tabler/icons-react'
import { notifications } from '@mantine/notifications'
import {
  useAddSeries,
  useLibrarySettings,
  useMangaReviews,
  useRecommendationDetail,
  type RecommendationItem,
} from '../api/hooks'
import { useCreateSeriesRequest } from '../api/requests'
import { useAuth } from '../auth/AuthProvider'
import type { RootFolder } from '../api/types'
import { MetadataLinks } from './MetadataLinks'
import { MetadataSiteIcon } from './MetadataSiteIcon'
import { RequestForm } from './RequestForm'
import { INCOGNITO_OPTIONS, type IncognitoMode } from './ui/incognito'

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
  const { can } = useAuth()
  const { data: detail, isLoading } = useRecommendationDetail(item?.providerId ?? null)
  const { data: reviews, isLoading: reviewsLoading } = useMangaReviews(detail?.malId ?? null)
  const addSeries = useAddSeries()
  const createRequest = useCreateSeriesRequest()
  const { data: librarySettings } = useLibrarySettings()

  // Without AddSeries the same modal asks an admin for the title instead of adding it. The server
  // enforces both halves independently; this only decides which form to draw.
  const canAdd = can('AddSeries')

  const [rootFolderId, setRootFolderId] = useState<string | null>(null)
  const [monitored, setMonitored] = useState(true)
  /**
   * Null until the content-rating rules have had their say. Choosing a value from the Select pins
   * it, so a rule that resolves late (the detail request carries the rating) can't overwrite a
   * deliberate choice a fast reader already made.
   */
  const [incognito, setIncognito] = useState<IncognitoMode | null>(null)
  const [incognitoPinned, setIncognitoPinned] = useState(false)
  const [chapterStart, setChapterStart] = useState<number | ''>('')
  const [chapterEnd, setChapterEnd] = useState<number | ''>('')
  const [note, setNote] = useState('')
  const [requested, setRequested] = useState(false)
  /**
   * Series id from an add made in this modal, so the button can flip to "Go to series" without
   * navigating. The ['series'] invalidation eventually feeds the same id back via
   * inLibrarySeriesId; this covers the gap until it refetches.
   */
  const [addedSeriesId, setAddedSeriesId] = useState<number | null>(null)

  useEffect(() => {
    if (rootFolders && rootFolders.length > 0 && !rootFolderId) {
      setRootFolderId(String(rootFolders[0].id))
    }
  }, [rootFolders, rootFolderId])

  // A different card opened the modal, so the previous add or request no longer applies.
  useEffect(() => {
    setAddedSeriesId(null)
    setRequested(false)
    setIncognito(null)
    setIncognitoPinned(false)
    setChapterStart('')
    setChapterEnd('')
    setNote('')
  }, [item?.providerId])

  // Auto-fill from Settings → Library ("Incognito by content rating"). The rating only arrives with
  // the detail response, so this can't be an initial state value.
  const ratingRule = detail?.contentRating
    ? librarySettings?.incognitoByRating?.[detail.contentRating]
    : undefined
  useEffect(() => {
    if (!incognitoPinned) {
      setIncognito(ratingRule ?? 'Off')
    }
  }, [ratingRule, incognitoPinned])

  const seriesId = inLibrarySeriesId ?? addedSeriesId

  const title = detail?.title ?? item?.title ?? ''
  // The card's 334x500 thumbnail stands in until the detail row's full-size art arrives: it is
  // already in the browser's image cache, so the modal opens with a cover rather than a hole.
  const cover = detail?.coverUrl ?? item?.thumbUrlHiDpi ?? item?.coverUrl ?? null
  const genres = detail?.genres ?? item?.matchedGenres ?? []

  const goToLibrary = () => {
    if (seriesId != null) {
      onClose()
      navigate(`/series/${seriesId}`)
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
        incognito: incognito ?? 'Off',
      },
      {
        onSuccess: (series) => {
          // Deliberately stay put: adding used to jump straight to the series page, throwing away
          // the Discover filters the user had set up and making a second add a round trip. The
          // button becomes "Go to series" instead, so leaving is their choice.
          setAddedSeriesId(series.id)

          // The series was created either way, so this stays a success, but a failed folder has to
          // be said out loud, not just logged server-side. Source matching is no longer among the
          // warnings — it runs in the background now, and the series page reports on it.
          const warnings = series.warnings ?? []
          notifications.show({
            title: `Added ${title}`,
            message:
              warnings.length > 0
                ? warnings.join(' ')
                : 'Now in your library. Matching sources in the background.',
            color: warnings.length > 0 ? 'yellow' : 'green',
            autoClose: warnings.length > 0 ? false : undefined,
          })
        },
      },
    )
  }

  const request = () => {
    if (!item) return
    createRequest.mutate(
      {
        kind: 'NewSeries',
        metadataProviderId: item.providerId,
        chapterStart: chapterStart === '' ? null : chapterStart,
        chapterEnd: chapterEnd === '' ? null : chapterEnd,
        note: note.trim() || null,
      },
      {
        onSuccess: () => {
          setRequested(true)
          notifications.show({
            title: `Requested ${title}`,
            message: 'An admin will see it on the Requests page.',
            color: 'green',
          })
        },
      },
    )
  }

  return (
    // Explicit zIndex: Discover's fullscreen "Show more" modal (FeedExpandModal) can open this
    // one from a card click inside it, and both default to the same Mantine modal z-index, so
    // whichever mounted first would otherwise win and this modal opened from behind it.
    <Modal
      opened={item !== null}
      onClose={onClose}
      size="xl"
      title={null}
      padding="lg"
      zIndex={1000}
    >
      {item === null ? null : (
        <Stack gap="md">
          {/* Two columns until there isn't room for two. On a phone the modal is ~310px wide, so
              a fixed 180px cover beside everything else leaves the title, the badges and the add
              form fighting over ~130px, and the title clips. Below `xs` the cover goes on top,
              centred, and the rest gets the full width. */}
          <Flex direction={{ base: 'column', xs: 'row' }} align="flex-start" gap="lg">
            {cover ? (
              <Image
                src={cover}
                w={180}
                h={270}
                radius="md"
                fit="cover"
                alt=""
                mx={{ base: 'auto', xs: 0 }}
                style={{ flexShrink: 0 }}
              />
            ) : (
              <Skeleton
                w={180}
                h={270}
                radius="md"
                mx={{ base: 'auto', xs: 0 }}
                style={{ flexShrink: 0 }}
              />
            )}

            <Stack gap="xs" w={{ base: '100%', xs: 'auto' }} style={{ flex: 1, minWidth: 0 }}>
              <div>
                <Title order={3} lh={1.2}>
                  {title}
                </Title>
                {(detail?.nativeTitle || detail?.romanizedTitle) && (
                  <Text size="sm" c="dimmed">
                    {[detail?.romanizedTitle, detail?.nativeTitle].filter(Boolean).join(' · ')}
                  </Text>
                )}
                {detail?.altTitles && detail.altTitles.length > 0 && (
                  <Group gap={6}>
                    {detail.altTitles.map((t, i) => (
                      <Text key={t} c="dimmed" size="xs">
                        {t}
                        {i < detail.altTitles.length - 1 ? ',' : ''}
                      </Text>
                    ))}
                  </Group>
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
                {detail?.hasAnime && (
                  <Badge leftSection={<IconDeviceTv size={12} />}>
                    Anime
                  </Badge>
              )}
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

              {/* The hint keeps its own baseline (the reader crowd's own mean), so it can be worth
                  showing on a series the catalogue never rated. */}
              {((detail?.rating ?? item.rating) != null || detail?.readerHint) && (
                <Group gap="xs" align="center">
                  {(detail?.rating ?? item.rating) != null && (
                    <Badge
                      size="lg"
                      color={ratingColor(detail?.rating ?? item.rating ?? 0)}
                      leftSection={<IconStar size={13} />}
                    >
                      {((detail?.rating ?? item.rating ?? 0) / 10).toFixed(1)}
                    </Badge>
                  )}
                  {/* Only ever rendered when the server decided there is something to say, which
                      is about one series in nine: the cohorts have to disagree with the wider
                      reader crowd by at least half a star. Deliberately a direction rather than a
                      second number - measured, a cohort score shown on every series renders the
                      same digits as the aggregate beside it nine times out of ten. */}
                  {detail?.readerHint && (
                    <Tooltip
                      withArrow
                      multiline
                      w={260}
                      zIndex={1001}
                      label={`${(detail.readerHint.score / 10).toFixed(1)} from ${detail.readerHint.readers.toLocaleString()} readers with reading habits like yours, against ${(detail.readerHint.baseline / 10).toFixed(1)} from readers overall.`}
                    >
                      <Badge
                        size="sm"
                        variant="light"
                        color={detail.readerHint.score > detail.readerHint.baseline ? 'teal' : 'orange'}
                        leftSection={
                          detail.readerHint.score > detail.readerHint.baseline ? (
                            <IconTrendingUp size={12} />
                          ) : (
                            <IconTrendingDown size={12} />
                          )
                        }
                      >
                        {detail.readerHint.score > detail.readerHint.baseline
                          ? 'Higher for readers like you'
                          : 'Lower for readers like you'}
                      </Badge>
                    </Tooltip>
                  )}
                  {detail?.sourceRatings.map((r) => (
                    <Tooltip key={r.source} label={r.source} withArrow zIndex={1001}>
                      <Badge
                        size="sm"
                        variant="outline"
                        color="gray"
                        leftSection={
                          <MetadataSiteIcon
                            site={r.source.toLowerCase()}
                            monogram={r.source.slice(0, 2).toUpperCase()}
                            size={11}
                          />
                        }
                      >
                        {(r.rating / 10).toFixed(1)}
                      </Badge>
                    </Tooltip>
                  ))}
                </Group>
              )}

              <MetadataLinks links={detail?.links ?? []} />

              {seriesId != null ? (
                <Group gap="sm" mt="xs">
                  <Button
                    color="teal"
                    variant="light"
                    leftSection={<IconArrowRight size={16} />}
                    onClick={goToLibrary}
                  >
                    {addedSeriesId != null ? 'Go to series' : 'View in library'}
                  </Button>
                </Group>
              ) : canAdd && !can('Admin') && (rootFolders?.length ?? 0) === 0 ? (
                // AddSeries lets someone create a series, but the root folder list is admin-only
                // (it discloses the host's directory layout), so a non-admin has nothing to point
                // the add at. Say so rather than leaving a dead Select and a disabled button.
                <Alert color="yellow" variant="light" mt="xs">
                  You can add series, but only an admin can choose a root folder. Ask one to add
                  this title, or to grant you admin.
                </Alert>
              ) : canAdd ? (
                <Group gap="sm" mt="xs">
                  {/* A root folder is an absolute host path, so it needs the room it can get:
                      full width on a phone rather than 200px of truncated prefix. */}
                  <Select
                    placeholder="Root folder"
                    data={rootFolders?.map((f) => ({ value: String(f.id), label: f.path })) ?? []}
                    value={rootFolderId}
                    onChange={setRootFolderId}
                    size="sm"
                    w={{ base: '100%', xs: 200 }}
                    comboboxProps={{ zIndex: 1001 }}
                  />
                  <Switch
                    label="Monitor"
                    checked={monitored}
                    onChange={(e) => setMonitored(e.currentTarget.checked)}
                  />
                  {/* Pre-filled from the content-rating rules, so an explicit pick here is the
                      exception rather than something to remember on every add. */}
                  <Tooltip
                    label="Keeps this series out of tracker pushes, and out of stats entirely on Full."
                    withArrow
                    zIndex={1001}
                  >
                    <Select
                      aria-label="Incognito"
                      data={INCOGNITO_OPTIONS.map((o) => ({
                        value: o.value,
                        label: `Incognito: ${o.label.toLowerCase()}`,
                      }))}
                      value={incognito ?? 'Off'}
                      onChange={(value) => {
                        setIncognitoPinned(true)
                        setIncognito((value as IncognitoMode | null) ?? 'Off')
                      }}
                      size="sm"
                      w={{ base: '100%', xs: 170 }}
                      comboboxProps={{ zIndex: 1001 }}
                    />
                  </Tooltip>
                  <Button
                    leftSection={<IconPlus size={16} />}
                    onClick={add}
                    loading={addSeries.isPending}
                    disabled={!rootFolderId}
                  >
                    Add
                  </Button>
                </Group>
              ) : requested ? (
                <Alert color="green" variant="light" icon={<IconCheck size={16} />} mt="xs">
                  Requested. An admin decides where it lands and what gets downloaded.
                </Alert>
              ) : (
                <RequestForm
                  chapterStart={chapterStart}
                  chapterEnd={chapterEnd}
                  note={note}
                  onChapterStart={setChapterStart}
                  onChapterEnd={setChapterEnd}
                  onNote={setNote}
                  onSubmit={request}
                  pending={createRequest.isPending}
                />
              )}
            </Stack>
          </Flex>

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

           {detail?.animeStart && (
              <Text size="sm" c="dimmed">
                Anime aired from{' '}
                <Text span fw={600} c="gray.3" className="tnum">
                  {detail?.animeStart}
                </Text>
              </Text>
            )}
            {detail?.animeEnd && (
              <Text size="sm" c="dimmed">
                Anime aired until{' '}
                <Text span fw={600} c="gray.3" className="tnum">
                  {detail?.animeEnd}
                </Text>
              </Text>
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
                            <Badge
                              variant="light"
                              color={bucket.color}
                              className={t.isSpoiler ? 'spoiler-tag' : undefined}
                              tabIndex={t.isSpoiler ? 0 : undefined}
                            >
                              {t.name}
                            </Badge>
                          )
                          // Spoiler tags always get a tooltip hint; others only when described.
                          const tip = t.isSpoiler
                            ? t.description
                              ? `Spoiler · ${t.description}`
                              : 'Spoiler - hover to reveal'
                            : t.description
                          return tip ? (
                            <Tooltip
                              key={t.name}
                              label={tip}
                              withArrow
                              multiline
                              maw={320}
                              openDelay={200}
                              zIndex={1001}
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
                <Credit label="Story" role="author" values={detail.authors} onNavigate={onClose} />
              )}
              {detail.artists.length > 0 && (
                <Credit label="Art" role="artist" values={detail.artists} onNavigate={onClose} />
              )}
              {detail.publishers.length > 0 && (
                <Credit
                  label="Publishers"
                  role="studio"
                  values={detail.publishers}
                  onNavigate={onClose}
                />
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
              {!reviewsLoading && reviews === null && (
                <Text size="sm" c="dimmed" ta="center">
                  Reviews are temporarily unavailable: MyAnimeList didn't respond. Try again
                  later.
                </Text>
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

          {createRequest.isError && (
            <Alert color="red" variant="light">
              {String(createRequest.error)}
            </Alert>
          )}
        </Stack>
      )}
    </Modal>
  )
}

/**
 * A credit line whose names link through to everything that person or studio made.
 *
 * <p>
 * Navigating closes this modal first. On Discover it can already be the second layer, opened from
 * behind `FeedExpandModal` and carrying an explicit zIndex to survive that; a creator view stacked
 * on top would be a third. A route is also linkable, which a modal is not, and Back brings the
 * results underneath straight out of the query cache.
 * </p>
 */
function Credit({
  label,
  role,
  values,
  onNavigate,
}: {
  label: string
  role: 'author' | 'artist' | 'studio'
  values: string[]
  onNavigate: () => void
}) {
  return (
    <div>
      <Text size="xs" fw={700} c="dimmed" tt="uppercase" mb={2}>
        {label}
      </Text>
      <Text size="sm">
        {values.map((value, i) => (
          <span key={value}>
            {i > 0 && ', '}
            <Anchor
              component={Link}
              to={`/creator/${encodeURIComponent(value)}?role=${role}`}
              onClick={onNavigate}
              inherit
            >
              {value}
            </Anchor>
          </span>
        ))}
      </Text>
    </div>
  )
}
