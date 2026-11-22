import type { CSSProperties } from 'react'
import { Link } from 'react-router-dom'
import { Button, Text, Tooltip } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'
import type {
  RecommendationFilters,
  TasteFacet,
  TastePopularity,
  TasteProfile,
  TasteUnderFacet,
} from '../../../api/hooks'
import { IconChevronRight } from '../../../lib/icons'
import { Panel } from '../../../components/ui/Panel'
import { PanelTitle } from '../../../components/ui/PanelTitle'
import { GENRE_LABELS } from '../../../components/CatalogueFilters'
import { useLabel } from '../../../i18n-context'
import { formatFixedDecimal, formatNumber, formatPercent } from '../../../format'

/**
 * A facet only counts as reached for from here. Between roughly 1 and this a facet is proportional,
 * and labelling that "1.1x" invites reading noise as a preference.
 */
const NOTEWORTHY = 1.25
const ROWS = 8
const MAX_SCALE = 6

interface ReachRow {
  facet: TasteFacet
  kind: 'genre' | 'tag'
  shelf: number | null
  catalogue: number | null
}

function ratioText(value: number): string {
  return `${value >= 10 ? formatNumber(Math.round(value)) : formatFixedDecimal(value, 1)}x`
}

/**
 * Genres and tags read more than a baseline predicts, against the shelf or the catalogue, one row
 * each, strongest first. A row needs one noteworthy ratio; the other is drawn whatever its value.
 */
function reachRows(profile: TasteProfile): ReachRow[] {
  const catalogueOf = (facet: TasteFacet) => (profile.catalogueBaselineAvailable ? facet.overIndexCatalogue : null)
  const strongest = (row: ReachRow) => Math.max(row.shelf ?? 0, row.catalogue ?? 0)
  return [
    ...profile.genres.map((facet) => ({ facet, kind: 'genre' as const })),
    ...profile.tags.map((facet) => ({ facet, kind: 'tag' as const })),
  ]
    .map((row) => ({ ...row, shelf: row.facet.overIndexShelf, catalogue: catalogueOf(row.facet) }))
    .filter((row) => strongest(row) >= NOTEWORTHY)
    .sort((a, b) => strongest(b) - strongest(a))
    .slice(0, ROWS)
}

/**
 * One ratio as a bar from zero, with its value at the end. Between 1x and the noise line it is drawn
 * faint; below 1x the slot stays empty so the rows keep one height.
 */
function RatioBar({
  kind,
  value,
  scale,
  tip,
}: {
  kind: 'shelf' | 'catalogue'
  value: number | null
  scale: number
  tip: string
}) {
  // Below 1x the facet is rarer in the reading than the baseline, which this list is not about.
  if (value === null || value < 1) return <span className="taste-reach-bar" data-kind={kind} />
  return (
    <Tooltip label={tip} multiline w={280} withArrow>
      <span className="taste-reach-bar" data-kind={kind} data-weak={value < NOTEWORTHY || undefined} tabIndex={0}>
        <b style={{ width: `calc((100% - var(--reach-label)) * ${Math.min(1, value / scale)})` }} />
        <span className="tnum">{ratioText(value)}</span>
      </span>
    </Tooltip>
  )
}

/**
 * The reach list: per genre or tag, how many times more of the reading carries it than the shelf
 * would predict (accent, the reader) and than the catalogue would (grey), against a 1x line where a
 * facet is exactly as common as expected. Each row leads to Find more.
 */
