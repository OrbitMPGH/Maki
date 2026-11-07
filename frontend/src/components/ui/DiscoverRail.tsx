import { memo, type CSSProperties } from 'react'
import { useNavigate } from 'react-router-dom'
import {
  IconAffiliate,
  IconBookmark,
  IconBookmarkFilled,
  IconCheck,
  IconFeather,
  IconHeartFilled,
  IconPlus,
  IconIntersect,
  IconStarFilled,
  IconUsers,
} from '../../lib/icons'
import type { Icon } from '../../lib/icons'
import { useLibraryCoverLookup, type RecommendationItem } from '../../api/hooks'
import { formatFixedDecimal } from '../../format'
import { Rail } from './Rail'
import { seriesStatusGlyph, seriesStatusVisual, statusColor, statusOnColor } from './status'
import { useLabel } from '../../i18n-context'
import type { ShortlistSource } from '../../api/shortlist'
import { useShortlistToggle } from '../discover/shortlistActions'
import { t as now } from '@lingui/core/macro'
import { Trans, useLingui } from '@lingui/react/macro'

/**
 * Poster cards for catalogue (MangaBaka) items, and the horizontal rail that lays them out.
 *
 * Lives here rather than in DiscoverPage because three surfaces render these (Discover, the
 * series page's related rail and the Home dashboard) and importing them from a page module
 * dragged that page's filter panel and its Mantine sliders into every consumer's bundle.
 *
 * For *owned* series use `components/ui/CoverCard` instead; these take a catalogue item, which
 * has no library id, no download counts and no read progress.
 */

/**
 * "Sequel to X" and friends. `kind` is the server's wire value; each gets its own whole sentence
 * because the preposition belongs to the kind, and in most languages so does the word order.
 */
export function relationPhrase(kind: string, relatedTo: string): string {
  switch (kind) {
    case 'Sequel':
      return now`Sequel to ${relatedTo}`
    case 'Prequel':
      return now`Prequel to ${relatedTo}`
    case 'Spin-off':
      return now`Spin-off of ${relatedTo}`
    case 'Side story':
      return now`Side story of ${relatedTo}`
    case 'Main story':
      return now`Main story of ${relatedTo}`
    default:
      // An unknown wire value is not interpolated: it is English and would stay English.
      return now`Related to ${relatedTo}`
  }
}

/** The co-read reason, naming the reader's own title the crowd finished alongside this one when known. */
export function coReadPhrase(item: RecommendationItem): string {
  const finished = item.coReadWithTitle
  return finished
    ? now`Readers who finished ${finished} also finished this`
    : now`Readers like you also finished this`
}

function reasonFor(item: RecommendationItem): string {
  if (item.relationKind) {
    return relationPhrase(item.relationKind, item.relatedToTitle ?? '')
  }
  const parts: string[] = []
  if (item.authorMatch) parts.push(now`same author`)
  const because = [...item.matchedGenres, ...item.matchedTags].slice(0, 3)
  if (because.length > 0) parts.push(because.join(', '))
  // Semantic picks name the seed whose feel drove them; genre-only hits keep "Because:".
  if (item.becauseOfTitle) {
    const { becauseOfTitle } = item
    const feel = now`Feels like ${becauseOfTitle}`
    const feelReasons = parts.join(', ')
    return parts.length > 0 ? now`${feel}, ${feelReasons}` : feel
  }
  if (parts.length > 0) {
    const reasonList = parts.join(', ')
    return now`Because: ${reasonList}`
  }
  return now`Similar feel`
}

function posterUrl(item: RecommendationItem): string | null {
  return item.thumbUrlHiDpi ?? item.thumbUrl ?? item.coverUrl
}

function RatingChip({ rating }: { rating: number }) {
  return (
    <span className="cover-badge discover-rating">
      <IconStarFilled size={12} style={{ color: 'var(--rating)' }} />
      {formatFixedDecimal(rating / 10)}
    </span>
  )
}

/** The opener learns where a card sits alongside the item, so the modal saves it with the right provenance. */
export type OpenItem = (item: RecommendationItem, source?: ShortlistSource) => void

