import { useEffect, useRef, useState, type HTMLAttributes, type Ref } from 'react'
import {
  ActionIcon,
  Badge,
  Box,
  Button,
  CloseButton, Divider,
  Group,
  Modal,
  Skeleton,
  Spoiler,
  Stack,
  Text,
  Title,
  Tooltip,
} from '@mantine/core'
import {
  IconBooks,
  IconDeviceTv,
  IconFileText,
  IconStarFilled,
  IconTrendingDown,
  IconTrendingUp,
} from '../../lib/icons'
import { Trans, useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'
import {
  useReadingBehaviour,
  useRecommendationDetail,
  type RecommendationItem,
} from '../../api/hooks'
import type { MetadataLink, RootFolder } from '../../api/types'
import { formatFixedDecimal } from '../../format'
import { AnimeCoverageBar } from '../AnimeCoverageBar'
import { AnimeResumeCallout } from '../series/AnimeResumeCallout'
import { HeroBackdrop } from '../series/HeroBackdrop'
import { MetadataLinks } from '../MetadataLinks'
import { MetadataSiteIcon } from '../MetadataSiteIcon'
import {
  seriesStatusVisual,
  statusToken,
} from '../ui/status'
import { ContentRatingChip } from '../ContentRatingChip'
import { Panel } from '../ui/Panel'
import { PanelTitle } from '../ui/PanelTitle'
import { DiscoverGlance } from './DiscoverGlance'
import { DiscoverLibraryRail } from './DiscoverLibraryRail'
import { DiscoverReviews } from './DiscoverReviews'
import { RecommendationFeedbackMenu } from './RecommendationFeedbackMenu'
import { PreviewChapterButton } from './PreviewChapterButton'
import { ShortlistButton, ShortlistNote } from './ShortlistButton'
import { useIsShortlisted, type ShortlistSource } from '../../api/shortlist'
import { SeriesPreviewReader } from './SeriesPreviewReader'
import { DiscoverTags } from './DiscoverTags'
import { DiceIcon } from '../LuckyButton'
import { prefersReducedMotion, useDiceTumble } from '../../lib/lucky'
import { cleanSynopsis } from '../../lib/synopsis'
import { useLabel } from '../../i18n-context'
import { GENRE_LABELS, TYPE_LABELS } from '../CatalogueFilters'
import { MetaList } from '../ui/MetaList'
import { TagChip, TagChips } from '../ui/TagChip'
import { useModalHistory } from '../../lib/useModalHistory'

/** The collapsed bar's height; also how far above the top edge the add card must go to bring it in. */
const BAR_HEIGHT = 64

export function DiscoverDetailModal({
  item,
  inLibrarySeriesId,
  rootFolders,
  onClose,
  feedbackContext,
  onReroll,
  savedFrom,
}: {
  /** The card that was clicked; null closes the modal. Used for an instant header while detail loads. */
  item: RecommendationItem | null
  /** Library series id if already owned (enables "View in library"); null/undefined otherwise. */
  inLibrarySeriesId: number | null | undefined
  rootFolders: RootFolder[] | undefined
  onClose: () => void
  /** Set by recommendation surfaces, so an add from here is recorded as a recommended pick. */
  feedbackContext?: { surface: string }
  /** Shows a dice beside the close button that swaps in another random pick. */
  onReroll?: () => void
  /** Where the card was opened from, kept with the title when it is shortlisted. Saves as "other" without it. */
  savedFrom?: ShortlistSource
}) {
  const shortlisted = useIsShortlisted(item?.providerId ?? '')
  useModalHistory(item !== null, onClose)
  const { data: detail, isLoading, isError, refetch } = useRecommendationDetail(item?.providerId ?? null, true)
  const { scope: diceScope, tumble } = useDiceTumble()
  const rerolling = useRef(false)
  const rerollTimer = useRef<number | undefined>(undefined)
  const open = item != null
  const [previewFor, setPreviewFor] = useState<string | null>(null)
  const previewing = previewFor != null && previewFor === item?.providerId

  // The band scrolls away with the body, and once the add card has gone under the top edge a slim
  // bar slides in with the title and the add button, so the one thing to do stays in reach.
  const [scroller, setScroller] = useState<HTMLDivElement | null>(null)
  const [actionCard, setActionCard] = useState<HTMLDivElement | null>(null)
  const [barSlot, setBarSlot] = useState<HTMLDivElement | null>(null)
  const [barShown, setBarShown] = useState(false)
  useEffect(() => {
    if (!scroller || !actionCard) return
    // A desktop card scrolls this element; on a phone it lets go and the modal scrolls band and all,
    // so the observer watches whichever ancestor is doing the scrolling.
    let root: HTMLElement | null = scroller
    while (root && !/(auto|scroll)/.test(getComputedStyle(root).overflowY)) root = root.parentElement
    const observer = new IntersectionObserver(
      ([entry]) => {
        const top = entry.rootBounds?.top ?? 0
        setBarShown(!entry.isIntersecting && entry.boundingClientRect.bottom <= top + 1)
      },
      { root, rootMargin: `-${BAR_HEIGHT}px 0px 0px 0px` },
    )
    observer.observe(actionCard)
    return () => observer.disconnect()
  }, [scroller, actionCard])
  // A reroll swaps the series in place, so it starts again from the band.
  const shownId = item?.providerId
  useEffect(() => {
    scroller?.scrollTo({ top: 0 })
    setBarShown(false)
  }, [shownId, scroller])
  useEffect(() => {
    if (open) return
    window.clearTimeout(rerollTimer.current)
    rerolling.current = false
  }, [open])
  useEffect(() => () => window.clearTimeout(rerollTimer.current), [])
  const reroll = () => {
    if (!onReroll || rerolling.current) return
    if (prefersReducedMotion()) {
      onReroll()
      return
    }
    rerolling.current = true
    void tumble(500)
    rerollTimer.current = window.setTimeout(() => {
      rerolling.current = false
      onReroll()
    }, 500)
  }

  const title = detail?.title ?? item?.title ?? ''
  // The card's 334x500 thumbnail stands in until the detail row's full-size art arrives: it is
  // already in the browser's image cache, so the modal opens with a cover rather than a hole.
  const cover = detail?.coverUrl ?? item?.thumbUrlHiDpi ?? item?.coverUrl ?? null

  // Every one of these is on the card's own row as well as the detail response, so the band is
  // complete from the first frame and the detail request fills in rather than rearranges.
  const renderLabel = useLabel()
  const { t } = useLingui()
  const status = seriesStatusVisual(detail?.status ?? item?.status ?? '')
  const score = detail?.rating ?? item?.rating ?? null
  const year = detail?.year ?? item?.year ?? null
  const chapters = detail?.totalChapters ?? item?.totalChapters ?? null
  // Only ever set on a series that has finished a volume run, so an ongoing web series never
  // reads "in 0 volumes".
  const volumes = detail?.finalVolume ?? null
  // The reader's own median, so the estimate is theirs rather than a site average. Null until the
  // native reader has timed enough chapters, and then the figure stays out rather than guessing.
  const { data: behaviour } = useReadingBehaviour()
  const pace = behaviour?.medianSecondsPerChapter ?? null
  const catchUpMinutes = pace != null && chapters ? Math.max(1, Math.round((pace * chapters) / 60)) : null
  const paceMinutes = pace != null ? Math.max(1, Math.round(pace / 60)) : 0
  // The wire value picks the label and an unknown one falls through as itself, which is what
  // `useLabel` does with a plain string.
  const genreLabel = (g: string) => renderLabel(GENRE_LABELS[g] ?? g)
  const typeLabel = detail?.type ? renderLabel(TYPE_LABELS[detail.type] ?? detail.type) : null
  const titleLength = title.length > 70 ? 'long' : title.length > 36 ? 'medium' : undefined

  // Each site's score rides on its own link mark, so the ratings and the places they come from
  // are one row. A rated site with no link (Kitsu has no id in the dump) keeps a plain mark.
  const ratingBySite = new Map((detail?.sourceRatings ?? []).map((r) => [r.source.toLowerCase(), r.rating]))
  const links: MetadataLink[] = (detail?.links ?? []).map((link) => {
    const rating = ratingBySite.get(link.site)
    return rating != null && link.score == null ? { ...link, score: formatFixedDecimal(rating / 10, 1) } : link
  })
  const unlinkedRatings = (detail?.sourceRatings ?? []).filter(
    (r) => !links.some((link) => link.site === r.source.toLowerCase()),
  )

  // Named locals for the tooltip sentence below: Lingui only names a placeholder after the
  // expression when it is a plain identifier, so a member access or a formatter call would
  // otherwise extract as an unlabelled {0}.
  const readerHint = detail?.readerHint ?? null
  const readerHintReaders = readerHint?.readers ?? 0
  // A gap, never two absolute scores: the baseline is the AniList sample's mean, not the MangaBaka
  // aggregate in the star, so printing it read as the card contradicting itself.
  const readerHintGap = readerHint ? formatFixedDecimal(Math.abs(readerHint.score - readerHint.baseline) / 10, 1) : null
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
      // The preview reader portals out of the card, so while it is up the card must neither take
      // Escape for itself nor pull focus back from the reader's controls.
      closeOnEscape={!previewing}
      trapFocus={!previewing}
      // A width, not a Mantine size step: the two-column body wants ~1180px, and the calc keeps it
      // off the edges of a laptop screen rather than relying on the modal's own max-width.
      size="min(1180px, calc(100vw - 3rem))"
      radius="lg"
      title={null}
      attributes={{ content: { 'aria-label': title || t`Series details` } }}
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
        <div className="discover-modal-root" data-bar={barShown || undefined}>
          <CloseButton
            className="discover-modal-close"
            size="lg"
            aria-label={t`Close`}
            onClick={onClose}
          />
          {onReroll && (
            <Tooltip label={t`Roll again`} withArrow zIndex={1001}>
              <ActionIcon
                className="discover-modal-reroll"
                size="lg"
                variant="subtle"
                aria-label={t`Roll again`}
                onClick={reroll}
              >
                <DiceIcon spinRef={diceScope} />
              </ActionIcon>
            </Tooltip>
          )}

          <div className="discover-scroll" ref={setScroller}>
          {/* Sticky at zero height, so it costs the band no room and only shows once scrolled. */}
          <div className="discover-bar-anchor">
            <div className="discover-bar" data-shown={barShown || undefined} inert={!barShown}>
              {cover && <img className="discover-bar-poster" src={cover} alt="" />}
              <div className="discover-bar-identity">
                <div className="discover-bar-title">{title}</div>
                <Text component="div" size="xs" c="var(--ink-3)" className="discover-bar-meta">
                  <MetaList
                    inline
                    items={[
                      score != null ? (
                        <span key="score" className="discover-bar-score">
                          <IconStarFilled size={12} />
                          {formatFixedDecimal(score / 10, 1)}
                        </span>
                      ) : null,
                      <span key="status" style={{ color: `var(--${statusToken(status.color)})` }}>
                        {renderLabel(status.label)}
                      </span>,
                      chapters != null ? plural(chapters, { one: '# chapter', other: '# chapters' }) : null,
                      year,
                    ]}
                  />
                </Text>
              </div>
              <div className="discover-bar-actions">
                {inLibrarySeriesId == null && (
                  <ShortlistButton compact providerId={item.providerId} title={title} source={savedFrom} />
                )}
                {/* The add panel portals its own primary action in here, so the bar's button is the
                    same add with the same settings, not a second copy of the form's state. */}
                <div ref={setBarSlot} className="discover-bar-slot" />
              </div>
              <CloseButton className="discover-bar-close" size="lg" aria-label={t`Close`} onClick={onClose} />
            </div>
          </div>

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
                      animate={!isError}
                      className="discover-poster-skeleton"
                      w={150}
                      h={225}
                      radius="var(--radius-hero)"
                      style={{ flexShrink: 0 }}
                    />
                  )}

                  <Stack gap={0} style={{ flex: 1, minWidth: 0 }}>
                    {/* Capped at three lines, set smaller as the title gets longer: a light-novel
                        title at display size filled half the card before the synopsis started. The
                        whole title is the tooltip. */}
                    <Title
                      order={1}
                      className="series-hero-title"
                      data-length={titleLength}
                      title={title || undefined}
                    >
                      {title || (isError ? t`Couldn't load this title` : '')}
                    </Title>

                    {/* One line, truncated. Every other alternative title is under At a glance. */}
                    {(detail?.nativeTitle || detail?.romanizedTitle) && (
                      <Text component="div" size="sm" mt={8} c="var(--ink-3)" className="discover-alt-line">
                        <MetaList inline items={[detail?.romanizedTitle, detail?.nativeTitle]} />
                      </Text>
                    )}

                    <Group gap={8} mt={14} wrap="wrap">
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
                      <ContentRatingChip rating={detail?.contentRating} tooltipZIndex={1001} />
                      {/* What kind of book it is: a fact, not a state, so it takes the quiet pill. An
                          anime adaptation rides on the same pill as a violet mark rather than
                          adding a fourth pill to the row. */}
                      {typeLabel && (
                        <span className="series-hero-status" data-quiet>
                          <IconBooks size={14} />
                          {typeLabel}
                          {detail?.hasAnime && (
                            <Tooltip label={t`Has an anime adaptation`} withArrow zIndex={1001}>
                              <span className="hero-anime-mark" role="img" aria-label={t`Has an anime adaptation`} tabIndex={0}>
                                <IconDeviceTv size={14} />
                              </span>
                            </Tooltip>
                          )}
                        </span>
                      )}
                    </Group>

                    {/* The numbers people open a card to check, each labelled for the question it
                        answers. The score ring and the chapter count are on the card's own row, so
                        they are set from the first frame rather than arriving with the detail. */}
                    <div className="hero-figures discover-figures">
                      {score != null && (
                        <Tooltip label={t`MangaBaka aggregate score`} withArrow zIndex={1001}>
                          <ScoreRing score={score} />
                        </Tooltip>
                      )}
                      {score != null && (chapters != null || volumes != null || year != null) && (
                        <span className="hero-figure-rule" aria-hidden />
                      )}
                      <div className="hero-stats">
                        {chapters != null ? (
                          <div className="hero-stat">
                            <span className="hero-stat-n figure">
                              {volumes != null ? (
                                <Trans>
                                  {chapters}
                                  <small className="hero-stat-unit">ch</small>
                                </Trans>
                              ) : (
                                chapters
                              )}
                            </span>
                            <span className="hero-stat-l">
                              {volumes != null
                                ? plural(volumes, { one: 'In # volume', other: 'In # volumes' })
                                : t`Chapters`}
                            </span>
                          </div>
                        ) : (
                          volumes != null && (
                            <div className="hero-stat">
                              <span className="hero-stat-n figure">{volumes}</span>
                              <span className="hero-stat-l">
                                <Trans>Volumes</Trans>
                              </span>
                            </div>
                          )
                        )}
                        {catchUpMinutes != null && (
                          <Tooltip
                            withArrow
                            multiline
                            w={260}
                            zIndex={1001}
                            label={plural(chapters ?? 0, {
                              one: `# chapter at your usual ${paceMinutes} min a chapter.`,
                              other: `# chapters at your usual ${paceMinutes} min a chapter.`,
                            })}
                          >
                            <div className="hero-stat" tabIndex={0}>
                              <span className="hero-stat-n figure">
                                {catchUpMinutes < 90 ? (
                                  <Trans>
                                    ~{catchUpMinutes}
                                    <small className="hero-stat-unit">min</small>
                                  </Trans>
                                ) : (
                                  <Trans>
                                    ~{Math.round(catchUpMinutes / 60)}
                                    <small className="hero-stat-unit">h</small>
                                  </Trans>
                                )}
                              </span>
                              <span className="hero-stat-l">
                                <Trans>To catch up</Trans>
                              </span>
                            </div>
                          </Tooltip>
                        )}
                        {year != null && (
                          <div className="hero-stat">
                            <span className="hero-stat-n figure">{year}</span>
                            <span className="hero-stat-l">
                              <Trans>First released</Trans>
                            </span>
                          </div>
                        )}
                      </div>
                    </div>

                    {/* The sites with their own scores on them: where a number comes from and the
                        number are one control rather than two rows. */}
                    {(links.length > 0 || unlinkedRatings.length > 0 || readerHint) && (
                      <Group gap="sm" mt={14} align="center" wrap="wrap">
                        <MetadataLinks
                          links={links}
                          chips
                          extra={unlinkedRatings.map((r) => (
                            <Tooltip key={r.source} label={r.source} withArrow zIndex={1001}>
                              <span className="site-chip" data-static tabIndex={0}>
                                <MetadataSiteIcon
                                  site={r.source.toLowerCase()}
                                  monogram={r.source.slice(0, 2).toUpperCase()}
                                  size={15}
                                />
                                <span className="tnum">{formatFixedDecimal(r.rating / 10, 1)}</span>
                              </span>
                            </Tooltip>
                          ))}
                        />
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
                            label={
                              readerHintHigher
                                ? plural(readerHintReaders, {
                                    one: `# AniList reader with reading habits like yours scored this ${readerHintGap} higher than AniList readers overall.`,
                                    other: `# AniList readers with reading habits like yours scored this ${readerHintGap} higher than AniList readers overall.`,
                                  })
                                : plural(readerHintReaders, {
                                    one: `# AniList reader with reading habits like yours scored this ${readerHintGap} lower than AniList readers overall.`,
                                    other: `# AniList readers with reading habits like yours scored this ${readerHintGap} lower than AniList readers overall.`,
                                  })
                            }
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
                      </Group>
                    )}
                  </Stack>
                </Group>

                {/* One card for everything there is to do with the series: the add settings, the
                    add itself last, and the lighter actions as the card's footer. Top-aligned with
                    the poster, so the band has one top line. */}
                <div className="discover-action-col">
                  <div className="discover-action-card" ref={setActionCard}>
                    {/* Keyed by provider id: a half-filled request belongs to the series it was
                        started for, and remounting is a cheaper reset than clearing six fields. */}
                    <DiscoverLibraryRail
                      key={item.providerId}
                      item={item}
                      detail={detail}
                      inLibrarySeriesId={inLibrarySeriesId}
                      rootFolders={rootFolders}
                      onClose={onClose}
                      addedFrom={feedbackContext ? 'recommendation' : 'library'}
                      barSlot={barSlot}
                    />
                    <div className="discover-card-footer">
                      {inLibrarySeriesId == null && (
                        <>
                          <PreviewChapterButton
                            key={`preview-${item.providerId}`}
                            compact
                            providerId={item.providerId}
                            title={title}
                            onRead={() => setPreviewFor(item.providerId)}
                          />
                          <ShortlistButton
                            key={`shortlist-${item.providerId}`}
                            compact
                            providerId={item.providerId}
                            title={title}
                            source={savedFrom}
                          />
                        </>
                      )}
                      <RecommendationFeedbackMenu providerId={item.providerId} compact />
                    </div>
                  </div>
                  {inLibrarySeriesId == null && shortlisted && (
                    <ShortlistNote key={`note-${item.providerId}`} providerId={item.providerId} />
                  )}
                </div>
              </div>

            </div>
          </Box>

          <div className="discover-body">
          <div className="detail-split">
              <div className="detail-main">
                <Panel>
                  <Stack gap="md">
                    <PanelTitle order={2} icon={IconFileText}>
                      <Trans>Synopsis</Trans>
                    </PanelTitle>

                    {isLoading && !detail && (
                      <Stack gap="xs">
                        <Skeleton h={12} />
                        <Skeleton h={12} />
                        <Skeleton h={12} w="70%" />
                      </Stack>
                    )}
                    {isError && !detail && (
                      <Group gap="sm">
                        <Text size="sm" c="var(--ink-3)">
                          <Trans>Couldn't load the details for this title.</Trans>
                        </Text>
                        <Button size="xs" variant="default" onClick={() => void refetch()}>
                          <Trans>Retry</Trans>
                        </Button>
                      </Group>
                    )}

                    {(detail?.description || item.description) && (
                      <Spoiler
                        maxHeight={120}
                        showLabel={t`Show more`}
                        hideLabel={t`Show less`}
                        attributes={{ content: { 'aria-label': t`Synopsis` } }}
                      >
                        <Text size="sm" c="var(--ink-3)" style={{ whiteSpace: 'pre-line', lineHeight: 1.66 }}>
                          {cleanSynopsis(detail?.description ?? item.description)}
                        </Text>
                      </Spoiler>
                    )}
                    {/* Said out loud rather than left as a titled panel with nothing under it. */}
                    {detail && !detail.description && !item.description && (
                      <Text size="sm" c="var(--ink-4)">
                        <Trans>The catalogue has no synopsis for this one.</Trans>
                      </Text>
                    )}

                    {detail?.animeResume && (
                        <AnimeResumeCallout
                            resume={detail.animeResume}
                            variant="catalogue"
                            inLibrarySeriesId={inLibrarySeriesId ?? detail.animeResume?.inLibrarySeriesId ?? null}
                        />
                    )}

                    {(detail?.animeStart || detail?.animeEnd) && (
                        <>
                          <Divider color="var(--hairline)"/>
                          <Title order={3} size="h4" fz="var(--type-body)">
                            <Trans>Anime coverage</Trans>
                          </Title>
                          <AnimeCoverageBar
                              start={detail.animeStart}
                              end={detail.animeEnd}
                              totalChapters={detail.totalChapters}
                              tooltipZIndex={1001}
                              hideResumeHint={Boolean(detail?.animeResume)}
                          />
                        </>
                    )}

                    {/* The genres close the synopsis they summarise. The band only carries what a
                        glance needs, and the tags have a panel of their own below. */}
                    {detail && detail.genres.length > 0 && (
                      <>
                        <Divider color="var(--hairline)" />
                        <TagChips>
                          {detail.genres.map((g) => (
                            <TagChip key={g}>{genreLabel(g)}</TagChip>
                          ))}
                        </TagChips>
                      </>
                    )}
                  </Stack>
                </Panel>

                {detail && detail.tags.length > 0 && <DiscoverTags tags={detail.tags} />}
              </div>

              {/* The column beside the reading one: what other readers made of it, then the facts
                  about it. Mounted only once there is something to put in it, which is what lets
                  `.detail-split` drop the track and give the synopsis the whole width. */}
              {detail && (
                <div className="detail-aside">
                  <DiscoverReviews ids={detail} />
                  <DiscoverGlance detail={detail} onNavigate={onClose} />
                </div>
              )}
            </div>
          </div>
          </div>
          {previewing && (
            <SeriesPreviewReader
              providerId={item.providerId}
              title={title}
              coverUrl={cover}
              seriesType={detail?.type ?? null}
              onClose={() => setPreviewFor(null)}
            />
          )}
        </div>
      )}
    </Modal>
  )
}

