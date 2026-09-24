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
import { Trans, useLingui } from '@lingui/react/macro'
import {
  useRecommendationDetail,
  type RecommendationItem,
} from '../../api/hooks'
import { altTitleLabel, readableTitles } from '../../api/titles'
import type { RootFolder } from '../../api/types'
import { formatNumber } from '../../format'
import { AnimeCoverageBar } from '../AnimeCoverageBar'
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
import { TagChip, TagChips } from '../ui/TagChip'
import { DiscoverGlance } from './DiscoverGlance'
import { DiscoverLibraryRail } from './DiscoverLibraryRail'
import { DiscoverReviews } from './DiscoverReviews'
import { PlanToReadButton } from './PlanToReadButton'
import { RecommendationFeedbackMenu } from './RecommendationFeedbackMenu'
import { DiscoverTags } from './DiscoverTags'
import { useLabel } from '../../i18n-context'
import { GENRE_LABELS, TYPE_LABELS } from '../CatalogueFilters'

export function DiscoverDetailModal({
  item,
  inLibrarySeriesId,
  rootFolders,
  onClose,
  feedbackContext,
}: {
  /** The card that was clicked; null closes the modal. Used for an instant header while detail loads. */
  item: RecommendationItem | null
  /** Library series id if already owned (enables "View in library"); null/undefined otherwise. */
  inLibrarySeriesId: number | null | undefined
  rootFolders: RootFolder[] | undefined
  onClose: () => void
  feedbackContext?: { surface: string }
}) {
  const { data: detail, isLoading } = useRecommendationDetail(item?.providerId ?? null)

  const title = detail?.title ?? item?.title ?? ''
  // The card's 334x500 thumbnail stands in until the detail row's full-size art arrives: it is
  // already in the browser's image cache, so the modal opens with a cover rather than a hole.
  const cover = detail?.coverUrl ?? item?.thumbUrlHiDpi ?? item?.coverUrl ?? null
  const genres = detail?.genres ?? item?.matchedGenres ?? []

  // Every one of these is on the card's own row as well as the detail response, so the band is
  // complete from the first frame and the detail request fills in rather than rearranges.
  const renderLabel = useLabel()
  const { t, i18n } = useLingui()
  const status = seriesStatusVisual(detail?.status ?? item?.status ?? '')
  const contentRating = contentRatingVisual(detail?.contentRating ?? null)
  const ratingToken = contentRatingToken(detail?.contentRating)
  const score = detail?.rating ?? item?.rating ?? null
  const band = ratingBandVisual(score ?? 0)
  const altTitles = readableTitles(detail?.altTitles ?? [], i18n.locale)
  // `id` is a stable key: `label` is translated text, and keying the row off it would remount the
  // whole figures block on a language switch.
  const figures = [
    { id: 'released', label: t`Released`, value: detail?.year ?? item?.year },
    { id: 'chapters', label: t`Chapters`, value: detail?.totalChapters ?? item?.totalChapters },
    // Only ever set on a series that has finished a volume run, so an ongoing web series shows two
    // figures rather than a third reading "0".
    { id: 'volumes', label: t`Volumes`, value: detail?.finalVolume },
  ].filter((f): f is { id: string; label: string; value: number } => f.value != null)
  // The wire value picks the label and an unknown one falls through as itself, which is what
  // `useLabel` does with a plain string.
  const genreLabel = (g: string) => renderLabel(GENRE_LABELS[g] ?? g)
  const facts = [
    detail?.type ? renderLabel(TYPE_LABELS[detail.type] ?? detail.type) : null,
    detail?.hasAnime ? t`Anime adaptation` : null,
    detail?.genres.slice(0, 5).map(genreLabel).join(', ') || null,
  ].filter(Boolean)

  // Named locals for the tooltip sentence below: Lingui only names a placeholder after the
  // expression when it is a plain identifier, so a member access or a `.toFixed()` call would
  // otherwise extract as an unlabelled {0}.
  const readerHint = detail?.readerHint ?? null
  const readerHintScoreDisplay = readerHint ? (readerHint.score / 10).toFixed(1) : null
  const readerHintReadersDisplay = readerHint ? formatNumber(readerHint.readers) : null
  const readerHintBaselineDisplay = readerHint ? (readerHint.baseline / 10).toFixed(1) : null
  const readerHintHigher = readerHint ? readerHint.score > readerHint.baseline : false

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
        content: { maxHeight: 'min(94dvh, 1200px)' },
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
            aria-label={t`Close`}
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
                      radius="var(--radius-hero)"
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
                    {altTitles.length > 0 && (
                      <Text size="xs" c="var(--ink-4)" mt={4} lineClamp={2}>
                        {altTitles.map(altTitleLabel).join(', ')}
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
                        {renderLabel(status.label)}
                      </span>
                      {contentRating && (
                          <Tooltip label={t`Content rating`} withArrow zIndex={1001}>
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
                            {renderLabel(contentRating.label)}
                          </span>
                          </Tooltip>
                      )}
                    </Group>

                    {/* The numbers people open a card to check. Every one of them is on the item the
                        card was built from, so they are set at this size from the first frame rather
                        than arriving with the detail request and shoving the row about. */}
                    <div className="hero-figures">
                      {score != null && (
                        <Tooltip label={t`MangaBaka aggregate score`} withArrow zIndex={1001}>
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
                            <div key={f.id} className="hero-stat">
                              <span className="hero-stat-n tnum">{f.value}</span>
                              <span className="hero-stat-l">{f.label}</span>
                            </div>
                          ))}
                        </div>
                      )}
                    </div>

                    {/* The other sites' scores are a set under the headline number, not four more
                        headline numbers. */}
                    {(readerHint || (detail?.sourceRatings.length ?? 0) > 0) && (
                      <Group gap="xs" align="center" mt={14}>
                        {/* Only ever rendered when the server decided there is something to say, which
                            is about one series in nine: the cohorts have to disagree with the wider
                            reader crowd by at least half a star. Deliberately a direction rather than a
                            second number - measured, a cohort score shown on every series renders the
                            same digits as the aggregate beside it nine times out of ten. */}
                        {readerHint && (
                          <Tooltip
                            withArrow
                            multiline
                            w={260}
                            zIndex={1001}
                            label={t`${readerHintScoreDisplay} from ${readerHintReadersDisplay} readers with reading habits like yours, against ${readerHintBaselineDisplay} from readers overall.`}
                          >
                            <Badge
                              size="sm"
                              variant="light"
                              color={readerHintHigher ? 'var(--ok)' : 'var(--warn)'}
                              leftSection={
                                readerHintHigher ? (
                                  <IconTrendingUp size={12} />
                                ) : (
                                  <IconTrendingDown size={12} />
                                )
                              }
                            >
                              {readerHintHigher ? (
                                <Trans>Higher for readers like you</Trans>
                              ) : (
                                <Trans>Lower for readers like you</Trans>
                              )}
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

                {/* One grid cell, not two. `.series-hero-content` is a two-column grid, so a
                    feedback control rendered as its own child became a third item and wrapped onto a
                    row of its own under the poster. It belongs under the add panel it relates to.

                    `alignSelf: end` because the grid sets `align-items: start`: the cell hugs its
                    content, so without it the panel rides at the top of the band and leaves the gap
                    underneath. Bottom is where the panel sat before the feedback row joined it. */}
                <Stack gap="sm" style={{ minWidth: 0, alignSelf: 'end' }}>
                  {/* Keyed by provider id: a half-filled request belongs to the series it was started
                      for, and remounting is a cheaper reset than clearing six fields. */}
                  <DiscoverLibraryRail
                    key={item.providerId}
                    item={item}
                    detail={detail}
                    inLibrarySeriesId={inLibrarySeriesId}
                    rootFolders={rootFolders}
                    onClose={onClose}
                    addedFrom={feedbackContext ? 'recommendation' : 'library'}
                  />
                  {(feedbackContext || !inLibrarySeriesId) && (
                    <Group gap="xs" justify="center" wrap="nowrap">
                      {!inLibrarySeriesId && <PlanToReadButton item={item} />}
                      {feedbackContext && (
                        <RecommendationFeedbackMenu
                          providerId={item.providerId}
                          surface={feedbackContext.surface}
                        />
                      )}
                    </Group>
                  )}
                </Stack>
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
                      <Trans>Synopsis</Trans>
                    </Title>

                    {(detail?.description || item.description) && (
                      <Spoiler maxHeight={120} showLabel={t`Show more`} hideLabel={t`Show less`}>
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
                        <Trans>The catalogue has no synopsis for this one.</Trans>
                      </Text>
                    )}

                    {(detail?.animeStart || detail?.animeEnd) && (
                        <>
                          <Divider color="var(--hairline)"/>
                          <Title order={4} fz={14}>
                            <Trans>Anime coverage</Trans>
                          </Title>
                          <AnimeCoverageBar
                              start={detail.animeStart}
                              end={detail.animeEnd}
                              totalChapters={detail.totalChapters}
                              tooltipZIndex={1001}
                          />
                        </>
                    )}

                    {genres.length > 0 && (
                      <div>
                        <Divider mb="md" color="var(--hairline)"/>
                        <Text size="xs" fw={700} c="var(--ink-3)" tt="uppercase" mb={6}>
                          <Trans>Genres</Trans>
                        </Text>
                        <TagChips>
                          {genres.map((g) => (
                            <TagChip key={g} dot="var(--info)">
                              {genreLabel(g)}
                            </TagChip>
                          ))}
                        </TagChips>
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
