import { useId, useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { Button, Group, Text, UnstyledButton } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'
import { IconArrowsShuffle, IconCompass, IconStack } from '../../../lib/icons'
import type { RecommendationItem, TasteGroup, TasteMember, TasteSideLane } from '../../../api/hooks'
import { TYPE_LABELS } from '../../../components/CatalogueFilters'
import { useLabel } from '../../../i18n-context'
import { DiscoverRailRow, type OpenItem } from '../../../components/ui/DiscoverRail'
import { sectionSource } from '../../../api/shortlist'
import { Panel } from '../../../components/ui/Panel'
import { formatPercent } from '../../../format'
import { SeriesLink, SeriesThumb } from '../../stats/SeriesLink'

type Seed = { id: number; title: string | null }

/**
 * Coherence in words, and only at the ends. The raw cosine means nothing to a reader, and "fairly
 * alike", the middle band most groups land in, says nothing a reader can use. A group defined by a
 * tag its members all carry starts out tight, which is why the bands sit high.
 */
function useCoherenceWord() {
  const { t } = useLingui()
  return (coherence: number) =>
    coherence >= 0.84 ? t`very alike` : coherence >= 0.76 ? null : t`loosely related`
}

function ThemeRow({
  group,
  maxShare,
  selected,
  controls,
  onSelect,
}: {
  group: TasteGroup
  maxShare: number
  selected: boolean
  controls: string
  onSelect: () => void
}) {
  const word = useCoherenceWord()(group.coherence)
  const seriesCount = plural(group.size, { one: '# series', other: '# series' })
  const width = maxShare > 0 ? Math.max(4, Math.min(100, (group.share / maxShare) * 100)) : 0
  return (
    <UnstyledButton
      className="taste-theme-row"
      aria-pressed={selected}
      aria-controls={controls}
      onClick={onSelect}
    >
      <span className="taste-theme-text">
        <span className="taste-theme-name">{group.label}</span>
        <span className="taste-theme-meta tnum">
          {seriesCount}
          {word && <> · {word}</>}
        </span>
      </span>
      <span className="taste-theme-bar" aria-hidden>
        <b style={{ width: `${width}%` }} />
      </span>
      <span className="taste-theme-pct tnum">{formatPercent(group.share)}</span>
    </UnstyledButton>
  )
}

function ThemeStage({
  group,
  id,
  covered,
  onOpen,
  seriesIdFor,
  onRecommend,
}: {
  group: TasteGroup
  id: string
  covered: number
  onOpen: OpenItem
  seriesIdFor: (item: RecommendationItem) => number | null
  onRecommend: (group: TasteGroup) => void
}) {
  const size = group.size
  const ofYours = plural(covered, {
    one: `${size} of your # series`,
    other: `${size} of your # series`,
  })
  return (
    <Panel id={id} className="taste-stage">
      <div className="taste-stage-head">
        {/* Only the name is announced on a switch, not every cover title and pick under it. */}
        <h3 className="taste-stage-title" aria-live="polite">
          {group.label}
        </h3>
        <Text span className="taste-stage-meta tnum">
          {ofYours}
        </Text>
        {group.seedIds.length > 0 && (
          <Button
            variant="default"
            size="compact-sm"
            leftSection={<IconCompass size={14} />}
            className="taste-stage-action"
            onClick={() => onRecommend(group)}
          >
            <Trans>More like this</Trans>
          </Button>
        )}
      </div>
      <div className="taste-stage-covers">
        {group.examples.map((m: TasteMember) => (
          // The library card's poster, scrim and title, without its status and download marks:
          // a theme member carries none of that, only what the card needs to be named.
          <Link key={m.seriesId} to={`/series/${m.seriesId}`} className="cover-card">
            <div className="cover-poster">
              {m.coverUrl ? (
                <img src={m.coverUrl} alt="" loading="lazy" decoding="async" />
              ) : (
                <div className="cover-placeholder" aria-hidden>
                  {m.title}
                </div>
              )}
              <div className="cover-scrim" />
              <div className="cover-meta">
                <span className="cover-title">{m.title}</span>
              </div>
            </div>
          </Link>
        ))}
      </div>
      {group.picks.length > 0 && (
        <>
          <Text size="sm" fw={600} mt="lg" mb={6}>
            <Trans>More of this</Trans>{' '}
            <Text span size="sm" fw={400} c="var(--ink-4)">
              <Trans>not in your library</Trans>
            </Text>
          </Text>
          <DiscoverRailRow
            items={group.picks}
            seriesIdFor={seriesIdFor}
            onOpen={onOpen}
            savedFrom={sectionSource('taste')}
          />
        </>
      )}
    </Panel>
  )
}

/** "Action Manhwa", from the lane's type and its Action, Romance or neither bucket. */
function useLaneName() {
  const { t } = useLingui()
  const renderLabel = useLabel()
  return (lane: TasteSideLane) => {
    const type = lane.type ? renderLabel(TYPE_LABELS[lane.type] ?? lane.type) : t`Series`
    if (lane.bucket === 'action') return t`Action ${type}`
    if (lane.bucket === 'romance') return t`Romance ${type}`
    return t`${type} outside action and romance`
  }
}

/**
 * The reader's side tastes, which the themes above rarely name because they rank by how common a
 * facet is. Sits in the stage column, where the theme list leaves empty room.
 */
export function SideLanes({ lanes, onRecommendSeeds }: { lanes: TasteSideLane[]; onRecommendSeeds: (seeds: Seed[]) => void }) {
  const nameOf = useLaneName()
  return (
    <Panel p="md" className="taste-lanes">
      <Text size="sm" fw={600} className="taste-lanes-head">
        <IconStack size={16} className="taste-odd-icon" aria-hidden />
        <Trans>Outside your core</Trans>
      </Text>
      <div className="taste-lanes-row">
        {lanes.map((lane) => {
          const name = nameOf(lane)
          const { read, owned } = lane
          return (
            <div key={`${lane.type}:${lane.bucket}`} className="taste-lane">
              <span className="taste-lane-fan" aria-hidden>
                {lane.examples.map((m) => (
                  <span key={m.seriesId} className="taste-cover">
                    {m.coverUrl && <img src={m.coverUrl} alt="" loading="lazy" decoding="async" />}
                  </span>
                ))}
              </span>
              <span className="taste-lane-text">
                <span className="taste-lane-name">{name}</span>
                <Text span size="xs" c="var(--ink-3)" className="tnum">
                  <Trans>
                    {read} read of {owned} owned
                  </Trans>
                </Text>
                <Button
                  variant="subtle"
                  size="compact-xs"
                  leftSection={<IconCompass size={13} />}
                  className="taste-lane-action"
                  onClick={() =>
                    onRecommendSeeds(lane.seedIds.map((id) => ({
                      id,
                      title: lane.examples.find((m) => m.mangaBakaId === id)?.title ?? null,
                    })))
                  }
                >
                  <Trans>More from these</Trans>
                </Button>
              </span>
            </div>
          )
        })}
      </div>
    </Panel>
  )
}

/**
 * Recurring themes as a list beside a stage. Picking a row fills the stage with that theme's own
 * covers and its picks, so the page never grows or jumps and a theme always shows where it leads.
 * Under the list breakpoint the rows become a sideways strip above the stage.
 */
export function TasteThemes({
  groups,
  covered,
  oddOneOut,
  sideLanes,
  onOpen,
  seriesIdFor,
  onRecommend,
  onRecommendSeeds,
}: {
  groups: TasteGroup[]
  covered: number
  oddOneOut: TasteMember | null
  sideLanes: TasteSideLane[]
  onOpen: OpenItem
  seriesIdFor: (item: RecommendationItem) => number | null
  onRecommend: (group: TasteGroup) => void
  onRecommendSeeds: (seeds: Seed[]) => void
}) {
  const stageId = useId()
  const [selected, setSelected] = useState<string | null>(null)
  // The server ranks by how specific a theme is; the list reads as a ranking by share, so it is
  // sorted that way. The sort is stable, so equal shares keep the server's order.
  const ordered = useMemo(() => [...groups].sort((a, b) => b.share - a.share), [groups])
  const group = ordered.find((g) => g.label === selected) ?? ordered[0]
  const maxShare = Math.max(0, ...groups.map((g) => g.share))
  if (!group) return null

  return (
    <div className="taste-themes">
      <Panel p={0} className="taste-theme-list">
        {ordered.map((g) => (
          <ThemeRow
            key={g.label}
            group={g}
            maxShare={maxShare}
            selected={g.label === group.label}
            controls={stageId}
            onSelect={() => setSelected(g.label)}
          />
        ))}
      </Panel>
      <div className="taste-stage-col">
        <ThemeStage
          group={group}
          id={stageId}
          covered={covered}
          onOpen={onOpen}
          seriesIdFor={seriesIdFor}
          onRecommend={onRecommend}
        />
        {(oddOneOut || sideLanes.length > 0) && (
          <div className="taste-stage-foot">
            {oddOneOut && (
              <Panel p="md" className="taste-odd">
                <Text size="sm" fw={600} className="taste-lanes-head">
                  <IconArrowsShuffle size={16} className="taste-odd-icon" aria-hidden />
                  <Trans>The odd one out</Trans>
                </Text>
                <Group gap="sm" wrap="nowrap" align="center">
                  <SeriesThumb url={oddOneOut.coverUrl} alt={oddOneOut.title} large seriesId={oddOneOut.seriesId} />
                  <div className="taste-odd-text">
                    <Text size="sm" fw={600} lineClamp={2} className="taste-odd-title">
                      <SeriesLink id={oddOneOut.seriesId} title={oddOneOut.title} />
                    </Text>
                    <Text size="xs" c="var(--ink-3)">
                      <Trans>Least like anything else you read</Trans>
                    </Text>
                    <Button
                      variant="subtle"
                      size="compact-xs"
                      leftSection={<IconCompass size={13} />}
                      className="taste-lane-action"
                      onClick={() => onRecommendSeeds([{ id: oddOneOut.mangaBakaId, title: oddOneOut.title }])}
                    >
                      <Trans>More like it</Trans>
                    </Button>
                  </div>
                </Group>
              </Panel>
            )}
            {sideLanes.length > 0 && <SideLanes lanes={sideLanes} onRecommendSeeds={onRecommendSeeds} />}
          </div>
        )}
      </div>
    </div>
  )
}
