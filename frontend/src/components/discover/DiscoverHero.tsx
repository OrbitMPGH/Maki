import { useCallback, useEffect, useLayoutEffect, useRef, useState, type CSSProperties } from 'react'
import { ActionIcon, Box, Button, Group, Text, Title, Tooltip } from '@mantine/core'
import { useMediaQuery } from '@mantine/hooks'
import {
  IconArchive,
  IconArrowRight,
  IconPlayerPauseFilled,
  IconPlayerPlayFilled,
  IconPlus,
} from '../../lib/icons'
import { useNavigate } from 'react-router-dom'
import { AnimatePresence, motion, useAnimate, useReducedMotion } from 'motion/react'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'
import { useRecommendationDetail, type RecommendationItem } from '../../api/hooks'
import type { MetadataLink } from '../../api/types'
import { MetadataLinks } from '../MetadataLinks'
import { MetadataSiteIcon } from '../MetadataSiteIcon'
import { HeroBackdrop } from '../series/HeroBackdrop'
import { HeroChips } from '../series/HeroChips'
import { seriesStatusVisual } from '../ui/status'
import { engineReason } from '../ui/DiscoverRail'
import { ScoreInline } from '../ui/ScoreInline'
import { TYPE_LABELS } from '../CatalogueFilters'
import { ScoreFigure } from './ScoreFigure'
import { RestoreArchivedButton, useIsArchivedSeries } from './ArchivedRestore'
import { useLabel } from '../../i18n-context'
import { formatFixedDecimal } from '../../format'
import { onRovingKeyDown } from '../../lib/rovingFocus'
import { CINEMATIC, DUR, DWELL, EASE, paced } from '../../lib/motion'
import { MetaList } from '../ui/MetaList'

/** How long one pick holds the band before the next takes it. */
const ROTATE_MS = DWELL.heroPick * 1000

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
 * The title sits in a box two display lines tall, so the band is the same height for every pick and
 * a rotation never moves the page under someone scrolled past it. A title that does not fit is set
 * smaller, three lines in the same box (`data-fit`), and clamped past that. Measured rather than
 * guessed from its length, because how much fits depends on the column and the font.
 */
function useTitleFit(title: string | undefined) {
  const ref = useRef<HTMLHeadingElement>(null)
  useLayoutEffect(() => {
    const el = ref.current
    if (!el) return
    // The button's own height, not the heading's scrollHeight: at a 0.94 line height the glyphs
    // overhang their lines, so a title that fits still reports overflow.
    const fit = () => {
      el.dataset.fit = 'full'
      const lines = el.firstElementChild?.getBoundingClientRect().height ?? 0
      el.dataset.fit = lines > el.clientHeight + 1 ? 'small' : 'full'
    }
    fit()
    let live = true
    void document.fonts?.ready.then(() => live && fit())
    const observer = new ResizeObserver(fit)
    observer.observe(el)
    return () => {
      live = false
      observer.disconnect()
    }
  }, [title])
  return ref
}

/**
 * Whether the band is on screen in a visible tab. The rotation holds otherwise, so someone who
 * scrolls back up or returns to the tab finds the pick they left.
 */
