import { Text } from '@mantine/core'
import type { DiscoverRail } from '../../api/hooks'

/** Covers fanned behind each genre name. Three reads as a shelf; more is a smear at this size. */
const PEEK = 3

/**
 * Every genre as a tile, each showing the three most popular titles in it.
 *
 * <p>
 * This is what makes removing the Genres tab lossless: the tab was nineteen rails of forty, which
 * is a lot of page for a thing people use as a jumping-off point. The wall says the same nineteen
 * words, shows what is behind each, and opens the full rail on click.
 * </p>
 */
export function DiscoverGenreWall({
  rails,
  onOpen,
}: {
  rails: DiscoverRail[] | undefined
  onOpen: (rail: DiscoverRail) => void
}) {
  if (!rails || rails.length === 0) return null

  return (
    <div className="discover-genre-wall">
      {rails.map((rail) => (
        <button
          key={rail.key}
          type="button"
          className="discover-genre-tile"
          onClick={() => onOpen(rail)}
        >
          <span className="discover-genre-stack" aria-hidden>
            {rail.items.slice(0, PEEK).map((item, n) => (
              <span
                key={item.providerId}
                className="discover-genre-peek"
                style={{ '--n': n } as React.CSSProperties}
              >
                {item.thumbUrl && (
                  <img src={item.thumbUrl} alt="" loading="lazy" decoding="async" />
                )}
              </span>
            ))}
          </span>
          <span className="discover-genre-text">
            <Text className="discover-genre-name">{rail.genre ?? rail.title}</Text>
            <Text className="discover-genre-top">{rail.items[0]?.title ?? ''}</Text>
          </span>
        </button>
      ))}
    </div>
  )
}