function openWith(onOpen: OpenItem, item: RecommendationItem, savedFrom?: ShortlistSource) {
  if (savedFrom) onOpen(item, savedFrom)
  else onOpen(item)
}

function cardLabel(title: string, owned: boolean, saved: boolean): string {
  if (owned) return now`View ${title}`
  return saved ? now`View ${title}, on your shortlist` : now`View and add ${title}`
}

/**
 * The poster's save control. A real button that comes after `.discover-card-action` in the DOM, so
 * it is the card's second keyboard stop, and stacks above that overlay with `z-index`. It is only
 * drawn under hover or focus on a device that hovers; a touch screen saves from the modal.
 */
function CardSaveButton({ title, saved, onToggle }: { title: string; saved: boolean; onToggle: () => void }) {
  const { t } = useLingui()
  return (
    <button
      type="button"
      className="discover-card-save"
      aria-pressed={saved}
      aria-label={saved ? t`Remove ${title} from your shortlist` : t`Shortlist ${title}`}
      data-tip={saved ? t`Remove from shortlist` : t`Shortlist`}
      onClick={onToggle}
    >
      {saved ? <IconBookmarkFilled size={16} /> : <IconBookmark size={16} />}
    </button>
  )
}

/** Status disc and word, then the year, on the left; "103 ch." on the right. The year drops out
 *  first on a narrow card, so the status always survives. */
function DiscoverSub({ item }: { item: RecommendationItem }) {
  const label = useLabel()
  const { year, status, totalChapters } = item
  const visual = seriesStatusVisual(status)
  const Glyph = seriesStatusGlyph(status)
  return (
    <div className="discover-sub">
      <span className="discover-sub-status">
        <span className="discover-sub-state">
          <span
            className="discover-sub-disc"
            style={{ '--tone': statusColor(visual.color), '--tone-on': statusOnColor(visual.color) } as CSSProperties}
            aria-hidden
          >
            <Glyph size={10} />
          </span>
          <span className="discover-sub-word">{label(visual.label)}</span>
        </span>
        {year ? <span className="discover-sub-year">{year}</span> : null}
      </span>
      {totalChapters ? (
        <span className="discover-sub-chapters">
          <Trans>{totalChapters} ch.</Trans>
        </span>
      ) : null}
    </div>
  )
}

/** Poster-forward Discover card. Cover art is the hero; a bottom scrim carries the
 *  reason line, title and meta, and a corner control quick-opens (or navigates when owned).
 *
 *  Memoized because Discover mounts hundreds of these at once: without it, any state change on
 *  an ancestor (a keystroke in the search box, say) reconciles every card on the page.
 *
 *  Built from plain elements + CSS classes rather than Mantine's Badge/Tooltip/ActionIcon/Text,
 *  for the same reason `CoverCard` is (see the note there): each Mantine component resolves its
 *  styles API per instance and each Tooltip mounts a floating-ui instance, and the Discover tab
 *  puts 240 of these on the page at once. Measured on that tab: 561 ms and a 138 ms long task to
 *  mount them the Mantine way. Tooltips come from the app-wide delegated `TipLayer` via `data-tip`. */