function ReachList({
  profile,
  onFind,
}: {
  profile: TasteProfile
  onFind: (filters: RecommendationFilters) => void
}) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const rows = reachRows(profile)
  const withCatalogue = profile.catalogueBaselineAvailable
  // One outlier (a 30x tag the catalogue barely carries) would flatten every other bar, so the
  // scale stops at MAX_SCALE and a longer ratio fills its bar with the true value printed.
  const scale = Math.min(MAX_SCALE, Math.max(2, ...rows.map((row) => Math.max(row.shelf ?? 0, row.catalogue ?? 0))))
  const catalogueTip =
    profile.catalogueBaselineSource === 'popularity'
      ? t`The catalogue is weighted by popularity rank, so it stands for what most people read.`
      : t`The catalogue is weighted toward titles more people read.`

  return (
    <Panel className="taste-reach">
      <PanelTitle>
        <Trans>More than you'd expect</Trans>
      </PanelTitle>
      <Text size="sm" c="var(--ink-3)" mt={4}>
        <Trans>How many times more of what you read carries each one than your shelf or the catalogue would predict.</Trans>
      </Text>
      <div className="taste-reach-key">
        <span data-kind="shelf">
          <i aria-hidden />
          <Trans>Against your shelf</Trans>
        </span>
        {withCatalogue && (
          <Tooltip label={catalogueTip} multiline w={260} withArrow>
            <span data-kind="catalogue" tabIndex={0}>
              <i aria-hidden />
              <Trans>Against the catalogue</Trans>
            </span>
          </Tooltip>
        )}
        <span data-kind="one">
          <i aria-hidden />
          <Trans>1x is as expected</Trans>
        </span>
      </div>
      {rows.length === 0 ? (
        <Text size="sm" c="var(--ink-3)" mt="sm">
          <Trans>Nothing stands out against your shelf or the catalogue yet.</Trans>
        </Text>
      ) : (
        <div className="taste-reach-rows" style={{ '--reach-one': 1 / scale } as CSSProperties}>
          {rows.map((row, i) => {
            const { facet } = row
            const name = row.kind === 'genre' ? renderLabel(GENRE_LABELS[facet.name] ?? facet.name) : facet.name
            const series = plural(facet.support, { one: '# series', other: '# series' })
            const shelfValue = row.shelf === null ? '' : ratioText(row.shelf)
            const catalogueValue = row.catalogue === null ? '' : ratioText(row.catalogue)
            return (
              <div key={`${row.kind}:${facet.name}`} className="taste-reach-row" style={{ gridRow: i + 1 }}>
                <Text size="sm" fw={600} lineClamp={2} className="taste-reach-name" title={name}>
                  {name}
                </Text>
                <span className="taste-reach-bars">
                  <RatioBar
                    kind="shelf"
                    value={row.shelf}
                    scale={scale}
                    tip={t`${name} turns up ${shelfValue} as often in what you read as on your shelf, across ${series}`}
                  />
                  {withCatalogue && (
                    <RatioBar
                      kind="catalogue"
                      value={row.catalogue}
                      scale={scale}
                      tip={t`${name} turns up ${catalogueValue} as often in what you read as in the MangaBaka catalogue`}
                    />
                  )}
                </span>
                <Button
                  variant="subtle"
                  size="compact-sm"
                  className="taste-reach-find"
                  onClick={() => onFind(row.kind === 'genre' ? { genres: [facet.name] } : { tags: [facet.name] })}
                >
                  <Trans>Find more</Trans>
                </Button>
              </div>
            )
          })}
          <span className="taste-reach-one" aria-hidden style={{ gridRow: `1 / span ${rows.length}` }} />
        </div>
      )}
      {!withCatalogue && (
        <Text size="xs" c="var(--ink-4)" className="taste-reach-note">
          <Trans>Comparisons against the wider catalogue need the embedding index.</Trans>
        </Text>
      )}
    </Panel>
  )
}

/** The server's popularity band limits (`TasteProfileService.PopularityBands`), for the labels. */
const POPULARITY_BANDS = [100, 1000, 10000]

/** A top-1,000 share gap below this is not worth a sentence. */
const POPULARITY_GAP = 0.1

/**
 * Where the reading and the shelf sit in MangaBaka's popularity ranking, as two bars cut into the
 * same four bands, with the reader's deepest cuts and the way to the obscurity dial.
 */
