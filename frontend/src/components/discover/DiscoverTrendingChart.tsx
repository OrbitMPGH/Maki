import { Button } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import type { RecommendationItem } from '../../api/hooks'
import { sectionSource } from '../../api/shortlist'
import { IconArrowRight, IconArrowUp, IconPlus } from '../../lib/icons'
import { formatNumber } from '../../format'
import { cleanSynopsis } from '../../lib/synopsis'
import { ScoreInline } from '../ui/ScoreInline'
import { MetaList } from '../ui/MetaList'
import type { OpenItem } from '../ui/DiscoverRail'
import { ShortlistButton } from './ShortlistButton'
import { StatusMark } from './StatusMark'

/** The most ranks on the page, on the widest chart: number one plus five columns of three. CSS hides
 *  the columns a narrower chart has no room for, down to three (the top ten). */
const SHOWN = 16

/**
 * Trending as a chart rather than another poster rail. Number one is the chart's first and largest
 * entry, with its cover and synopsis; the next ranks follow in columns of three read top to bottom. Every
 * entry carries how far it climbed this month, which is what puts it on the chart.
 */
export function DiscoverTrendingChart({
  items,
  seriesIdFor,
  onOpen,
}: {
  items: RecommendationItem[]
  seriesIdFor: (item: RecommendationItem) => number | null
  onOpen: OpenItem
}) {
  const { t } = useLingui()
  const [first, ...rest] = items.slice(0, SHOWN)
  if (!first) return null
  const source = sectionSource('trending')
  const cover = first.thumbUrlHiDpi ?? first.coverUrl ?? first.thumbUrl
  const owned = seriesIdFor(first) != null
  const synopsis = cleanSynopsis(first.description)

  return (
    <div className="discover-chart">
      <div className="discover-chart-first">
        <span className="discover-chart-rank" aria-label={t`Number 1`}>
          1
        </span>
        <button type="button" className="discover-chart-poster" onClick={() => onOpen(first, source)} aria-label={t`View ${first.title}`}>
          {cover ? <img src={cover} alt="" loading="lazy" decoding="async" /> : <span className="discover-chart-blank" />}
        </button>
        <div className="discover-chart-first-copy">
          <button type="button" className="discover-chart-first-title" onClick={() => onOpen(first, source)}>
            {first.title}
          </button>
          <ChartFacts item={first} />
          <Climb value={first.climb} long />
          {synopsis && <p className="discover-chart-synopsis">{synopsis}</p>}
          <div className="discover-chart-actions">
            {owned ? (
              <Button size="sm" variant="light" rightSection={<IconArrowRight size={14} />} onClick={() => onOpen(first, source)}>
                <Trans>View in library</Trans>
              </Button>
            ) : (
              <>
                <Button size="sm" leftSection={<IconPlus size={14} />} onClick={() => onOpen(first, source)}>
                  <Trans>Add to library</Trans>
                </Button>
                <ShortlistButton providerId={first.providerId} title={first.title} source={source} compact />
              </>
            )}
          </div>
        </div>
      </div>
      <ol className="discover-chart-rest" start={2}>
        {rest.map((item, i) => {
          const thumbnail = item.thumbUrl ?? item.coverUrl
          return (
            <li key={item.providerId}>
              <button type="button" className="discover-chart-row" onClick={() => onOpen(item, source)}>
                <span className="discover-chart-row-rank tnum" aria-hidden>
                  {i + 2}
                </span>
                {thumbnail ? (
                  <img src={thumbnail} srcSet={item.thumbUrlHiDpi ? `${item.thumbUrlHiDpi} 2x` : undefined} alt="" loading="lazy" decoding="async" />
                ) : (
                  <span className="discover-chart-blank" />
                )}
                <span className="discover-chart-row-copy">
                  <span className="discover-chart-row-title">{item.title}</span>
                  <ChartFacts item={item} />
                  <Climb value={item.climb} />
                </span>
              </button>
            </li>
          )
        })}
      </ol>
    </div>
  )
}

function ChartFacts({ item }: { item: RecommendationItem }) {
  return (
    <MetaList
      inline
      className="discover-chart-facts"
      items={[
        <StatusMark key="status" status={item.status} word={false} />,
        item.totalChapters != null ? <Trans key="ch">{item.totalChapters} ch.</Trans> : null,
        item.rating != null ? <ScoreInline key="score" score={item.rating} /> : null,
      ]}
    />
  )
}

/** The month's climb. The section header says what the number counts, so a row shows only the figure. */
function Climb({ value, long }: { value?: number | null; long?: boolean }) {
  if (value == null) return null
  const places = formatNumber(value)
  return (
    <span className="discover-chart-climb tnum">
      <IconArrowUp size={12} />
      {long ? <Trans>{places} places this month</Trans> : places}
    </span>
  )
}
