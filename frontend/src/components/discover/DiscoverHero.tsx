import { useCallback, useEffect, useState, type CSSProperties } from 'react'
import { Badge, Box, Button, Group, Popover, Stack, Text, Title } from '@mantine/core'
import { useMediaQuery } from '@mantine/hooks'
import { IconArrowRight, IconChevronDown, IconPlus, IconStar } from '@tabler/icons-react'
import { useNavigate } from 'react-router-dom'
import { AnimatePresence, motion, useReducedMotion } from 'motion/react'
import { Trans, useLingui } from '@lingui/react/macro'
import { useRecommendationDetail, type RecommendationItem } from '../../api/hooks'
import { MetadataSiteIcon } from '../MetadataSiteIcon'
import { HeroBackdrop } from '../series/HeroBackdrop'
import {
  ratingBandVisual,
  seriesStatusVisual,
} from '../ui/status'
import { HeroStatusPills } from '../ui/HeroStatus'
import { coReadPhrase, relationPhrase } from '../ui/DiscoverRail'
import { useLabel } from '../../i18n-context'
import { formatFixedDecimal } from '../../format'

/** How long one pick holds the band before the next takes it. */
const ROTATE_MS = 7000

/**
 * How many picks the list carries. The list is the tallest thing in the band, so this number is
 * what the band's height is: at six rows it ran to 570px and pushed every rail below the fold.
 * A short viewport drops one more.
 */
const PICKS = 5
const PICKS_SHORT = 4
/** Under 820px the list stops sitting beside the feature and stacks under it, so it costs double. */
const PICKS_NARROW = 3

/**
 * The Discover page's opening band: one pick at full size, the rest in a vertical list beside it.
 *
 * <p>
 * It is the same `.series-hero[data-compact]` band the detail modal and the series page use, for
 * the reason the modal gives for reusing the pills: a pick, the card it opens and the series it
 * becomes have to read as one object rather than three vocabularies. The only thing this adds is
 * the list and the rotation.
 * </p>
 *
 * <p>
 * There is no spotlight endpoint and no wide banner asset anywhere in the catalogue, so the band is
 * synthesized from the first few items of whichever rail the caller passes and `HeroBackdrop` makes
 * a backdrop out of the portrait cover, exactly as it does on the series page.
 * </p>
 */