export const RecommendationCard = memo(function RecommendationCard({
  item,
  inLibrarySeriesId,
  onOpen,
  reasonOverride,
  savedFrom,
}: {
  item: RecommendationItem
  /** Library series id if already owned (shows a persistent "in library" check); null otherwise. */
  inLibrarySeriesId: number | null
  onOpen: OpenItem
  /** Overrides the reason line: a string replaces it, `null` hides it. Omit for the default. */
  reasonOverride?: string | null
  /** Where this card is shown, kept with the title when it is shortlisted. Keep it referentially stable. */
  savedFrom?: ShortlistSource
}) {
  const { t } = useLingui()
  const owned = inLibrarySeriesId != null
  const reason = reasonOverride !== undefined ? reasonOverride : reasonFor(item)
  const { title } = item
  const { saved, usable, toggle } = useShortlistToggle(item.providerId, title, savedFrom)

  return (
    <div className="cover-card discover-card">
      {/* One native control owns the poster; the corner glyphs are status/intent cues, not
          duplicate actions. The one exception is the save button after it, so an unowned card is
          two keyboard stops: open, then shortlist. */}
      <button
        type="button"
        className="discover-card-action"
        aria-label={cardLabel(title, owned, saved)}
        onClick={() => openWith(onOpen, item, savedFrom)}
      />
      {!owned && usable && <CardSaveButton title={title} saved={saved} onToggle={toggle} />}
      <div className="cover-poster">
        {/* Use the 334x500 proxy variant as the card baseline: the 167x250 version looks soft on
            the wider default and comfortable cards. Keep the raw ~460x690 art for the detail card
            and as a fallback only; using it for every catalogue poster made a 240-card page too
            expensive to keep decoded. */}
        {posterUrl(item) ? (
          <img
            src={posterUrl(item) ?? undefined}
            alt=""
            loading="lazy"
            decoding="async"
          />
        ) : (
          <div className="cover-placeholder" aria-hidden>{item.title}</div>
        )}
        <div className="cover-scrim" />

        {item.rating != null && <RatingChip rating={item.rating} />}

        {owned ? (
          <span className="discover-corner" data-owned="true" data-tip={t`In library`} aria-hidden="true">
            <IconCheck size={16} />
          </span>
        ) : saved ? (
          <span className="discover-corner" data-saved="true" data-tip={t`On your shortlist`} aria-hidden="true">
            <IconBookmarkFilled size={16} />
          </span>
        ) : (
          <span
            className="discover-corner"
            data-add="true"
            data-tip={t`View and add`}
            aria-hidden="true"
          >
            <IconPlus size={16} />
          </span>
        )}

        <div className="discover-meta">
          {reason && (
            <span className="discover-reason" title={reason}>
              {reason}
            </span>
          )}
          <span className="cover-title" title={item.title}>
            {item.title}
          </span>
          <DiscoverSub item={item} />
        </div>
      </div>
    </div>
  )
})

/** List-view row for a catalogue item, mirroring `SeriesRow`'s layout/classes so grid/list toggle
 *  reads as the same feature across Library and Discover. Owned items link into the library;
 *  unowned ones open the detail modal instead, same split as the poster card's corner control. */
export const RecommendationRow = memo(function RecommendationRow({
  item,
  inLibrarySeriesId,
  density,
  onOpen,
  reasonOverride,
  savedFrom,
}: {
  item: RecommendationItem
  inLibrarySeriesId: number | null
  density: 'compact' | 'default' | 'comfortable'
  onOpen: OpenItem
  reasonOverride?: string | null
  savedFrom?: ShortlistSource
}) {
  const navigate = useNavigate()
  const { t } = useLingui()
  const label = useLabel()
  const owned = inLibrarySeriesId != null
  const reason = reasonOverride !== undefined ? reasonOverride : reasonFor(item)
  const thumbSize = density === 'compact' ? 48 : density === 'comfortable' ? 72 : 56
  const { totalChapters, title } = item
  const { saved, usable, toggle } = useShortlistToggle(item.providerId, title, savedFrom)

  // Same shape as the card: one button covers the row and opens it, and the save button is a
  // sibling stacked above it, so no control sits inside another.
  return (
    <div className={`series-row discover-row ${density}`}>
      <button
        type="button"
        className="discover-row-action"
        aria-label={cardLabel(title, owned, saved)}
        onClick={() => (owned ? navigate(`/series/${inLibrarySeriesId}`) : openWith(onOpen, item, savedFrom))}
      />
      <div className="row-cover" data-series-cover={inLibrarySeriesId ?? undefined} style={{ width: thumbSize, height: thumbSize * 1.5, flexShrink: 0 }}>
        {posterUrl(item) ? (
          <img
            src={posterUrl(item) ?? undefined}
            alt=""
            loading="lazy"
            decoding="async"
          />
        ) : (
          <div className="row-cover-placeholder" aria-hidden>{item.title}</div>
        )}
      </div>

      <div className="row-body">
        <div className="row-header">
          <span className="row-title" title={item.title}>
            {item.title}
          </span>
          {item.year && <span className="row-year">{item.year}</span>}
          <span className="cover-badge" style={{ flexShrink: 0 }}>
            {label(seriesStatusVisual(item.status).label)}
          </span>
          {owned && (
            <span className="cover-badge cover-badge-circle" data-tip={t`In library`} style={{ flexShrink: 0 }}>
              <IconCheck size={12} />
            </span>
          )}
        </div>

        {reason && <div className="row-description">{reason}</div>}

        <div className="row-progress">
          {item.rating != null && (
            <span className="cover-badge" style={{ flexShrink: 0 }}>
              <IconStarFilled size={11} style={{ color: 'var(--rating)' }} />
              {formatFixedDecimal(item.rating / 10, 1)}
            </span>
          )}
          {totalChapters != null && (
            <span className="cover-count tnum">
              <Trans>{totalChapters} ch.</Trans>
            </span>
          )}
        </div>
      </div>

      {!owned && usable && (
        <button
          type="button"
          className="row-shortlist"
          aria-pressed={saved}
          aria-label={saved ? t`Remove ${title} from your shortlist` : t`Shortlist ${title}`}
          data-tip={saved ? t`Remove from shortlist` : t`Shortlist`}
          onClick={toggle}
        >
          {saved ? <IconBookmarkFilled size={16} /> : <IconBookmark size={16} />}
        </button>
      )}
    </div>
  )
})