function HowDeep({ popularity }: { popularity: TastePopularity }) {
  const { t } = useLingui()
  const [top100, top1000, top10000] = POPULARITY_BANDS.map((limit) => formatNumber(limit))
  const bands = [t`Top ${top100}`, t`Top ${top1000}`, t`Top ${top10000}`, t`Deeper`]
  const inTopThousand = (shares: number[]) => shares[0] + shares[1]
  const shelfTop = inTopThousand(popularity.shelf)
  const readTop = inTopThousand(popularity.read)
  const shelfPct = formatPercent(shelfTop)
  const readPct = formatPercent(readTop)
  const gap = Math.abs(shelfTop - readTop) >= POPULARITY_GAP
  const rows = [
    { key: 'read', label: t`You read`, shares: popularity.read },
    { key: 'shelf', label: t`Your shelf`, shares: popularity.shelf },
  ]

  return (
    <Panel className="taste-deep">
      <div className="taste-deep-head">
        <PanelTitle>
          <Trans>How deep you go</Trans>
        </PanelTitle>
        <Button
          component={Link}
          to="/discover/recommended"
          variant="subtle"
          size="compact-xs"
          color="var(--ink-2)"
          rightSection={<IconChevronRight size={14} />}
        >
          <Trans>Obscurity dial in Recommended</Trans>
        </Button>
      </div>
      {gap && (
        <Text size="sm" c="var(--ink-2)">
          <Trans>
            {shelfPct} of your shelf is in the catalogue's top {top1000}. Of what you read, {readPct}.
          </Trans>
        </Text>
      )}
      {rows.map((row) => (
        <div key={row.key} className="taste-deep-row">
          <span className="taste-deep-label">{row.label}</span>
          <span className="taste-deep-bar">
            {row.shares.map((share, i) =>
              share > 0 ? (
                <Tooltip key={i} label={`${bands[i]}: ${formatPercent(share)}`} withArrow>
                  <i data-band={i} style={{ flexGrow: share }}>
                    {share >= 0.12 && <span className="tnum">{formatPercent(share)}</span>}
                  </i>
                </Tooltip>
              ) : null,
            )}
          </span>
        </div>
      ))}
      <div className="taste-deep-key">
        {bands.map((band, i) => (
          <span key={band}>
            <i data-band={i} aria-hidden />
            {band}
          </span>
        ))}
      </div>
      {popularity.deepCuts.length > 0 && (
        <div className="taste-deep-side">
          <Text size="xs" fw={600} c="var(--ink-3)">
            <Trans>Your deepest cuts, by catalogue popularity</Trans>
          </Text>
          <div className="taste-deep-cuts">
            {popularity.deepCuts.map((cut) => {
              const rank = formatNumber(cut.rank)
              return (
                <Link key={cut.seriesId} to={`/series/${cut.seriesId}`} className="taste-deep-cut">
                  <span className="taste-cover" aria-hidden>
                    {cut.coverUrl && <img src={cut.coverUrl} alt="" loading="lazy" decoding="async" />}
                  </span>
                  <span className="taste-deep-cut-text">
                    <span className="taste-deep-cut-title">{cut.title}</span>
                    <span className="tnum">
                      <Trans>#{rank}</Trans>
                    </span>
                  </span>
                </Link>
              )
            })}
          </div>
        </div>
      )}
    </Panel>
  )
}

/**
 * The mirror of "more than your shelf predicts": genres and story tags the shelf holds far more of
 * than the reading does, each with the unread series waiting in it. Here the bar is the shelf and
 * the tick is the reading.
 */
function RarelyOpened({ facets }: { facets: TasteUnderFacet[] }) {
  const renderLabel = useLabel()
  return (
    <Panel className="taste-under">
      <div className="taste-under-head">
        <PanelTitle>
          <Trans>You collect these, but rarely open them</Trans>
        </PanelTitle>
        <Text size="xs" c="var(--ink-4)">
          <Trans>The bar is your shelf. The tick is your reading.</Trans>
        </Text>
      </div>
      <div className="taste-under-row">
        {facets.map((facet) => {
          const name = facet.isGenre ? renderLabel(GENRE_LABELS[facet.name] ?? facet.name) : facet.name
          const owned = formatNumber(facet.owned)
          const read = formatNumber(facet.read)
          const more = facet.unread - facet.examples.length
          return (
            <div key={`${facet.isGenre}:${facet.name}`} className="taste-under-pocket">
              <div className="taste-under-title">
                <span className="taste-under-name">{name}</span>
                <Text span size="xs" c="var(--ink-3)" className="tnum">
                  <Trans>
                    {owned} owned, {read} read
                  </Trans>
                </Text>
                <span className="taste-under-ratio tnum">{ratioText(facet.ratio)}</span>
              </div>
              <span className="taste-under-track" aria-hidden>
                <b />
                <i style={{ left: `${Math.min(100, facet.ratio * 100)}%` }} />
              </span>
              <div className="taste-under-covers">
                {facet.examples.map((ex) => (
                  <Link key={ex.seriesId} to={`/series/${ex.seriesId}`} className="taste-cover" title={ex.title} aria-label={ex.title}>
                    {ex.coverUrl && <img src={ex.coverUrl} alt="" loading="lazy" decoding="async" />}
                  </Link>
                ))}
                {more > 0 && <span className="taste-under-more tnum">+{formatNumber(more)}</span>}
              </div>
            </div>
          )
        })}
      </div>
    </Panel>
  )
}

/**
 * What the reader reads more of than a baseline predicts, against their own shelf and against the
 * catalogue, each with a way to find more. The plain counts are on the Stats page.
 */
export function TasteReach({
  profile,
  onFind,
}: {
  profile: TasteProfile
  onFind: (filters: RecommendationFilters) => void
}) {
  return (
    <>
      <div className="taste-reach-grid" data-single={!profile.popularity || undefined}>
        <ReachList profile={profile} onFind={onFind} />
        {profile.popularity && <HowDeep popularity={profile.popularity} />}
      </div>
      {profile.underRead && profile.underRead.length > 0 && <RarelyOpened facets={profile.underRead} />}
    </>
  )
}
