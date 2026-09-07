import { Paper, Text } from '@mantine/core'
import type { DiscoverRail, DiscoverSeedState, RecommendationItem } from '../../api/hooks'

/** Picks shown per seed. The cell has room for three; the rail keeps the rest for "Show more". */
const PER_SEED = 3

const STATE_LABEL: Record<DiscoverSeedState['state'], string> = {
  reading: 'Reading',
  'caught-up': 'Caught up',
  finished: 'Finished',
}

/**
 * One card per recently-read series, each headed by the seed and listing what it pointed at.
 *
 * <p>
 * The flat rail this replaces said "Because you read A, B and C" once and then showed forty picks
 * with no way to tell which seed produced which. Splitting it is the whole point: the attribution
 * already exists on every pick, and a card headed by a series you read is a stronger claim than a
 * subtitle naming three of them.
 * </p>
 */
export function DiscoverSeedGrid({
  rails,
  onOpen,
}: {
  rails: DiscoverRail[]
  onOpen: (item: RecommendationItem) => void
}) {
  if (rails.length === 0) return null

  return (
    <div className="discover-seed-grid">
      {rails.map((rail) => (
        <Paper key={rail.key} withBorder radius="lg" className="discover-seed-card">
          <div className="discover-seed-head">
            <Text className="discover-seed-title" title={rail.seed?.title}>
              {rail.seed?.title ?? rail.title}
            </Text>
            {rail.seed && <SeedProgress seed={rail.seed} />}
          </div>

          <div className="discover-seed-picks">
            {rail.items.slice(0, PER_SEED).map((item) => (
              <button
                key={item.providerId}
                type="button"
                className="discover-seed-pick"
                onClick={() => onOpen(item)}
              >
                {item.thumbUrl ? (
                  <img
                    className="discover-seed-cover"
                    src={item.thumbUrl}
                    srcSet={
                      item.thumbUrl && item.thumbUrlHiDpi
                        ? `${item.thumbUrl} 1x, ${item.thumbUrlHiDpi} 2x`
                        : undefined
                    }
                    alt=""
                    loading="lazy"
                    decoding="async"
                  />
                ) : (
                  <span className="discover-seed-cover" aria-hidden />
                )}
                <span className="discover-seed-pick-body">
                  <span className="discover-seed-pick-title">{item.title}</span>
                  <span className="discover-seed-pick-meta tnum">
                    {[
                      item.rating != null ? `★ ${(item.rating / 10).toFixed(1)}` : null,
                      item.year,
                      item.totalChapters != null
                        ? `${item.totalChapters.toLocaleString()} ch`
                        : null,
                    ]
                      .filter(Boolean)
                      .join(' · ')}
                  </span>
                </span>
              </button>
            ))}
          </div>
        </Paper>
      ))}
    </div>
  )
}

/**
 * How far through the seed the reader is. The pip differs in shape as well as colour — filled,
 * hollow, filled-teal — so the three states survive a monochrome display and colour-blind vision,
 * which a colour-only dot would not.
 */
function SeedProgress({ seed }: { seed: DiscoverSeedState }) {
  return (
    <span className="discover-seed-state">
      <i className="discover-seed-pip" data-state={seed.state} aria-hidden />
      {STATE_LABEL[seed.state]}
      <em className="tnum">
        {seed.state === 'reading'
          ? `ch ${seed.chaptersRead.toLocaleString()} of ${seed.chaptersAvailable.toLocaleString()}`
          : `${seed.chaptersRead.toLocaleString()} ch read`}
      </em>
    </span>
  )
}