/* --- Engine rails ---------------------------------------------------------
   A second rail shape, for rows the recommender produced rather than rows the catalogue did.
   "Trending now" is a leaderboard and needs no defence; "Based on your recent activity" is a claim
   about this reader, and a claim you cannot see the grounds for is just a row of covers. So these
   cards are wider, and they carry the grounds under the poster instead of squeezed into the scrim
   where the catalogue card's optional reason line lives. */

/** The relation as a label that sits above the related title rather than around it. */
function relationLabel(kind: string): string {
  switch (kind) {
    case 'Sequel':
      return now`Sequel to`
    case 'Prequel':
      return now`Prequel to`
    case 'Spin-off':
      return now`Spin-off of`
    case 'Side story':
      return now`Side story of`
    case 'Main story':
      return now`Main story of`
    default:
      return now`Related to`
  }
}

type EngineWhy = {
  Glyph: Icon
  /** The whole sentence, for the tooltip and for reasons that name no series. */
  text: string
  /** Reasons that name one of the reader's series split into the kind and the series itself. */
  seed?: { label: string; title: string; id: string | null; thumb: string | null }
  /** The same reason said of a whole rail, for when every card in it would carry it. */
  rail?: string
}

/** The strongest thing the engine can say about one pick, and the icon that says which kind it is. */
function engineWhy(item: RecommendationItem): EngineWhy {
  if (item.relationKind && item.relatedToTitle) {
    return {
      Glyph: IconAffiliate,
      text: relationPhrase(item.relationKind, item.relatedToTitle),
      rail: relationPhrase(item.relationKind, item.relatedToTitle),
      seed: {
        label: relationLabel(item.relationKind),
        title: item.relatedToTitle,
        id: item.relatedToId ?? null,
        thumb: item.relatedToThumbUrl ?? null,
      },
    }
  }
  if (item.becauseOfTitle) {
    const { becauseOfTitle } = item
    return {
      Glyph: IconIntersect,
      text: now`Feels like ${becauseOfTitle}`,
      rail: now`These feel like ${becauseOfTitle}`,
      seed: {
        label: now`Feels like`,
        title: becauseOfTitle,
        id: item.becauseOfId ?? null,
        thumb: item.becauseOfThumbUrl ?? null,
      },
    }
  }
  if (item.authorMatch) {
    return { Glyph: IconFeather, text: now`By an author you read` }
  }
  // The crowd signals come after the item-specific ones on purpose: on the cohort rail every pick
  // has `coRead`, and a card repeating its own heading twenty times says nothing.
  if (item.coRead && item.coReadWithTitle) {
    const finished = item.coReadWithTitle
    return {
      Glyph: IconUsers,
      text: coReadPhrase(item),
      rail: now`Readers who finished ${finished} also finished these`,
      seed: {
        label: now`Fans of`,
        title: item.coReadWithTitle,
        id: item.coReadWithId ?? null,
        thumb: item.coReadWithThumbUrl ?? null,
      },
    }
  }
  if (item.coRead) return { Glyph: IconUsers, text: coReadPhrase(item) }
  if (item.coRecommended) return { Glyph: IconUsers, text: now`Readers like you also recommended this` }
  if (item.tasteMatch) return { Glyph: IconHeartFilled, text: now`Close to your taste` }
  return { Glyph: IconIntersect, text: now`Similar feel` }
}