export function DiscoverHero({
  items,
  onOpen,
  onRecommend,
  seriesIdFor,
}: {
  /** The picks to rotate through. Rendered from the first; anything past {@link PICKS} is ignored. */
  items: RecommendationItem[]
  /** Opens the detail modal, whose library rail owns the full add/request flow. */
  onOpen: (item: RecommendationItem) => void
  /** Opens Recommended with this title as its only seed. */
  onRecommend: (item: RecommendationItem) => void
  /** Library series id when the pick is already owned; the primary action then opens the series. */
  seriesIdFor?: (item: RecommendationItem) => number | null
}) {
  const navigate = useNavigate()
  const shortViewport = useMediaQuery('(max-height: 860px)')
  const stacked = useMediaQuery('(max-width: 820px)')
  const picks = items.slice(0, stacked ? PICKS_NARROW : shortViewport ? PICKS_SHORT : PICKS)
  const [activeIndex, setActive] = useState(0)
  const [paused, setPaused] = useState(false)
  const [autoRotate, setAutoRotate] = useState(true)
  const [detailsOpen, setDetailsOpen] = useState(false)
  const reducedMotion = useReducedMotion()
  // Called up here, unconditionally, rather than after the `if (!item) return null` below: both are
  // hooks, and the rule holds even though `item` is never actually undefined once Discover is the
  // one mounting this (it only does so with a non-empty list).
  const renderLabel = useLabel()
  const { t } = useLingui()
  const canAutoRotate = picks.length > 1 && autoRotate && !reducedMotion

  // Clamped rather than reset: the list shortens when the window does, and an index left pointing
  // past the end would blank the band mid-resize.
  const active = Math.min(activeIndex, picks.length - 1)
  const item = picks[active] as RecommendationItem | undefined
  const { data: detail } = useRecommendationDetail(item?.providerId ?? null)
  // The band's reason is clamped; when it is actually cut, the details list carries it in full
  // (a title attribute alone leaves a phone with no way to read the rest).
  const [reasonCut, setReasonCut] = useState(false)
  const reasonRef = useCallback(
    (el: HTMLParagraphElement | null) => {
      if (!el) return
      const check = () => setReasonCut(el.scrollHeight > el.clientHeight + 1)
      check()
      const observer = new ResizeObserver(check)
      observer.observe(el)
      return () => {
        observer.disconnect()
        setReasonCut(false)
      }
    },
    // Re-measured per pick: the next reason can be cut where this one was not, at the same size.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [item?.providerId],
  )

  const choose = useCallback((n: number) => {
    setAutoRotate(false)
    setDetailsOpen(false)
    setActive(n)
  }, [])

  useEffect(() => {
    if (!canAutoRotate || paused || detailsOpen) return

    const id = window.setTimeout(() => setActive((n) => (n + 1) % picks.length), ROTATE_MS)
    return () => window.clearTimeout(id)
  }, [active, canAutoRotate, paused, detailsOpen, picks.length])

  if (!item) return null

  // Everything here is on the rail item as well as the detail response, so the band is complete on
  // the first frame and the detail request fills in behind it rather than rearranging it.
  const cover = item.thumbUrlHiDpi ?? item.coverUrl ?? null
  const { title, becauseOfTitle, relationKind, relatedToTitle } = item
  const ownedSeriesId = seriesIdFor?.(item) ?? null
  const score = detail?.rating ?? item.rating
  const band = ratingBandVisual(score ?? 0)
  // `id` is a stable key: `label` is translated text, and keying the row off it would remount the
  // whole figures block on a language switch.
  const year = detail?.year ?? item.year
  const lengths = [
    { id: 'chapters', label: t`Chapters`, value: detail?.totalChapters ?? item.totalChapters },
    { id: 'volumes', label: t`Volumes`, value: detail?.finalVolume },
  ].filter((f): f is { id: string; label: string; value: number } => f.value != null)

  // The three "why" flavours, in the order of how much they claim: what readers did beats what they
  // said, and both beat proximity in the behavioural space. The band shows the first; the rest wait
  // behind the score. Each carries a stable id rather than being keyed by its own translated text.
  const reasons = [
    item.coRead ? { id: 'co-read', text: coReadPhrase(item) } : null,
    item.coRecommended
      ? { id: 'co-recommended', text: t`Readers like you also recommended this` }
      : null,
    item.tasteMatch ? { id: 'taste-match', text: t`Close to your reading in taste space` } : null,
    becauseOfTitle ? { id: 'because-of-title', text: t`Because you read ${becauseOfTitle}` } : null,
    relationKind && relatedToTitle
      ? { id: 'relation', text: relationPhrase(relationKind, relatedToTitle) }
      : null,
  ].filter((r): r is { id: string; text: string } => r != null)

  return (
    <Box
      className="series-hero discover-hero"
      data-compact
      data-auto-rotating={canAutoRotate ? 'true' : undefined}
      style={{ '--hero-rotate-ms': `${ROTATE_MS}ms` } as CSSProperties}
      onMouseEnter={() => setPaused(true)}
      onMouseLeave={() => setPaused(false)}
      onFocusCapture={() => setPaused(true)}
      onBlurCapture={() => setPaused(false)}
    >
      <AnimatePresence initial={false}>
        <motion.div
          key={item.providerId}
          className="discover-hero-backdrop"
          initial={reducedMotion ? false : { opacity: 0 }}
          animate={{ opacity: 1 }}
          exit={reducedMotion ? undefined : { opacity: 0 }}
          transition={{ duration: 0.55, ease: 'easeOut' }}
        >
          <HeroBackdrop coverUrl={cover} />
        </motion.div>
      </AnimatePresence>

      <div className="series-hero-body">
        <div className="series-hero-content">
          <motion.div
            key={item.providerId}
            className="discover-hero-feature"
            initial={reducedMotion ? false : { opacity: 0, y: 10 }}
            animate={{ opacity: 1, y: 0 }}
            transition={{ duration: 0.4, ease: [0.16, 1, 0.3, 1] }}
          >
            <Group align="flex-start" gap={26} wrap="nowrap" className="discover-hero-feature-row">
            {cover && (
              <button
                type="button"
                className="discover-hero-poster"
                onClick={() => onOpen(item)}
                aria-label={t`View ${title}`}
              >
                <img className="series-hero-poster" src={cover} alt="" />
              </button>
            )}

            <Stack gap={0} style={{ flex: 1, minWidth: 0 }}>
              <Title order={1} className="series-hero-title">
                <button type="button" className="discover-hero-title" onClick={() => onOpen(item)}>
                  {title}
                </button>
              </Title>

              {/* Score, year and the strongest reason are what the band is for. Everything else a
                  pick carries sits behind the score: the per-source scores, status, rating,
                  lengths and the weaker reasons. */}
              <div className="hero-figures">
                <Popover
                  opened={detailsOpen}
                  onChange={setDetailsOpen}
                  position="bottom-start"
                  offset={8}
                  shadow="md"
                  withinPortal
                >
                  <Popover.Target>
                    <button
                      type="button"
                      className="hero-score"
                      data-empty={score == null || undefined}
                      style={{ '--band': score != null ? `var(--${band.token})` : 'var(--ink-3)' } as CSSProperties}
                      onClick={() => setDetailsOpen((o) => !o)}
                      aria-expanded={detailsOpen}
                      aria-label={
                        score != null
                          ? t`Score ${formatFixedDecimal(score / 10, 1)}. Show scores and details`
                          : t`Show scores and details`
                      }
                    >
                      <IconStar size={18} />
                      {score != null ? (
                        <span className="hero-score-n figure">{formatFixedDecimal(score / 10, 1)}</span>
                      ) : (
                        <span className="hero-score-none">
                          <Trans>Details</Trans>
                        </span>
                      )}
                      <IconChevronDown size={15} className="hero-score-caret" aria-hidden />
                    </button>
                  </Popover.Target>
                  <Popover.Dropdown className="hero-details">
                    <section className="hero-details-group">
                      <h3 className="hero-details-label">
                        <Trans>Scores by source</Trans>
                      </h3>
                      {detail ? (
                        detail.sourceRatings.length > 0 ? (
                          <Group gap="xs">
                            {detail.sourceRatings.map((r) => (
                              <Badge
                                key={r.source}
                                size="md"
                                variant="outline"
                                color="var(--neutral)"
                                title={r.source}
                                leftSection={
                                  <MetadataSiteIcon
                                    site={r.source.toLowerCase()}
                                    monogram={r.source.slice(0, 2).toUpperCase()}
                                    size={12}
                                  />
                                }
                              >
                                {formatFixedDecimal(r.rating / 10, 1)}
                              </Badge>
                            ))}
                          </Group>
                        ) : (
                          <Text size="sm" c="var(--ink-3)">
                            <Trans>No source has scored this yet.</Trans>
                          </Text>
                        )
                      ) : (
                        <Text size="sm" c="var(--ink-3)">
                          <Trans>Loading scores…</Trans>
                        </Text>
                      )}
                    </section>

                    <section className="hero-details-group">
                      <h3 className="hero-details-label">
                        <Trans>Details</Trans>
                      </h3>
                      <Group gap={8} wrap="wrap">
                        <HeroStatusPills status={detail?.status ?? item.status} contentRating={detail?.contentRating ?? null} />
                      </Group>
                      {lengths.length > 0 && (
                        <dl className="hero-details-facts">
                          {lengths.map((f) => (
                            <div key={f.id}>
                              <dt>{f.label}</dt>
                              <dd className="tnum">{f.value}</dd>
                            </div>
                          ))}
                        </dl>
                      )}
                    </section>

                    {(reasons.length > 1 || (reasons.length > 0 && reasonCut)) && (
                      <section className="hero-details-group">
                        <h3 className="hero-details-label">
                          {reasonCut ? <Trans>Why this pick</Trans> : <Trans>Also because</Trans>}
                        </h3>
                        <ul className="hero-details-reasons">
                          {reasons.slice(reasonCut ? 0 : 1).map((r) => (
                            <li key={r.id}>{r.text}</li>
                          ))}
                        </ul>
                      </section>
                    )}
                  </Popover.Dropdown>
                </Popover>
                {year != null && (
                  <>
                    <span className="hero-figure-rule" aria-hidden />
                    <div className="hero-stats">
                      <div className="hero-stat">
                        <span className="hero-stat-n figure">{year}</span>
                        <span className="hero-stat-l">
                          <Trans>Released</Trans>
                        </span>
                      </div>
                    </div>
                  </>
                )}
              </div>

              {reasons.length > 0 && (
                <p ref={reasonRef} className="discover-hero-reason" title={reasons[0].text}>
                  {reasons[0].text}
                </p>
              )}

              <Group gap="xs" className="discover-hero-actions">
                {/* Adding needs a root folder, the caller's permissions and the request path for
                    non-admins, all of which `DiscoverLibraryRail` handles inside the detail card. */}
                {ownedSeriesId != null ? (
                  <Button
                    color="var(--ok)"
                    variant="light"
                    rightSection={<IconArrowRight size={16} />}
                    onClick={() => navigate(`/series/${ownedSeriesId}`)}
                    aria-label={t`View ${title} in library`}
                  >
                    <Trans>View in library</Trans>
                  </Button>
                ) : (
                  <Button
                    leftSection={<IconPlus size={16} />}
                    onClick={() => onOpen(item)}
                    aria-label={t`Add ${title} to library`}
                  >
                    <Trans>Add to library</Trans>
                  </Button>
                )}
                <Button variant="default" onClick={() => onRecommend(item)}>
                  <Trans>More like this</Trans>
                </Button>
              </Group>
            </Stack>
            </Group>
          </motion.div>

          <div className="discover-hero-strip">
            <div className="discover-hero-strip-heading">
              <Text className="discover-hero-strip-label">
                <Trans>Also for you</Trans>
              </Text>
              {canAutoRotate && (
                <Text className="discover-hero-strip-mode">
                  {paused || detailsOpen ? <Trans>Paused</Trans> : <Trans>Auto</Trans>}
                </Text>
              )}
            </div>
            <div
              className="discover-hero-strip-row"
              role="tablist"
              aria-label={t`Other picks for you`}
            >
              {picks.map((p, n) => {
                const pickStatus = seriesStatusVisual(p.status)
                const matches = [...p.matchedTags, ...p.matchedGenres].slice(0, 2)
                const thumbnail = p.thumbUrl ?? p.coverUrl

                return (
                  <button
                    key={p.providerId}
                    type="button"
                    role="tab"
                    aria-selected={n === active}
                    aria-label={p.title}
                    className="discover-hero-strip-item"
                    onClick={() => choose(n)}
                  >
                    {thumbnail ? (
                      <img
                        src={thumbnail}
                        srcSet={p.thumbUrlHiDpi ? `${p.thumbUrlHiDpi} 2x` : undefined}
                        alt=""
                        loading="lazy"
                        decoding="async"
                      />
                    ) : (
                      <span className="discover-hero-strip-fallback">{p.title.slice(0, 1)}</span>
                    )}
                    <span className="discover-hero-strip-copy">
                      <span className="discover-hero-strip-title">{p.title}</span>
                      <span className="discover-hero-strip-meta">
                        {p.year != null && <span className="tnum">{p.year}</span>}
                        <span>{renderLabel(pickStatus.label)}</span>
                        {p.rating != null && (
                          <span className="discover-hero-strip-rating tnum">
                            <IconStar size={11} />
                            {formatFixedDecimal(p.rating / 10, 1)}
                          </span>
                        )}
                      </span>
                      {matches.length > 0 && (
                        <span className="discover-hero-strip-match">{matches.join(' · ')}</span>
                      )}
                    </span>
                    {n === active && canAutoRotate && !paused && !detailsOpen && (
                      <span className="discover-hero-strip-progress" aria-hidden>
                        <span key={item.providerId} />
                      </span>
                    )}
                  </button>
                )
              })}
            </div>
          </div>
        </div>
      </div>
    </Box>
  )
}
