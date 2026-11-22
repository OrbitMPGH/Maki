import { useLayoutEffect, useRef } from 'react'
import { Box, Button, Group, Text, Title } from '@mantine/core'
import { IconArchive, IconArrowRight, IconPlus } from '../../lib/icons'
import type { Icon } from '../../lib/icons'
import { useNavigate } from 'react-router-dom'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'
import { useRecommendationDetail, type RecommendationItem } from '../../api/hooks'
import type { ShortlistSource } from '../../api/shortlist'
import { HeroBackdrop } from '../series/HeroBackdrop'
import { HeroChips } from '../series/HeroChips'
import { ScoreInline } from '../ui/ScoreInline'
import { TagChip } from '../ui/TagChip'
import { TYPE_LABELS } from '../CatalogueFilters'
import { ScoreFigure } from './ScoreFigure'
import { ShortlistButton } from './ShortlistButton'
import { StatusMark } from './StatusMark'
import { RestoreArchivedButton, useIsArchivedSeries } from './ArchivedRestore'
import { useLabel } from '../../i18n-context'
import { cleanSynopsis } from '../../lib/synopsis'
import { MetaList } from '../ui/MetaList'

/** One pick in Start here: the series, the line that says why it is there, and the section it came from. */
export interface StartPick {
  item: RecommendationItem
  reason: { Glyph: Icon; text: string } | null
  source: ShortlistSource
}

/**
 * The title sits in a box two display lines tall, so the band is the same height for every pick and
 * a swap never moves the page under someone scrolled past it. A title that does not fit is set
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
 * Start here: the top of Discover's Browse tab. One pick at full size and two beside it, each from
 * a different section of the page, and each with its synopsis so the reader can decide without
 * opening it.
 *
 * <p>
 * It is the `.series-hero[data-compact]` band the detail modal and the series page use, on the
 * same `HeroBackdrop`, so a pick, the card it opens and the series it becomes read as one object.
 * The two side picks sit on the opaque panel, because small synopsis text cannot sit on cover art.
 * Nothing rotates: the page swaps the set when the reader asks for other picks, and leaves these
 * three out of the rails below so nothing shows twice.
 * </p>
 */
