import type { CSSProperties } from 'react'
import {
  Badge,
  Box,
  CloseButton, Divider,
  Group,
  Modal,
  Paper,
  Skeleton,
  Spoiler,
  Stack,
  Text,
  Title,
  Tooltip,
} from '@mantine/core'
import {
  IconStar,
  IconTrendingDown,
  IconTrendingUp,
} from '@tabler/icons-react'
import {
  useRecommendationDetail,
  type RecommendationItem,
} from '../../api/hooks'
import type { RootFolder } from '../../api/types'
import { HeroBackdrop } from '../series/HeroBackdrop'
import { MetadataLinks } from '../MetadataLinks'
import { MetadataSiteIcon } from '../MetadataSiteIcon'
import {
  contentRatingToken,
  contentRatingVisual,
  ratingBandVisual,
  seriesStatusVisual,
  statusToken,
} from '../ui/status'
import { DiscoverGlance } from './DiscoverGlance'
import { DiscoverLibraryRail } from './DiscoverLibraryRail'
import { DiscoverReviews } from './DiscoverReviews'
import { DiscoverTags } from './DiscoverTags'

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
  const { data: detail, isLoading } = useRecommendationDetail(item?.providerId ?? null)

  const title = detail?.title ?? item?.title ?? ''
  // The card's 334x500 thumbnail stands in until the detail row's full-size art arrives: it is
  // already in the browser's image cache, so the modal opens with a cover rather than a hole.
  const cover = detail?.coverUrl ?? item?.thumbUrlHiDpi ?? item?.coverUrl ?? null
  const genres = detail?.genres ?? item?.matchedGenres ?? []

  // Every one of these is on the card's own row as well as the detail response, so the band is
  // complete from the first frame and the detail request fills in rather than rearranges.
  const status = seriesStatusVisual(detail?.status ?? item?.status ?? '')
  const contentRating = contentRatingVisual(detail?.contentRating ?? null)
  const ratingToken = contentRatingToken(detail?.contentRating)
  const score = detail?.rating ?? item?.rating ?? null
  const band = ratingBandVisual(score ?? 0)
  const figures = [
    { label: 'Released', value: detail?.year ?? item?.year },
    { label: 'Chapters', value: detail?.totalChapters ?? item?.totalChapters },
    // Only ever set on a series that has finished a volume run, so an ongoing web series shows two
    // figures rather than a third reading "0".
    { label: 'Volumes', value: detail?.finalVolume },
  ].filter((f): f is { label: string; value: number } => f.value != null)
  const facts = [
    detail?.type,
    detail?.hasAnime ? 'Anime adaptation' : null,
    detail?.genres.slice(0, 5).join(', ') || null,
  ].filter(Boolean)

  return (
    // Explicit zIndex: Discover's fullscreen "Show more" modal (FeedExpandModal) can open this
    // one from a card click inside it, and both default to the same Mantine modal z-index, so
    // whichever mounted first would otherwise win and this modal opened from behind it.
    //
    // No padding and no Mantine close button: the hero band bleeds to the modal's own edges, and a
    // header row above it would push the art down and put a hairline across the top of the card.
    <Modal
      opened={item !== null}
      onClose={onClose}
      // A width, not a Mantine size step: the two-column body wants ~1180px, and the calc keeps it
      // off the edges of a laptop screen rather than relying on the modal's own max-width.
      size="min(1180px, calc(100vw - 3rem))"
      radius="lg"
      title={null}
      padding={0}
      withCloseButton={false}
      zIndex={1000}
      // Mantine reserves 5vh above and below by default, and caps the card at what is left. Two
      // gives the modal most of the screen and still keeps it a card rather than a page.
      yOffset="2vh"
      // A flex column all the way down, so the body takes whatever height the band leaves instead
      // of a stylesheet guessing at the band's height. `max-height` rather than `height`: a series
      // with a short synopsis and no reviews still gets a card its own size.
      styles={{
        content: { maxHeight: 'min(94dvh, 1200px)', display: 'flex', flexDirection: 'column' },
        body: { flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' },
      }}
    >
      {item === null ? null : (
        // The class carries the positioning the floating close button needs. Mantine's own content
        // element is not positioned, so without it the button anchors to the viewport and lands in
        // the top-right corner of the screen rather than of the card.
        <div className="discover-modal-root">
          <CloseButton
            className="discover-modal-close"
            size="lg"
            aria-label="Close"
            onClick={onClose}
          />

          <Box className="series-hero" data-compact>
            <HeroBackdrop coverUrl={cover} />

            <div className="series-hero-body">
              {/* Identity against the one thing there is to do with it, the way the series page
                  puts Progress beside its own title block. */}
              <div className="series-hero-content">
                <Group align="flex-start" gap={26} wrap="nowrap" className="series-hero-row">
                  {cover ? (
                    <img className="series-hero-poster" src={cover} alt="" />
                  ) : (
                    // Sized here rather than through `.series-hero-poster`: Skeleton drives its own
                    // height from a CSS variable at the same specificity, so which one wins would
                    // come down to stylesheet order. The class only carries the phone rule that
                    // takes the poster slot out entirely.
                    <Skeleton
                      className="discover-poster-skeleton"
                      w={176}
                      h={264}
                      radius={11}
                      style={{ flexShrink: 0 }}
                    />
                  )}

                  <Stack gap={0} style={{ flex: 1, minWidth: 0 }}>
                    <Title order={1} className="series-hero-title">
                      {title}
                    </Title>

                    {(detail?.nativeTitle || detail?.romanizedTitle) && (
                      <Text size="sm" pt="xs" c="var(--ink-3)">
                        {[detail?.romanizedTitle, detail?.nativeTitle].filter(Boolean).join(' · ')}
                      </Text>
                    )}
                    {detail?.altTitles && detail.altTitles.length > 0 && (
                      <Text size="xs" c="var(--ink-4)" mt={4} lineClamp={2}>
                        {detail.altTitles.join(', ')}
                      </Text>
                    )}

                    <Group gap={9} mt={16} wrap="wrap">
                      {/* Same pills as the series page's band, from the same two maps: a result and
                          the series it becomes have to read as one object, not two vocabularies. */}
                      <span
                        className="series-hero-status"
                        style={{
                          color: `var(--${statusToken(status.color)})`,
                          background: `var(--${statusToken(status.color)}-soft)`,
                        }}
                      >
                        <status.Icon size={14} />
                        {status.label}
                      </span>
                      {contentRating && (
                          <Tooltip label="Content rating" withArrow zIndex={1001}>
                          <span
                              className="series-hero-status"
                              data-quiet={ratingToken ? undefined : true}
                              style={
                                ratingToken
                                    ? {
                                      color: `var(--${ratingToken})`,
                                      background: `var(--${ratingToken}-soft)`,
                                    }
                                    : undefined
                              }
                          >
                            <contentRating.Icon size={14} />
                            {contentRating.label}
                          </span>
                          </Tooltip>
                      )}
                    </Group>

                    {/* The numbers people open a card to check. Every one of them is on the item the
                        card was built from, so they are set at this size from the first frame rather
                        than arriving with the detail request and shoving the row about. */}
                    <div className="hero-figures">
                      {score != null && (
                        <Tooltip label="MangaBaka aggregate score" withArrow zIndex={1001}>
                          <span
                            className="hero-score"
                            style={{ '--band': `var(--${band.token})` } as CSSProperties}
                          >
                            <IconStar size={18} />
                            <span className="hero-score-n tnum">{(score / 10).toFixed(1)}</span>
                          </span>
                        </Tooltip>
                      )}
                      {score != null && figures.length > 0 && (
                        <span className="hero-figure-rule" aria-hidden />
                      )}
                      {figures.length > 0 && (
                        <div className="hero-stats">
                          {figures.map((f) => (
                            <div key={f.label} className="hero-stat">
                              <span className="hero-stat-n tnum">{f.value}</span>
                              <span className="hero-stat-l">{f.label}</span>
                            </div>
                          ))}
                        </div>
                      )}
                    </div>

                    {/* The other sites' scores are a set under the headline number, not four more
                        headline numbers. */}
                    {(detail?.readerHint || (detail?.sourceRatings.length ?? 0) > 0) && (
                      <Group gap="xs" align="center" mt={14}>
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
                              color={
                                detail.readerHint.score > detail.readerHint.baseline
                                  ? 'teal'
                                  : 'orange'
                              }
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

                    {facts.length > 0 && (
                        <Text size="sm" c="var(--ink-4)" mt={9}>
                          {facts.join(' · ')}
                        </Text>
                    )}

                    <Box mt="sm">
                      <MetadataLinks links={detail?.links ?? []} />
                    </Box>
                  </Stack>
                </Group>

                {/* Keyed by provider id: a half-filled request belongs to the series it was started
                    for, and remounting is a cheaper reset than clearing six fields. */}
                <DiscoverLibraryRail
                  key={item.providerId}
                  item={item}
                  detail={detail}
                  inLibrarySeriesId={inLibrarySeriesId}
                  rootFolders={rootFolders}
                  onClose={onClose}
                />
              </div>

            </div>
          </Box>

          <div className="discover-body">
          <div className="detail-split">
              <div className="detail-main">
                <Paper withBorder radius="lg" p="lg">
                  <Stack gap="md">
                    {isLoading && !detail && (
                      <Stack gap="xs">
                        <Skeleton h={12} />
                        <Skeleton h={12} />
                        <Skeleton h={12} w="70%" />
                      </Stack>
                    )}

                    <Title order={3} fz={17}>
                      Synopsis
                    </Title>

                    {(detail?.description || item.description) && (
                      <Spoiler maxHeight={120} showLabel="Show more" hideLabel="Show less">
                        <Text size="sm" c="var(--ink-3)" style={{ whiteSpace: 'pre-line', lineHeight: 1.66 }}>
                          {detail?.description ?? item.description}
                        </Text>
                      </Spoiler>
                    )}
                    {/* Said out loud rather than left as an empty panel: a series with no
                        synopsis, no anime dates and no genres would otherwise open on a bordered
                        box with nothing in it. */}
                    {detail && !detail.description && !item.description && (
                      <Text size="sm" c="var(--ink-4)">
                        The catalogue has no synopsis for this one.
                      </Text>
                    )}

                    {detail?.animeStart && (
                        <>
                          <Divider color="var(--hairline)"/>
                          <Title order={4} fz={14}>
                            Anime coverage
                          </Title>
                        
                          <Text size="sm" c="dimmed">
                            Anime aired from{' '}
                            <Text span fw={600} c="gray.3" className="tnum">
                              {detail?.animeStart}
                            </Text>
                          </Text>
                        </>
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
                        <Divider mb="md" color="var(--hairline)"/>
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

                  </Stack>
                </Paper>

                {detail && detail.tags.length > 0 && <DiscoverTags tags={detail.tags} />}
              </div>

              {/* The column beside the reading one: what other readers made of it, then the facts
                  about it. Mounted only once there is something to put in it, which is what lets
                  `.detail-split` drop the track and give the synopsis the whole width. */}
              {detail && (
                <div className="detail-aside">
                  <DiscoverReviews malId={detail.malId} />
                  <DiscoverGlance detail={detail} onNavigate={onClose} />
                </div>
              )}
            </div>
          </div>
        </div>
      )}
    </Modal>
  )
}