/**
 * MangaBaka's 0-100 aggregate as a gold ring around the score out of ten. The ring is the same
 * shape Home's level ring and the covers' read ring use, in the score's own gold. Takes a ref and
 * passes the rest through, because a Tooltip wraps it.
 */
function ScoreRing({
  score,
  ref,
  ...rest
}: { score: number; ref?: Ref<HTMLSpanElement> } & HTMLAttributes<HTMLSpanElement>) {
  const { t } = useLingui()
  const value = formatFixedDecimal(score / 10, 1)
  const radius = 22
  const circumference = 2 * Math.PI * radius
  const filled = (Math.min(100, Math.max(0, score)) / 100) * circumference
  return (
    // The spread goes first: the Tooltip hands the child's own props back, an undefined className
    // among them, and spread last it wiped the ring's class.
    <span {...rest} ref={ref} className="hero-ring" role="img" aria-label={t`Score ${value} out of 10`} tabIndex={0}>
      <svg viewBox="0 0 52 52" aria-hidden>
        <circle className="hero-ring-track" cx="26" cy="26" r={radius} />
        <circle
          className="hero-ring-fill"
          cx="26"
          cy="26"
          r={radius}
          strokeDasharray={`${filled} ${circumference}`}
          transform="rotate(-90 26 26)"
        />
      </svg>
      <span className="hero-ring-n figure" aria-hidden>
        {value}
      </span>
    </span>
  )
}
