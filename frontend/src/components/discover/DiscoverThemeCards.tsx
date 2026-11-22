import { Button } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import type { DiscoverRail, RecommendationItem } from '../../api/hooks'
import { sectionSource } from '../../api/shortlist'
import { RecommendationCard, type OpenItem } from '../ui/DiscoverRail'
import { formatNumber } from '../../format'
import { DiscoverSeedStrip } from './DiscoverSeedStrip'
import { themeOf } from './discoverBrowse'

/** The most posters a theme card shows, on the widest card; CSS hides what doesn't fit one row. */
const SHOWN = 7

/**
 * "Another side of your library": each smaller theme on the reader's shelf as a card, side by
 * side, instead of a full rail per theme. A card names the theme, the reader's own series it came
 * from, and four picks; "See all" opens the theme's expanded view.
 */
export function DiscoverThemeCards({
  rails,
  seriesIdFor,
  onOpen,
  onShowMore,
}: {
  rails: DiscoverRail[]
  seriesIdFor: (item: RecommendationItem) => number | null
  onOpen: OpenItem
  onShowMore: (rail: DiscoverRail) => void
}) {
  const { t } = useLingui()
  return (
    <div className="discover-themes">
      {rails.map((rail) => {
        const theme = themeOf(rail)
        return (
          <section key={rail.key} className="discover-theme" aria-label={theme}>
            <div className="discover-theme-head">
              <h3 className="discover-theme-title">{theme}</h3>
              <span className="discover-theme-count tnum">{formatNumber(rail.items.length)}</span>
              <Button
                variant="subtle"
                size="xs"
                color="var(--ink-2)"
                className="discover-theme-more"
                onClick={() => onShowMore(rail)}
                aria-label={t`See all in ${theme}`}
              >
                <Trans>See all</Trans>
              </Button>
            </div>
            {rail.seedIds && rail.seedIds.length > 0 && (
              <DiscoverSeedStrip seedIds={rail.seedIds} label={t`From your library`} />
            )}
            <div className="discover-theme-grid">
              {rail.items.slice(0, SHOWN).map((item) => (
                <RecommendationCard
                  key={item.providerId}
                  item={item}
                  inLibrarySeriesId={seriesIdFor(item)}
                  onOpen={onOpen}
                  reasonOverride={null}
                  savedFrom={sectionSource('sideinterests')}
                />
              ))}
            </div>
          </section>
        )
      })}
    </div>
  )
}
