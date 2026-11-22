import { Link } from 'react-router-dom'
import { Button, Group, SegmentedControl, Text, Tooltip } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'
import { IconAdjustmentsHorizontal, IconFilter, IconLock, IconThumbDown, IconThumbUp } from '../../../lib/icons'
import type { TasteInsights, TasteMember, TasteProfile, TasteView } from '../../../api/hooks'
import { useFeedbackLab } from '../../../api/recommendationFeedback'
import { DiscoverTasteStrip } from '../../../components/discover/DiscoverTasteStrip'
import { Panel } from '../../../components/ui/Panel'
import { formatNumber, formatPercent } from '../../../format'
import { AvoidRow } from '../FeedbackLab'
import { useManageSignals } from '../useManageSignals'

const HEADLINE = 3
const COLLAGE = 8

interface HeadlineLine {
  label: string
  share: number | null
}

/**
 * The three themes that lead the headline. Single-facet groups read as a name; pairs ("Coming of Age
 * + Award Winning") only fill in when there are not three singles. Without themes (no index, thin
 * library) the profile's most common tags stand in.
 */
function headline(insights: TasteInsights | undefined, profile: TasteProfile | undefined): {
  lines: HeadlineLine[]
  fromThemes: boolean
} {
  const groups = insights?.groups ?? []
  if (groups.length > 0) {
    const byShare = [...groups].sort((a, b) => b.share - a.share)
    const ordered = [...byShare.filter((g) => g.tags.length === 1), ...byShare.filter((g) => g.tags.length !== 1)]
    return { lines: ordered.slice(0, HEADLINE).map((g) => ({ label: g.label, share: g.share })), fromThemes: true }
  }
  const count = profile?.seriesCount ?? 0
  const lines = (profile?.tags ?? [])
    .slice(0, HEADLINE)
    .map((f) => ({ label: f.name, share: count > 0 ? f.support / count : null }))
  return { lines, fromThemes: false }
}

/** Covers from the leading themes, nearest each theme's centre first, no series twice. */
function collage(insights: TasteInsights | undefined): TasteMember[] {
  const groups = [...(insights?.groups ?? [])].sort((a, b) => b.share - a.share)
  const seen = new Set<number>()
  const picked: TasteMember[] = []
  // Round-robin so the first three themes each get a face before any theme gets a second.
  const depth = Math.max(0, ...groups.map((g) => g.examples.length))
  for (let i = 0; i < depth && picked.length < COLLAGE; i++) {
    for (const group of groups) {
      const member = group.examples[i]
      if (!member || seen.has(member.seriesId) || !member.coverUrl) continue
      seen.add(member.seriesId)
      picked.push(member)
      if (picked.length >= COLLAGE) break
    }
  }
  return picked
}

/**
 * The first screen of the Taste tab: what the reader reads, in a sentence they would recognise, with
 * the covers that make it true and the two ways onward (recommend from it, change what steers it).
 */
