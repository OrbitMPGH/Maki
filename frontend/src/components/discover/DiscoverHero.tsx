import { useCallback, useEffect, useState, type CSSProperties } from 'react'
import { Badge, Box, Button, Group, Stack, Text, Title, Tooltip } from '@mantine/core'
import { IconPlus, IconStar } from '@tabler/icons-react'
import { AnimatePresence, motion, useReducedMotion } from 'motion/react'
import { useRecommendationDetail, type RecommendationItem } from '../../api/hooks'
import { MetadataSiteIcon } from '../MetadataSiteIcon'
import { HeroBackdrop } from '../series/HeroBackdrop'
import {
  contentRatingToken,
  contentRatingVisual,
  ratingBandVisual,
  seriesStatusVisual,
  statusToken,
} from '../ui/status'

/** How long one pick holds the band before the next takes it. */
const ROTATE_MS = 7000

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
}: {
  /** The picks to rotate through. Rendered from the first; anything past six is ignored. */
  items: RecommendationItem[]
  /** Opens the detail modal. The band has no add controls of its own — see below. */
  onOpen: (item: RecommendationItem) => void
}) {
  const picks = items.slice(0, 6)
  const [active, setActive] = useState(0)
  const [paused, setPaused] = useState(false)
  const [autoRotate, setAutoRotate] = useState(true)
  const reducedMotion = useReducedMotion()
  const canAutoRotate = picks.length > 1 && autoRotate && !reducedMotion

  const item = picks[active] as RecommendationItem | undefined
  const { data: detail } = useRecommendationDetail(item?.providerId ?? null)

  const choose = useCallback((n: number) => {
    setAutoRotate(false)
    setActive(n)
  }, [])

  useEffect(() => {
    if (!canAutoRotate || paused) return

    const id = window.setTimeout(() => setActive((n) => (n + 1) % picks.length), ROTATE_MS)
    return () => window.clearTimeout(id)
  }, [active, canAutoRotate, paused, picks.length])

  if (!item) return null

  // Everything here is on the rail item as well as the detail response, so the band is complete on
  // the first frame and the detail request fills in behind it rather than rearranging it.
  const cover = item.thumbUrlHiDpi ?? item.coverUrl ?? null
  const status = seriesStatusVisual(detail?.status ?? item.status)
  const contentRating = contentRatingVisual(detail?.contentRating ?? null)
  const ratingToken = contentRatingToken(detail?.contentRating)
  const score = detail?.rating ?? item.rating
  const band = ratingBandVisual(score ?? 0)
  const figures = [
    { label: 'Released', value: detail?.year ?? item.year },
    { label: 'Chapters', value: detail?.totalChapters ?? item.totalChapters },
    { label: 'Volumes', value: detail?.finalVolume },
  ].filter((f): f is { label: string; value: number } => f.value != null)

  // The three "why" flavours, in the order of how much they claim: what readers did beats what they
  // said, and both beat proximity in the behavioural space.
  const reasons = [
    item.coRead ? 'Readers of your shelf finished it' : null,
    item.coRecommended ? 'Readers recommend it alongside yours' : null,
    item.tasteMatch ? 'Close to your reading in taste space' : null,
    item.becauseOfTitle ? `Because you read ${item.becauseOfTitle}` : null,
    item.relationKind && item.relatedToTitle
      ? `${item.relationKind} to ${item.relatedToTitle}`
      : null,
  ].filter((r): r is string => r != null)

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
            <Group align="flex-start" gap={26} wrap="nowrap">
            {cover && (
              <button
                type="button"
                className="discover-hero-poster"
                onClick={() => onOpen(item)}
                aria-label={`View ${item.title}`}
              >
                <img className="series-hero-poster" src={cover} alt="" />
              </button>
            )}

            <Stack gap={0} style={{ flex: 1, minWidth: 0 }}>
              <Text className="discover-hero-eyebrow">Your next read</Text>

              <Title order={1} className="series-hero-title">
                <button type="button" className="discover-hero-title" onClick={() => onOpen(item)}>
                  {item.title}
                </button>
              </Title>

              <Group gap={9} mt={14} wrap="wrap">
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
                )}
              </Group>

              <div className="hero-figures">
                {score != null && (
                  <span
                    className="hero-score"
                    style={{ '--band': `var(--${band.token})` } as CSSProperties}
                  >
                    <IconStar size={18} />
                    <span className="hero-score-n tnum">{(score / 10).toFixed(1)}</span>
                  </span>
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

              {/* Reserved height: the source scores arrive with the detail request, and a row that
                  appears from nothing shoves the buttons down mid-read. */}
              <Group gap="xs" align="center" mt={14} className="discover-hero-sources">
                {detail?.sourceRatings.map((r) => (
                  <Tooltip key={r.source} label={r.source} withArrow>
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

              {reasons.length > 0 && (
                <Group gap={7} mt={12} wrap="wrap">
                  {reasons.slice(0, 3).map((r) => (
                    <span key={r} className="discover-hero-reason">
                      {r}
                    </span>
                  ))}
                </Group>
              )}

              <Group gap="xs" mt="lg">
                {/* Both controls open the detail card. Adding needs a root folder, the caller's
                    permissions and the request path for non-admins, all of which
                    `DiscoverLibraryRail` already handles inside that card — a second copy here
                    would be the fork that drifts. */}
                <Button
                  leftSection={<IconPlus size={16} />}
                  onClick={() => onOpen(item)}
                  aria-label={`Add ${item.title} to library`}
                >
                  Add to library
                </Button>
                <Button variant="default" onClick={() => onOpen(item)}>
                  More like this
                </Button>
              </Group>
            </Stack>
            </Group>
          </motion.div>

          <div className="discover-hero-strip">
            <div className="discover-hero-strip-heading">
              <Text className="discover-hero-strip-label">Also for you</Text>
              {canAutoRotate && (
                <Text className="discover-hero-strip-mode">{paused ? 'Paused' : 'Auto'}</Text>
              )}
            </div>
            <div className="discover-hero-strip-row" role="tablist" aria-label="Other picks for you">
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
                        <span>{pickStatus.label}</span>
                        {p.rating != null && (
                          <span className="discover-hero-strip-rating tnum">
                            <IconStar size={11} />
                            {(p.rating / 10).toFixed(1)}
                          </span>
                        )}
                      </span>
                      {matches.length > 0 && (
                        <span className="discover-hero-strip-match">{matches.join(' · ')}</span>
                      )}
                    </span>
                    {n === active && canAutoRotate && !paused && (
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