/**
 * The reason every card in a rail shares, or null when any card differs. A rail seeded from one
 * title (or one where the crowd pairs every pick with the same series) would otherwise print the
 * same "Fans of X" under every poster.
 */
function sharedWhy(items: RecommendationItem[]): EngineWhy | null {
  if (items.length < 2) return null
  const key = (why: EngineWhy) => (why.seed && why.rail ? `${why.seed.label}|${why.seed.id ?? why.seed.title}` : null)
  const first = engineWhy(items[0])
  const firstKey = key(first)
  if (firstKey == null) return null
  return items.every((item) => key(engineWhy(item)) === firstKey) ? first : null
}

/**
 * The cover of the series an engine card's reason names: the library's own when the reader owns it,
 * since that is the cover they know it by, else the catalogue's for a seed picked by hand.
 */
export function useSeedCover() {
  const coverFor = useLibraryCoverLookup()
  return (item: RecommendationItem) => {
    const seed = engineWhy(item).seed
    return seed ? coverFor(seed.id) ?? seed.thumb : null
  }
}

/**
 * Poster card for an engine rail: the catalogue card's poster, plus a footer carrying why this
 * pick is here: one reason line and the tags it matched on.
 *
 * Memoized and built from plain elements for the same reasons `RecommendationCard` is.
 */
export const EngineCard = memo(function EngineCard({
  item,
  inLibrarySeriesId,
  onOpen,
  seedCoverUrl,
  savedFrom,
  reasonInRail = false,
}: {
  item: RecommendationItem
  inLibrarySeriesId: number | null
  onOpen: OpenItem
  /** Cover of the library series the reason names. It stays recognisable when a long title is cut off. */
  seedCoverUrl?: string | null
  savedFrom?: ShortlistSource
  /** The rail already states this card's reason, so the footer carries only the matched tags. */
  reasonInRail?: boolean
}) {
  const { t } = useLingui()
  const owned = inLibrarySeriesId != null
  const { Glyph, text, seed } = engineWhy(item)
  const { title } = item
  const { saved, usable, toggle } = useShortlistToggle(item.providerId, title, savedFrom)
  // Tags before genres: "Time Loop" says what a pick is, "Action" says what a third of the
  // catalogue is. The chip row is one line tall and wraps, so only chips that fit whole show.
  // matchedTags is filtered against this series' spoiler flags by both recommendation paths.
  // Keep using that display-safe list rather than the detail view's full tag collection.
  const chips = [...item.matchedTags, ...item.matchedGenres].slice(0, 3)

  return (
    <div className="cover-card discover-card engine-card">
      <button
        type="button"
        className="discover-card-action"
        aria-label={cardLabel(title, owned, saved)}
        onClick={() => openWith(onOpen, item, savedFrom)}
      />
      {!owned && usable && <CardSaveButton title={title} saved={saved} onToggle={toggle} />}
      <div className="cover-poster">
        {posterUrl(item) ? (
          <img
            src={posterUrl(item) ?? undefined}
            alt=""
            loading="lazy"
            decoding="async"
          />
        ) : (
          <div className="cover-placeholder" aria-hidden>{item.title}</div>
        )}
        <div className="cover-scrim" />

        {item.rating != null && <RatingChip rating={item.rating} />}

        {owned ? (
          <span className="discover-corner" data-owned="true" data-tip={t`In library`} aria-hidden="true">
            <IconCheck size={16} />
          </span>
        ) : saved ? (
          <span className="discover-corner" data-saved="true" data-tip={t`On your shortlist`} aria-hidden="true">
            <IconBookmarkFilled size={16} />
          </span>
        ) : (
          <span className="discover-corner" data-add="true" data-tip={t`View and add`} aria-hidden="true">
            <IconPlus size={16} />
          </span>
        )}

        <div className="discover-meta">
          <span className="cover-title" title={item.title}>
            {item.title}
          </span>
          <DiscoverSub item={item} />
        </div>
      </div>

      {reasonInRail ? (
        chips.length > 0 && (
          <div className="engine-why">
            <span className="engine-why-chips">
              {chips.map((c) => (
                <span key={c} className="engine-chip" title={c}>
                  {c}
                </span>
              ))}
            </span>
          </div>
        )
      ) : (
        <div className="engine-why">
          {seed ? (
            <span className="engine-why-seed-row" title={text}>
              {seedCoverUrl && (
                <img className="engine-why-seed" src={seedCoverUrl} alt="" loading="lazy" decoding="async" />
              )}
              <span className="engine-why-seed-body">
                <span className="engine-why-kind">
                  <Glyph size={12} />
                  <span>{seed.label}</span>
                </span>
                <span className="engine-why-seed-title">{seed.title}</span>
              </span>
            </span>
          ) : (
            <span className="engine-why-line" title={text}>
              <Glyph size={13} />
              <span className="engine-why-text">{text}</span>
            </span>
          )}
          {!seed && chips.length > 0 && (
            <span className="engine-why-chips">
              {chips.map((c) => (
                <span key={c} className="engine-chip" title={c}>
                  {c}
                </span>
              ))}
            </span>
          )}
        </div>
      )}
    </div>
  )
})