export function TastePortrait({
  view,
  onView,
  insights,
  profile,
  canRecommend,
  onRecommend,
}: {
  view: TasteView
  onView: (view: TasteView) => void
  insights: TasteInsights | undefined
  profile: TasteProfile | undefined
  canRecommend: boolean
  onRecommend: () => void
}) {
  const { t } = useLingui()
  const { data: lab } = useFeedbackLab()
  const { open: manage, modal } = useManageSignals()
  const signals = lab?.capabilities.labUi ? lab : undefined
  const { lines, fromThemes } = headline(insights, profile)
  const covers = collage(insights)

  const readCount = profile?.seriesCount ?? 0
  const shelfCount = profile?.libraryCount ?? 0
  const allCount = profile?.seriesCount ?? 0
  const lead = !profile
    ? null
    : fromThemes
      ? view === 'read'
        ? plural(shelfCount, {
            one: `Your biggest themes, from ${readCount} of your # series.`,
            other: `Your biggest themes, from ${readCount} of your # series.`,
          })
        : plural(allCount, {
            one: 'Your biggest themes, across all # series you own.',
            other: 'Your biggest themes, across all # series you own.',
          })
      : view === 'read'
        ? plural(shelfCount, {
            one: `Your most common tags, from ${readCount} of your # series.`,
            other: `Your most common tags, from ${readCount} of your # series.`,
          })
        : plural(allCount, {
            one: 'Your most common tags, across all # series you own.',
            other: 'Your most common tags, across all # series you own.',
          })

  const summary = signals?.summary
  const liked = summary?.liked ?? 0
  const disliked = summary?.disliked ?? 0
  const pushingDown = summary?.pushingDown ?? 0
  const suppressed = (summary?.hidden ?? 0) + (summary?.dismissed ?? 0)

  return (
    <Panel p={{ base: 'md', sm: 'xl' }} className="taste-portrait">
      <div className="taste-portrait-main">
        <Group justify="space-between" gap="sm" wrap="wrap">
          <span className="taste-private">
            <IconLock size={14} aria-hidden />
            <Trans>Only you can see this</Trans>
          </span>
          <SegmentedControl
            aria-label={t`View`}
            size="xs"
            value={view}
            onChange={(v) => onView(v as TasteView)}
            data={[
              { value: 'read', label: t`What you've read` },
              { value: 'shelf', label: t`Everything you own` },
            ]}
          />
        </Group>

        {lines.length > 0 && (
          <h2 className="taste-headline">
            {lines.map((line) => (
              <span key={line.label} className="taste-headline-line">
                <span className="taste-headline-name">{line.label}.</span>
                {line.share !== null && <span className="taste-headline-share tnum">{formatPercent(line.share)}</span>}
              </span>
            ))}
          </h2>
        )}
        {lead && <Text className="taste-portrait-lead">{lead}</Text>}

        <div className="taste-portrait-facts">
          <div>
            <Text size="sm" fw={600} className="taste-portrait-label">
              <Trans>Most of what you read</Trans>
            </Text>
            <DiscoverTasteStrip />
          </div>
          {signals && signals.avoids.length > 0 && <AvoidRow avoids={signals.avoids} stacked />}
        </div>

        {summary && (
          <div className="taste-portrait-figures">
            <span>
              <b className="tnum">{formatNumber(summary.readSources)}</b> <Trans>read</Trans>
            </span>
            <span>
              <b className="tnum">{formatNumber(summary.ratedSources)}</b> <Trans>rated</Trans>
            </span>
            <Tooltip label={t`${pushingDown} pushing similar titles down`} withArrow>
              <span tabIndex={0}>
                <b className="tnum taste-portrait-thumb" data-tone="up">
                  <IconThumbUp size="0.8em" aria-hidden />
                  {formatNumber(liked)}
                </b>{' '}
                <b className="tnum taste-portrait-thumb" data-tone="down">
                  <IconThumbDown size="0.8em" aria-hidden />
                  {formatNumber(disliked)}
                </b>{' '}
                <Trans>thumbs</Trans>
              </span>
            </Tooltip>
            <span>
              <b className="tnum">{formatNumber(suppressed)}</b> <Trans>hidden or dismissed</Trans>
            </span>
          </div>
        )}

        <Group gap="sm" wrap="wrap">
          <Button leftSection={<IconFilter size={16} />} disabled={!canRecommend} onClick={onRecommend}>
            <Trans>Recommend from this</Trans>
          </Button>
          {signals && (
            <Button
              variant="default"
              leftSection={<IconAdjustmentsHorizontal size={16} />}
              onClick={() => manage('titles')}
            >
              <Trans>Manage signals</Trans>
            </Button>
          )}
        </Group>
      </div>

      {covers.length >= 4 && (
        <div className="taste-portrait-collage">
          {covers.map((m) => (
            <Link key={m.seriesId} to={`/series/${m.seriesId}`} className="taste-cover" aria-label={m.title}>
              <img src={m.coverUrl ?? undefined} alt="" loading="lazy" decoding="async" />
            </Link>
          ))}
        </div>
      )}
      {modal}
    </Panel>
  )
}
