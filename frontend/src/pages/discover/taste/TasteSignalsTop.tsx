import { useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { Button, Text, UnstyledButton, VisuallyHidden } from '@mantine/core'
import { useElementSize } from '@mantine/hooks'
import { Trans, useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'
import { IconAdjustmentsHorizontal, IconClockPause, IconCompass, IconEyeOff, IconPlant } from '../../../lib/icons'
import type { FeedbackLabData, SteeringSeed } from '../../../api/recommendationFeedback'
import { useMutateSignalOverride, useSignalOverrides } from '../../../api/recommendationFeedback'
import { randomUUID } from '../../../lib/uuid'
import { errorText } from '../../../api/errorText'
import { Panel } from '../../../components/ui/Panel'
import { PanelTitle } from '../../../components/ui/PanelTitle'
import { formatFixedDecimal, formatNumber } from '../../../format'

/** Cover width at the heaviest weight; the rest scale down from it. */
const MAX_COVER = 112

/** Gap between covers, and the room the 1.0x mark takes. */
const SEED_GAP = 12
const MARK_WIDTH = 44

/** Damped series kept past the mark whatever the width, so the low end always shows. */
const LIGHT_SHOWN = 3

function weightText(weight: number) {
  return `${formatFixedDecimal(weight, 2)}x`
}

function useSourceText() {
  const { t } = useLingui()
  return (seed: SteeringSeed) => {
    const value = seed.value === null ? '' : formatNumber(seed.value)
    switch (seed.source) {
      case 'rating':
        return t`Rated ${value}`
      case 'sofar':
        return t`Rated ${value} so far`
      case 'liked':
        return t`Liked`
      case 'finished':
        return plural(seed.value ?? 0, { one: 'Finished # chapter', other: 'Finished # chapters' })
      case 'read':
        return plural(seed.value ?? 0, { one: 'Read # chapter', other: 'Read # chapters' })
      default:
        return t`Added by you`
    }
  }
}

/**
 * The shelf series that weigh most in the recommendations and the ones below neutral, covers sized
 * by weight across a 1.0x mark. Picking one fills the footer with why it weighs what it does and
 * the two things to do about it.
 */
function Steering({ seeds }: { seeds: SteeringSeed[] }) {
  const { t } = useLingui()
  const navigate = useNavigate()
  const sourceText = useSourceText()
  const { data: overrides } = useSignalOverrides()
  const exclude = useMutateSignalOverride()
  const [selectedId, setSelectedId] = useState<number | null>(null)
  const [error, setError] = useState('')
  const { ref: rowRef, width: rowWidth } = useElementSize()
  const top = Math.max(...seeds.map((s) => s.weight))
  const width = (weight: number) => Math.round(MAX_COVER * Math.max(0.45, Math.min(1, weight / top)))

  // Fill the row: the damped few first (they are the point of the mark), then as many of the
  // heaviest as the rest of the width holds.
  const light = seeds.filter((s) => s.weight < 1).slice(0, LIGHT_SHOWN)
  let budget = rowWidth > 0 ? rowWidth : 6 * (MAX_COVER + SEED_GAP)
  if (light.length > 0) budget -= MARK_WIDTH + light.reduce((sum, s) => sum + width(s.weight) + SEED_GAP, 0)
  const heavy: SteeringSeed[] = []
  for (const seed of seeds.filter((s) => s.weight >= 1)) {
    const need = width(seed.weight) + SEED_GAP
    if (heavy.length > 0 && need > budget) break
    heavy.push(seed)
    budget -= need
  }
  const visible = [...heavy, ...light]
  const selected = visible.find((s) => s.mangaBakaId === selectedId) ?? visible[0]
  if (!selected) return null
  const tile = (seed: SteeringSeed) => (
    <UnstyledButton
      key={seed.mangaBakaId}
      className="taste-steer-seed"
      data-light={seed.weight < 1 || undefined}
      aria-pressed={seed.mangaBakaId === selected.mangaBakaId}
      onClick={() => setSelectedId(seed.mangaBakaId)}
      style={{ width: width(seed.weight) }}
    >
      <span className="taste-cover">
        {seed.coverUrl && <img src={seed.coverUrl} alt="" loading="lazy" decoding="async" />}
      </span>
      <span className="taste-steer-weight tnum">{weightText(seed.weight)}</span>
      <span className="taste-steer-source">{sourceText(seed)}</span>
      <VisuallyHidden>{seed.title}</VisuallyHidden>
    </UnstyledButton>
  )

  const excludeSelected = async () => {
    setError('')
    try {
      await exclude.mutateAsync({
        id: selected.mangaBakaId,
        ignoreAsSeed: true,
        clientMutationId: randomUUID(),
        expectedRevision: overrides?.find((o) => o.mangaBakaId === selected.mangaBakaId)?.revision ?? 0,
      })
    } catch (cause) {
      setError(errorText(cause))
    }
  }

  const weight = weightText(selected.weight)
  const why = sourceText(selected)
  return (
    <Panel className="taste-steer">
      <div className="taste-steer-head">
        <PanelTitle icon={IconAdjustmentsHorizontal}>
          <Trans>What steers your picks most</Trans>
        </PanelTitle>
        <Text size="xs" c="var(--ink-4)">
          <Trans>Bigger covers pull harder.</Trans>
        </Text>
      </div>
      <div ref={rowRef} className="taste-steer-row">
        {heavy.map(tile)}
        {light.length > 0 && (
          <span className="taste-steer-mark tnum" aria-hidden>
            1.0x
          </span>
        )}
        {light.map(tile)}
      </div>
      <div className="taste-steer-foot">
        <Text size="sm">
          <b>{selected.title}</b>{' '}
          <Text span size="sm" c="var(--ink-3)">
            <Trans>
              counts {weight}: {why}
            </Trans>
          </Text>
        </Text>
        <div className="taste-steer-actions">
          <Button
            variant="default"
            size="compact-sm"
            leftSection={<IconCompass size={14} />}
            onClick={() =>
              navigate('/discover/recommended', {
                state: {
                  recommendationFilters: {},
                  seeds: [{ id: selected.mangaBakaId, title: selected.title }],
                  source: 'taste-profile',
                },
              })
            }
          >
            <Trans>Recommend from this</Trans>
          </Button>
          <Button
            variant="default"
            size="compact-sm"
            leftSection={<IconEyeOff size={14} />}
            loading={exclude.isPending}
            onClick={() => void excludeSelected()}
            aria-label={t`Exclude ${selected.title} from taste`}
          >
            <Trans>Exclude from taste</Trans>
          </Button>
        </div>
      </div>
      {error && (
        <Text size="xs" c="var(--danger)" mt="xs" role="alert">
          {error}
        </Text>
      )}
    </Panel>
  )
}

/**
 * What became of the series added from a recommendation: added, started, finished as narrowing
 * bars (started is blue, happening; finished green, done), then the ones still waiting to start.
 */
function TookUp({ adds }: { adds: NonNullable<FeedbackLabData['recommendationAdds']> }) {
  const { t } = useLingui()
  const steps = [
    { key: 'added', label: t`added`, value: adds.added },
    { key: 'started', label: t`started`, value: adds.started },
    { key: 'finished', label: t`finished`, value: adds.finished },
  ]
  return (
    <Panel className="taste-tookup">
      <PanelTitle icon={IconPlant}>
        <Trans>Recommendations you took up</Trans>
      </PanelTitle>
      <div className="taste-tookup-steps">
        {steps.map((step) => (
          <div key={step.key} className="taste-tookup-step" data-step={step.key}>
            <span className="taste-tookup-bar" style={{ width: `${Math.max(4, (step.value / adds.added) * 100)}%` }} />
            <span className="taste-tookup-value tnum">{formatNumber(step.value)}</span>
            <span className="taste-tookup-label">{step.label}</span>
          </div>
        ))}
      </div>
      {adds.waiting.length > 0 && (
        <div className="taste-tookup-waiting">
          <Text size="sm" fw={600} className="taste-tookup-waiting-head">
            <IconClockPause size={15} aria-hidden />
            <Trans>Waiting to be started</Trans>
          </Text>
          {adds.waiting.map((series) => (
            <Link key={series.seriesId} to={`/series/${series.seriesId}`} className="taste-tookup-row">
              <span className="taste-cover" aria-hidden>
                {series.coverUrl && <img src={series.coverUrl} alt="" loading="lazy" decoding="async" />}
              </span>
              <span className="taste-tookup-title">{series.title}</span>
              <span className="taste-tookup-open">
                <Trans>Open</Trans>
              </span>
            </Link>
          ))}
        </div>
      )}
    </Panel>
  )
}

/** The top row of the signals block on the Taste tab. Each card hides on thin data. */
export function TasteSignalsTop({ lab }: { lab: FeedbackLabData }) {
  const seeds = lab.steering ?? []
  const adds = lab.recommendationAdds ?? null
  const showSteering = seeds.filter((s) => s.weight >= 1).length >= 3
  if (!showSteering && !adds) return null
  return (
    <div className="taste-sigtop" data-single={!showSteering || !adds || undefined}>
      {showSteering && <Steering seeds={seeds} />}
      {adds && <TookUp adds={adds} />}
    </div>
  )
}