/**
 * A horizontal rail of {@link EngineCard}s. Same scroller as {@link DiscoverRailRow}, wider items.
 *
 * Use it for rows the *recommender* produced: Discover's "based on your recent activity", the
 * series page's "more like this", Home's "you might like". Catalogue rows (Trending, Popular, a
 * genre) stay on {@link DiscoverRailRow}, they are rankings, not claims about the reader, and so
 * does the reader-cohort rail, whose items hydrate from the dump and carry none of the per-item
 * grounds this card is built to show.
 */
export function EngineRailRow({
  items,
  seriesIdFor,
  onOpen,
  savedFrom,
}: {
  items: RecommendationItem[]
  seriesIdFor: (item: RecommendationItem) => number | null
  onOpen: OpenItem
  savedFrom?: ShortlistSource
}) {
  const seedCover = useSeedCover()
  const shared = sharedWhy(items)
  const sharedCover = shared ? seedCover(items[0]) : null
  const SharedGlyph = shared?.Glyph
  return (
    <>
      {shared && SharedGlyph && (
        <p className="engine-rail-reason">
          {sharedCover && <img src={sharedCover} alt="" loading="lazy" decoding="async" />}
          <SharedGlyph size={14} />
          <span className="engine-rail-reason-text" title={shared.rail}>{shared.rail}</span>
        </p>
      )}
      <Rail engine>
        {items.map((item) => (
          <div key={item.providerId} className="discover-rail-item">
            <EngineCard
              item={item}
              inLibrarySeriesId={seriesIdFor(item)}
              onOpen={onOpen}
              seedCoverUrl={seedCover(item)}
              savedFrom={savedFrom}
              reasonInRail={shared != null}
            />
          </div>
        ))}
      </Rail>
    </>
  )
}

/** A single horizontal-scroll rail of poster cards. */
export function DiscoverRailRow({
  items,
  seriesIdFor,
  onOpen,
  showReason = false,
  savedFrom,
}: {
  items: RecommendationItem[]
  seriesIdFor: (item: RecommendationItem) => number | null
  onOpen: OpenItem
  savedFrom?: ShortlistSource
  /**
   * Rails hide the reason line by default: a catalogue rail is a row of covers you skim, and every
   * card carrying "Because: Action, Drama" is noise where the rail's own heading already said why
   * these are here. A rail whose picks need defending individually, "More like this", where the
   * whole point is which parts of the seed a candidate picked up, opts in.
   */
  showReason?: boolean
}) {
  return (
    <Rail>
      {items.map((item) => (
        <div key={item.providerId} className="discover-rail-item">
          <RecommendationCard
            item={item}
            inLibrarySeriesId={seriesIdFor(item)}
            onOpen={onOpen}
            reasonOverride={showReason ? undefined : null}
            savedFrom={savedFrom}
          />
        </div>
      ))}
    </Rail>
  )
}