function useWatched(el: HTMLElement | null) {
  const [inView, setInView] = useState(true)
  const [tabVisible, setTabVisible] = useState(() => document.visibilityState === 'visible')
  useEffect(() => {
    if (!el) return
    const observer = new IntersectionObserver(
      ([entry]) => setInView(entry.intersectionRatio >= 0.3),
      { threshold: 0.3 },
    )
    observer.observe(el)
    const onVisibility = () => setTabVisible(document.visibilityState === 'visible')
    document.addEventListener('visibilitychange', onVisibility)
    return () => {
      observer.disconnect()
      document.removeEventListener('visibilitychange', onVisibility)
    }
  }, [el])
  return inView && tabVisible
}

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
  const [userPaused, setUserPaused] = useState(false)
  const [autoRotate, setAutoRotate] = useState(true)
  const [heroEl, setHeroEl] = useState<HTMLDivElement | null>(null)
  const watched = useWatched(heroEl)
  const halted = paused || userPaused || !watched
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
  const current = picks[active] as RecommendationItem | undefined
  // The feature lags the selection by a short fade, so its text and buttons always describe the same
  // pick and never sit half-faded over the next one.
  const [shownId, setShownId] = useState(current?.providerId)
  const item = picks.find((p) => p.providerId === shownId) ?? current
  const [swapScope, animateSwap] = useAnimate<HTMLDivElement>()
  const target = current?.providerId
  const showing = item?.providerId

  useEffect(() => {
    const parts = Array.from(swapScope.current?.querySelectorAll<HTMLElement>('[data-hero-swap]') ?? [])
    if (!target) return
    if (target === showing) {
      if (parts.length === 0) return
      const fadeIn = animateSwap(parts, { opacity: 1 }, { duration: reducedMotion ? 0 : paced(DUR.base), ease: EASE })
      return () => fadeIn.stop()
    }
    if (reducedMotion || parts.length === 0) {
      setShownId(target)
      return
    }
    let live = true
    const fadeOut = animateSwap(parts, { opacity: 0 }, { duration: paced(DUR.fast), ease: 'linear' })
    void fadeOut.then(() => live && setShownId(target))
    return () => {
      live = false
      fadeOut.stop()
    }
  }, [animateSwap, reducedMotion, showing, swapScope, target])

  const { data: detail } = useRecommendationDetail(item?.providerId ?? null)
  const titleRef = useTitleFit(item?.title)
  const ownedArchived = useIsArchivedSeries(item ? seriesIdFor?.(item) : null)

  const choose = useCallback((n: number) => {
    setAutoRotate(false)
    setActive(n)
  }, [])

  useEffect(() => {
    if (!canAutoRotate || halted) return

    const id = window.setTimeout(() => setActive((n) => (n + 1) % picks.length), ROTATE_MS)
    return () => window.clearTimeout(id)
  }, [active, canAutoRotate, halted, picks.length])

  if (!item || !current) return null

  // Everything here is on the rail item as well as the detail response, so the band is complete on
  // the first frame and the detail request fills in behind it rather than rearranging it.
  const cover = item.thumbUrlHiDpi ?? item.coverUrl ?? null
  const { title } = item
  const ownedSeriesId = seriesIdFor?.(item) ?? null
  const score = detail?.rating ?? item.rating
  const year = detail?.year ?? item.year
  const chapters = detail?.totalChapters ?? item.totalChapters
  const volumes = detail?.finalVolume ?? null
  const typeLabel = detail?.type ? renderLabel(TYPE_LABELS[detail.type] ?? detail.type) : null
  // The card's one reason, as the modal's kicker says it. The card names one, so the band does too.
  const reason = engineReason(item)

  // As in the modal: each site's score on its own link chip, scored sites first.
  const ratingBySite = new Map((detail?.sourceRatings ?? []).map((r) => [r.source.toLowerCase(), r.rating]))
  const links: MetadataLink[] = (detail?.links ?? [])
    .map((link) => {
      const rating = ratingBySite.get(link.site)
      return rating != null && link.score == null ? { ...link, score: formatFixedDecimal(rating / 10, 1) } : link
    })
    .sort((a, b) => Number(b.score != null) - Number(a.score != null))
  const unlinked = (detail?.sourceRatings ?? [])
    .filter((r) => !links.some((link) => link.site === r.source.toLowerCase()))
    .map((r) => (
      <Tooltip key={r.source} label={r.source} withArrow>
        <span className="site-chip" data-static tabIndex={0}>
          <MetadataSiteIcon site={r.source.toLowerCase()} monogram={r.source.slice(0, 2).toUpperCase()} size={15} />
          <span className="tnum">{formatFixedDecimal(r.rating / 10, 1)}</span>
        </span>
      </Tooltip>
    ))

  return (
    <Box
      ref={setHeroEl}
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
          transition={{ duration: paced(CINEMATIC.fade), ease: EASE }}
        >
          <HeroBackdrop coverUrl={cover} />
        </motion.div>
      </AnimatePresence>

      <div className="series-hero-body">
        <div className="series-hero-content">
          <div ref={swapScope} className="discover-hero-feature">
            <Group align="flex-start" gap={26} wrap="nowrap" className="discover-hero-feature-row">
            {cover && (
              <button
                type="button"
                data-hero-swap
                className="discover-hero-poster"
                data-series-cover={ownedSeriesId ?? undefined}
                onClick={() => onOpen(item)}
                aria-label={t`View ${title}`}
              >
                <img className="series-hero-poster" src={cover} alt="" />
              </button>
            )}

            <div className="hero-column hero-continue" style={{ flex: 1, minWidth: 0 }}>
              {/* The modal's header, piece for piece, so a pick reads the same here as in the card it
                  opens. Every row below the title holds its height while the detail loads and across
                  picks: the band must not move under someone reading it. */}
              <div className="hero-kicker" data-hero-swap>
                {reason ? (
                  <>
                    <reason.Glyph size={14} />
                    <span className="hero-kicker-text">{reason.text}</span>
                  </>
                ) : (
                  <Trans>Your next read</Trans>
                )}
              </div>

              {/* The page's h1 is "Discover"; the featured pick is a section of it. */}
              <Title order={2} className="series-hero-title" ref={titleRef} data-hero-swap>
                <button type="button" className="discover-hero-title" title={title} onClick={() => onOpen(item)}>
                  {title}
                </button>
              </Title>

              <Text component="div" className="hero-meta" data-hero-swap>
                <MetaList
                  inline
                  items={[
                    year,
                    chapters != null ? (
                      volumes != null ? (
                        <Trans key="count">
                          <Plural value={chapters} one="# chapter" other="# chapters" /> in{' '}
                          <Plural value={volumes} one="# volume" other="# volumes" />
                        </Trans>
                      ) : (
                        plural(chapters, { one: '# chapter', other: '# chapters' })
                      )
                    ) : volumes != null ? (
                      plural(volumes, { one: '# volume', other: '# volumes' })
                    ) : null,
                  ]}
                />
              </Text>

              <div data-hero-swap>
                <HeroChips
                  status={detail?.status ?? item.status}
                  contentRating={detail?.contentRating}
                  typeLabel={typeLabel}
                  hasAnime={detail?.hasAnime}
                >
                  {ownedSeriesId != null && ownedArchived && (
                    <span className="series-hero-status" data-quiet>
                      <IconArchive size={14} />
                      <Trans context="series">Archived</Trans>
                    </span>
                  )}
                </HeroChips>
              </div>

              <div className="hero-scoreline" data-hero-swap>
                <div className="discover-hero-score">
                  {score != null && <ScoreFigure score={score} standing={detail?.scoreStanding} />}
                </div>
                <div className="discover-hero-sites">
                  {links.length > 0 ? (
                    <MetadataLinks links={links} chips extra={unlinked} />
                  ) : (
                    unlinked.length > 0 && (
                      <Group gap={6} wrap="wrap" align="center">
                        {unlinked}
                      </Group>
                    )
                  )}
                </div>
              </div>

              <Group gap="xs" className="discover-hero-actions">
                {/* Adding needs a root folder, the caller's permissions and the request path for
                    non-admins, all of which `DiscoverLibraryRail` handles inside the detail card. */}
                {ownedSeriesId != null && ownedArchived ? (
                  <>
                    <RestoreArchivedButton seriesId={ownedSeriesId} aria-label={t({ message: `Restore ${title}`, context: 'series' })} />
                    <Button
                      variant="default"
                      rightSection={<IconArrowRight size={16} />}
                      onClick={() => navigate(`/series/${ownedSeriesId}`)}
                      aria-label={t`View series: ${title}`}
                    >
                      <Trans>View series</Trans>
                    </Button>
                  </>
                ) : ownedSeriesId != null ? (
                  <Button
                    variant="light"
                    rightSection={<IconArrowRight size={16} />}
                    onClick={() => navigate(`/series/${ownedSeriesId}`)}
                    aria-label={t`View in library: ${title}`}
                  >
                    <Trans>View in library</Trans>
                  </Button>
                ) : (
                  <Button
                    leftSection={<IconPlus size={16} />}
                    onClick={() => onOpen(item)}
                    aria-label={t`Add to library: ${title}`}
                  >
                    <Trans>Add to library</Trans>
                  </Button>
                )}
                <Button variant="default" onClick={() => onRecommend(item)}>
                  <Trans>More like this</Trans>
                </Button>
              </Group>
            </div>
            </Group>
          </div>

          <div className="discover-hero-strip">
            <div className="discover-hero-strip-heading">
              <Text className="discover-hero-strip-label">
                <Trans>Also for you</Trans>
              </Text>
              {canAutoRotate && (
                <div className="discover-hero-strip-controls">
                  <Text className="discover-hero-strip-mode">
                    {paused || userPaused ? <Trans>Paused</Trans> : <Trans>Auto</Trans>}
                  </Text>
                  <ActionIcon
                    className="discover-hero-pause"
                    variant="subtle"
                    color="var(--ink-2)"
                    size={26}
                    aria-label={userPaused ? t`Resume rotating picks` : t`Pause rotating picks`}
                    aria-pressed={userPaused}
                    onClick={() => setUserPaused((p) => !p)}
                  >
                    {userPaused ? <IconPlayerPlayFilled size={14} /> : <IconPlayerPauseFilled size={14} />}
                  </ActionIcon>
                </div>
              )}
            </div>
            <div
              className="discover-hero-strip-row"
              role="tablist"
              aria-label={t`Other picks for you`}
              onKeyDown={onRovingKeyDown}
            >
              {picks.map((p, n) => {
                const pickStatus = seriesStatusVisual(p.status)
                const matches = [...p.matchedTags, ...p.matchedGenres].slice(0, 3)
                const thumbnail = p.thumbUrl ?? p.coverUrl

                return (
                  <button
                    key={p.providerId}
                    type="button"
                    role="tab"
                    aria-selected={n === active}
                    tabIndex={n === active ? 0 : -1}
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
                        {p.rating != null && <ScoreInline score={p.rating} />}
                      </span>
                      {matches.length > 0 && (
                        <MetaList
                          className="discover-hero-strip-match"
                          items={matches.map((m) => (
                            <span key={m}>{m}</span>
                          ))}
                        />
                      )}
                    </span>
                    {n === active && canAutoRotate && !halted && (
                      <span className="discover-hero-strip-progress" aria-hidden>
                        <span key={current.providerId} />
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
