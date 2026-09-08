import { memo } from 'react'
import { useNavigate } from 'react-router-dom'
import {
  IconAffiliate,
  IconCheck,
  IconFeather,
  IconHeartFilled,
  IconPlus,
  IconSparkles,
  IconStar,
  IconUsers,
} from '@tabler/icons-react'
import type { Icon } from '@tabler/icons-react'
import type { RecommendationItem } from '../../api/hooks'

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

function reasonFor(item: RecommendationItem): string {
  if (item.relationKind) {
    return `${item.relationKind} of ${item.relatedToTitle}`
  }
  const parts: string[] = []
  if (item.authorMatch) parts.push('same author')
  const because = [...item.matchedGenres, ...item.matchedTags].slice(0, 3)
  if (because.length > 0) parts.push(because.join(', '))
  // Semantic picks name the seed whose feel drove them; genre-only hits keep "Because:".
  if (item.becauseOfTitle) {
    const feel = `Feels like ${item.becauseOfTitle}`
    return parts.length > 0 ? `${feel} · ${parts.join(' · ')}` : feel
  }
  return parts.length > 0 ? `Because: ${parts.join(' · ')}` : 'Similar feel'
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
}: {
  item: RecommendationItem
  /** Library series id if already owned (shows a persistent "in library" check); null otherwise. */
  inLibrarySeriesId: number | null
  onOpen: (item: RecommendationItem) => void
  /** Overrides the reason line: a string replaces it, `null` hides it. Omit for the default. */
  reasonOverride?: string | null
}) {
  const owned = inLibrarySeriesId != null
  const reason = reasonOverride !== undefined ? reasonOverride : reasonFor(item)

  return (
    <div className="cover-card discover-card">
      {/* One native control owns the whole poster. The corner glyphs are status/intent cues, not
          duplicate actions, so a card contributes one predictable stop to keyboard navigation. */}
      <button
        type="button"
        className="discover-card-action"
        aria-label={owned ? `View ${item.title}` : `View and add ${item.title}`}
        onClick={() => onOpen(item)}
      />
      <div className="cover-poster">
        {/* `thumbUrl` is a 167x250 cover, `thumbUrlHiDpi` its 334x500 twin, both from MangaBaka's
            image proxy; `coverUrl` is the raw art, which averages ~460x690 and is what the detail
            card wants. Rendering the raw one here cost ~2.5 MB of decoded RGBA per poster, which a
            240-card page could not keep in the browser's image cache — covers were evicted and
            re-decoded as you scrolled, which is what "the page can't keep up" looked like. The
            fallback matters: the title-search path has no thumbnail. */}
        {item.thumbUrl || item.coverUrl ? (
          <img
            src={item.thumbUrl ?? item.coverUrl ?? undefined}
            srcSet={
              item.thumbUrl && item.thumbUrlHiDpi
                ? `${item.thumbUrl} 1x, ${item.thumbUrlHiDpi} 2x`
                : undefined
            }
            alt={item.title}
            loading="lazy"
            decoding="async"
          />
        ) : (
          <div className="cover-placeholder">{item.title}</div>
        )}
        <div className="cover-scrim" />

        {item.rating != null && (
          <span className="cover-badge discover-rating">
            <IconStar size={10} style={{ color: '#f5c518' }} />
            {(item.rating / 10).toFixed(1)}
          </span>
        )}

        {owned ? (
          <span className="discover-corner" data-tip="In library" aria-hidden="true">
            <IconCheck size={16} />
          </span>
        ) : (
          <span
            className="discover-corner"
            data-add="true"
            data-tip="View & add"
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
          <div className="discover-sub">
            {item.year && <span className="tnum">{item.year}</span>}
            <span className="discover-sub-status">· {item.status}</span>
            {item.totalChapters && <span>· {item.totalChapters} ch</span>}
          </div>
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
}: {
  item: RecommendationItem
  inLibrarySeriesId: number | null
  density: 'compact' | 'default' | 'comfortable'
  onOpen: (item: RecommendationItem) => void
  reasonOverride?: string | null
}) {
  const navigate = useNavigate()
  const owned = inLibrarySeriesId != null
  const reason = reasonOverride !== undefined ? reasonOverride : reasonFor(item)
  const thumbSize = density === 'compact' ? 48 : density === 'comfortable' ? 72 : 56

  return (
    <div
      className={`series-row ${density}`}
      role="button"
      tabIndex={0}
      aria-label={item.title}
      onClick={() => (owned ? navigate(`/series/${inLibrarySeriesId}`) : onOpen(item))}
      onKeyDown={(e) => {
        if (e.key === 'Enter' || e.key === ' ') {
          e.preventDefault()
          owned ? navigate(`/series/${inLibrarySeriesId}`) : onOpen(item)
        }
      }}
    >
      <div className="row-cover" style={{ width: thumbSize, height: thumbSize * 1.5, flexShrink: 0 }}>
        {item.thumbUrl || item.coverUrl ? (
          <img
            src={item.thumbUrl ?? item.coverUrl ?? undefined}
            alt={item.title}
            loading="lazy"
            decoding="async"
          />
        ) : (
          <div className="row-cover-placeholder">{item.title}</div>
        )}
      </div>

      <div className="row-body">
        <div className="row-header">
          <span className="row-title" title={item.title}>
            {item.title}
          </span>
          {item.year && <span className="row-year">{item.year}</span>}
          <span className="cover-badge" style={{ flexShrink: 0 }}>
            {item.status}
          </span>
          {owned && (
            <span className="cover-badge cover-badge-circle" data-tip="In library" style={{ flexShrink: 0 }}>
              <IconCheck size={12} />
            </span>
          )}
        </div>

        {reason && <div className="row-description">{reason}</div>}

        <div className="row-progress">
          {item.rating != null && (
            <span className="cover-badge" style={{ flexShrink: 0 }}>
              <IconStar size={11} style={{ color: '#f5c518' }} />
              {(item.rating / 10).toFixed(1)}
            </span>
          )}
          {item.totalChapters != null && (
            <span className="cover-count tnum">{item.totalChapters} ch</span>
          )}
        </div>
      </div>
    </div>
  )
})


/* --- Engine rails ---------------------------------------------------------
   A second rail shape, for rows the recommender produced rather than rows the catalogue did.
   "Trending now" is a leaderboard and needs no defence; "Based on your recent activity" is a claim
   about this reader, and a claim you cannot see the grounds for is just a row of covers. So these
   cards are wider, and they carry the grounds under the poster instead of squeezed into the scrim
   where the catalogue card's optional reason line lives. */

/** The strongest thing the engine can say about one pick, and the icon that says which kind it is. */
function engineWhy(item: RecommendationItem): { Glyph: Icon; text: string } {
  if (item.relationKind && item.relatedToTitle) {
    return { Glyph: IconAffiliate, text: `${item.relationKind} to ${item.relatedToTitle}` }
  }
  if (item.becauseOfTitle) {
    return { Glyph: IconSparkles, text: `Feels like ${item.becauseOfTitle}` }
  }
  if (item.authorMatch) {
    return { Glyph: IconFeather, text: 'By an author you read' }
  }
  // The crowd signals come after the item-specific ones on purpose: on the cohort rail every pick
  // has `coRead`, and a card repeating its own heading twenty times says nothing.
  if (item.coRead) return { Glyph: IconUsers, text: 'Readers of your shelf finished it' }
  if (item.coRecommended) return { Glyph: IconUsers, text: 'Recommended alongside yours' }
  if (item.tasteMatch) return { Glyph: IconHeartFilled, text: 'Close to your taste' }
  return { Glyph: IconSparkles, text: 'Similar feel' }
}

/**
 * Poster card for an engine rail: the catalogue card's poster, plus a footer carrying why this
 * pick is here — one reason line and the tags it matched on.
 *
 * Memoized and built from plain elements for the same reasons `RecommendationCard` is.
 */
export const EngineCard = memo(function EngineCard({
  item,
  inLibrarySeriesId,
  onOpen,
}: {
  item: RecommendationItem
  inLibrarySeriesId: number | null
  onOpen: (item: RecommendationItem) => void
}) {
  const owned = inLibrarySeriesId != null
  const { Glyph, text } = engineWhy(item)
  // Tags before genres: "Time Loop" says what a pick is, "Action" says what a third of the
  // catalogue is. Two, because three at this width truncate to "Cl…", "Stud…", "High…".
  const chips = [...item.matchedTags, ...item.matchedGenres].slice(0, 2)

  return (
    <div className="cover-card discover-card engine-card">
      <button
        type="button"
        className="discover-card-action"
        aria-label={owned ? `View ${item.title}` : `View and add ${item.title}`}
        onClick={() => onOpen(item)}
      />
      <div className="cover-poster">
        {item.thumbUrl || item.coverUrl ? (
          <img
            src={item.thumbUrl ?? item.coverUrl ?? undefined}
            srcSet={
              item.thumbUrl && item.thumbUrlHiDpi
                ? `${item.thumbUrl} 1x, ${item.thumbUrlHiDpi} 2x`
                : undefined
            }
            alt={item.title}
            loading="lazy"
            decoding="async"
          />
        ) : (
          <div className="cover-placeholder">{item.title}</div>
        )}
        <div className="cover-scrim" />

        {item.rating != null && (
          <span className="cover-badge discover-rating">
            <IconStar size={10} style={{ color: '#f5c518' }} />
            {(item.rating / 10).toFixed(1)}
          </span>
        )}

        {owned ? (
          <span className="discover-corner" data-tip="In library" aria-hidden="true">
            <IconCheck size={16} />
          </span>
        ) : (
          <span className="discover-corner" data-add="true" data-tip="View & add" aria-hidden="true">
            <IconPlus size={16} />
          </span>
        )}

        <div className="discover-meta">
          <span className="cover-title" title={item.title}>
            {item.title}
          </span>
          <div className="discover-sub">
            {item.year && <span className="tnum">{item.year}</span>}
            <span className="discover-sub-status">· {item.status}</span>
            {item.totalChapters && <span>· {item.totalChapters} ch</span>}
          </div>
        </div>
      </div>

      <div className="engine-why">
        <span className="engine-why-line" title={text}>
          <Glyph size={13} />
          <span className="engine-why-text">{text}</span>
        </span>
        {chips.length > 0 && (
          <span className="engine-why-chips">
            {chips.map((c) => (
              <span key={c} className="engine-chip" title={c}>
                {c}
              </span>
            ))}
          </span>
        )}
      </div>
    </div>
  )
})

/**
 * A horizontal rail of {@link EngineCard}s. Same scroller as {@link DiscoverRailRow}, wider items.
 *
 * Use it for rows the *recommender* produced: Discover's "based on your recent activity", the
 * series page's "more like this", Home's "you might like". Catalogue rows (Trending, Popular, a
 * genre) stay on {@link DiscoverRailRow} — they are rankings, not claims about the reader — and so
 * does the reader-cohort rail, whose items hydrate from the dump and carry none of the per-item
 * grounds this card is built to show.
 */
export function EngineRailRow({
  items,
  seriesIdFor,
  onOpen,
}: {
  items: RecommendationItem[]
  seriesIdFor: (item: RecommendationItem) => number | null
  onOpen: (item: RecommendationItem) => void
}) {
  return (
    <div className="discover-rail" data-engine>
      {items.map((item) => (
        <div key={item.providerId} className="discover-rail-item">
          <EngineCard item={item} inLibrarySeriesId={seriesIdFor(item)} onOpen={onOpen} />
        </div>
      ))}
    </div>
  )
}

/** A single horizontal-scroll rail of poster cards. */
export function DiscoverRailRow({
  items,
  seriesIdFor,
  onOpen,
  showReason = false,
}: {
  items: RecommendationItem[]
  seriesIdFor: (item: RecommendationItem) => number | null
  onOpen: (item: RecommendationItem) => void
  /**
   * Rails hide the reason line by default: a catalogue rail is a row of covers you skim, and every
   * card carrying "Because: Action, Drama" is noise where the rail's own heading already said why
   * these are here. A rail whose picks need defending individually — "More like this", where the
   * whole point is which parts of the seed a candidate picked up — opts in.
   */
  showReason?: boolean
}) {
  return (
    <div className="discover-rail">
      {items.map((item) => (
        <div key={item.providerId} className="discover-rail-item">
          <RecommendationCard
            item={item}
            inLibrarySeriesId={seriesIdFor(item)}
            onOpen={onOpen}
            reasonOverride={showReason ? undefined : null}
          />
        </div>
      ))}
    </div>
  )
}