export function DiscoverHero({
  lead,
  side,
  onOpen,
  onRecommend,
  seriesIdFor,
}: {
  lead: StartPick
  /** Up to two smaller picks for the panel. */
  side: StartPick[]
  /** Opens the detail modal, whose library rail owns the full add/request flow. */
  onOpen: (item: RecommendationItem, source: ShortlistSource) => void
  /** Opens Recommended with this title as its only seed. */
  onRecommend: (item: RecommendationItem) => void
  /** Library series id when the pick is already owned; the primary action then opens the series. */
  seriesIdFor?: (item: RecommendationItem) => number | null
}) {
  const navigate = useNavigate()
  const renderLabel = useLabel()
  const { t } = useLingui()
  const { item, reason } = lead
  const { data: detail } = useRecommendationDetail(item.providerId)
  const titleRef = useTitleFit(item.title)
  const ownedSeriesId = seriesIdFor?.(item) ?? null
  const ownedArchived = useIsArchivedSeries(ownedSeriesId)

  // Everything here is on the rail item as well as the detail response, so the band is complete on
  // the first frame and the detail request fills in behind it rather than rearranging it.
  const cover = item.thumbUrlHiDpi ?? item.coverUrl ?? null
  const { title } = item
  const score = detail?.rating ?? item.rating
  const year = detail?.year ?? item.year
  const chapters = detail?.totalChapters ?? item.totalChapters
  const volumes = detail?.finalVolume ?? null
  const typeLabel = detail?.type ? renderLabel(TYPE_LABELS[detail.type] ?? detail.type) : null
  const synopsis = cleanSynopsis(item.description ?? detail?.description)
  const tags = (item.matchedTags.length > 0 ? item.matchedTags : item.matchedGenres).slice(0, 4)
  const open = () => onOpen(item, lead.source)

  return (
    <Box className="series-hero discover-hero" data-compact data-side={side.length > 0 || undefined}>
      <div className="discover-hero-backdrop">
        <HeroBackdrop coverUrl={cover} />
      </div>

      <div className="series-hero-body">
        <div className="series-hero-content">
          <div className="discover-hero-feature">
            <Group align="flex-start" gap={26} wrap="nowrap" className="discover-hero-feature-row">
              {cover && (
                <button
                  type="button"
                  className="discover-hero-poster"
                  data-series-cover={ownedSeriesId ?? undefined}
                  onClick={open}
                  aria-label={t`View ${title}`}
                >
                  <img className="series-hero-poster" src={cover} alt="" />
                </button>
              )}

              <div className="hero-column hero-continue" style={{ flex: 1, minWidth: 0 }}>
                {reason && (
                  <div className="hero-kicker">
                    <reason.Glyph size={14} />
                    <span className="hero-kicker-text">{reason.text}</span>
                  </div>
                )}

                {/* The page's h1 is "Discover"; the featured pick is a section of it. */}
                <Title order={2} className="series-hero-title" ref={titleRef}>
                  <button type="button" className="discover-hero-title" title={title} onClick={open}>
                    {title}
                  </button>
                </Title>

                <div className="discover-hero-facts">
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
                  <Text component="div" className="hero-meta">
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
                </div>

                {synopsis && <p className="discover-hero-synopsis">{synopsis}</p>}

                {tags.length > 0 && (
                  <div className="discover-hero-tags">
                    {tags.map((tag) => (
                      <TagChip key={tag} size="sm">
                        {tag}
                      </TagChip>
                    ))}
                  </div>
                )}

                <div className="discover-hero-score">
                  {score != null && <ScoreFigure score={score} standing={detail?.scoreStanding} />}
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
                    <Button leftSection={<IconPlus size={16} />} onClick={open} aria-label={t`Add to library: ${title}`}>
                      <Trans>Add to library</Trans>
                    </Button>
                  )}
                  {ownedSeriesId == null && (
                    <ShortlistButton providerId={item.providerId} title={title} source={lead.source} inline />
                  )}
                  <Button variant="subtle" color="var(--ink-2)" onClick={() => onRecommend(item)}>
                    <Trans>More like this</Trans>
                  </Button>
                </Group>
              </div>
            </Group>
          </div>

          {side.length > 0 && (
            <div className="discover-hero-strip">
              {side.map((pick) => (
                <SidePick key={pick.item.providerId} pick={pick} onOpen={onOpen} />
              ))}
            </div>
          )}
        </div>
      </div>
    </Box>
  )
}

/** A smaller pick in the panel: cover, its reason, title, the facts that size it up, and its synopsis. */
function SidePick({
  pick,
  onOpen,
}: {
  pick: StartPick
  onOpen: (item: RecommendationItem, source: ShortlistSource) => void
}) {
  const { t } = useLingui()
  const { item, reason } = pick
  const thumbnail = item.thumbUrlHiDpi ?? item.thumbUrl ?? item.coverUrl
  const synopsis = cleanSynopsis(item.description)

  return (
    <button
      type="button"
      className="discover-start-pick"
      onClick={() => onOpen(item, pick.source)}
      aria-label={t`View ${item.title}`}
    >
      {thumbnail ? (
        <img src={thumbnail} alt="" loading="lazy" decoding="async" />
      ) : (
        <span className="discover-start-pick-blank" aria-hidden />
      )}
      <span className="discover-start-pick-copy">
        {reason && (
          <span className="discover-start-pick-reason">
            <reason.Glyph size={13} />
            <span>{reason.text}</span>
          </span>
        )}
        <span className="discover-start-pick-title">{item.title}</span>
        <MetaList
          inline
          className="discover-start-pick-meta"
          items={[
            <StatusMark key="status" status={item.status} />,
            item.totalChapters != null ? <Trans key="ch">{item.totalChapters} ch.</Trans> : null,
            item.rating != null ? <ScoreInline key="score" score={item.rating} /> : null,
          ]}
        />
        {synopsis && <span className="discover-start-pick-synopsis">{synopsis}</span>}
      </span>
    </button>
  )
}
