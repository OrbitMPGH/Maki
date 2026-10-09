import { Alert, Badge, Button, Group, Loader, Progress, Text, Tooltip } from '@mantine/core'
import { IconCheck, IconRuler } from '@tabler/icons-react'
import { Trans, useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'
import type { ScoutSourceProgress, SourceOrderDto } from '../api/upgrades'

/** Why a source could not be sampled, in words. */
function useProblemText() {
  const { t } = useLingui()
  return (progress: ScoutSourceProgress, needsFlareSolverr: boolean) => {
    if (progress.state === 'skipped') return t`This source lists none of the sampled chapters.`
    if (progress.problem === 'cooldown') return t`This source is rate-limiting Maki right now. Try again later.`
    if (needsFlareSolverr) return t`This source needs FlareSolverr. Check it is set up in Settings.`
    return t`This source did not serve the sampled pages. It may be down or have changed.`
  }
}

/** The Quality cell of one source while it is being measured, or when its measurement failed. */
export function ScoutCell({ progress, needsFlareSolverr }: { progress: ScoutSourceProgress; needsFlareSolverr: boolean }) {
  const problemText = useProblemText()
  const { done, planned } = progress
  if (progress.state === 'waiting') {
    return (
      <Text size="xs" c="var(--ink-3)">
        <Trans>Waiting</Trans>
      </Text>
    )
  }
  if (progress.state === 'measuring') {
    return (
      <Group gap={6} wrap="nowrap">
        <Loader size={12} />
        <Text size="xs" c="var(--ink-2)" style={{ whiteSpace: 'nowrap' }}>
          <Trans>
            Measuring {done}/{planned}
          </Trans>
        </Text>
      </Group>
    )
  }
  return (
    <Tooltip label={problemText(progress, needsFlareSolverr)} withArrow multiline w={260}>
      <Badge size="sm" variant="light" color={progress.state === 'skipped' ? 'var(--neutral)' : 'var(--danger)'}>
        {progress.state === 'skipped' ? <Trans>Not sampled</Trans> : <Trans>Couldn't sample</Trans>}
      </Badge>
    </Tooltip>
  )
}

/** Shown above the source table while a measurement runs. */
export function MeasureProgress({ order }: { order: SourceOrderDto }) {
  const scout = order.scout!
  const sources = scout.sources.filter((s) => s.state !== 'skipped').length
  const chapters = scout.chapters.join(', ')
  const done = scout.done
  const probes = scout.probes
  return (
    <Alert color="var(--info)" variant="light" icon={<Loader size={16} />} mb="sm">
      <Text size="sm" fw={500}>
        {plural(sources, {
          one: `Measuring # source on chapters ${chapters}`,
          other: `Measuring # sources on chapters ${chapters}`,
        })}
      </Text>
      <Progress value={probes > 0 ? (done / probes) * 100 : 0} size="sm" mt={6} mb={4} animated />
      <Text size="xs" c="var(--ink-3)">
        <Trans>
          {done} of {probes} chapter samples. The order below updates once it finishes.
        </Trans>
      </Text>
    </Alert>
  )
}

/**
 * Shown once a measurement finishes, until dismissed: what was measured, what failed, and, when the
 * measured order would start with a different source than downloads use now, the two ways to adopt it.
 */
export function MeasureResult({
  order,
  label,
  needsFlareSolverr,
  busy,
  onUseQuality,
  onReorder,
  onDismiss,
}: {
  order: SourceOrderDto
  label: (mappingId: number) => string
  needsFlareSolverr: (mappingId: number) => boolean
  busy: boolean
  onUseQuality: () => void
  onReorder: () => void
  onDismiss: () => void
}) {
  const problemText = useProblemText()
  const scout = order.scout!
  const tried = scout.sources.filter((s) => s.state !== 'skipped')
  const measured = tried.filter((s) => s.state === 'done').length
  const total = tried.length
  const failed = scout.sources.filter((s) => s.state === 'failed' || s.state === 'skipped')
  const best = order.qualityOrder[0]
  const current = order.order.find((id) => order.qualityOrder.includes(id))
  const bestName = best != null ? label(best) : ''
  const currentName = current != null ? label(current) : ''
  const differs = order.mode === 'manual' && best != null && current != null && best !== current

  return (
    <Alert
      color={measured > 0 ? 'var(--ok)' : 'var(--warn)'}
      variant="light"
      icon={measured > 0 ? <IconCheck size={16} /> : <IconRuler size={16} />}
      withCloseButton
      onClose={onDismiss}
      mb="sm"
    >
      <Text size="sm" fw={500}>
        {plural(total, { one: `Measured ${measured} of # source`, other: `Measured ${measured} of # sources` })}
      </Text>
      {measured === 0 ? (
        <Text size="sm" mt={4}>
          <Trans>Nothing could be measured, so the download order is unchanged.</Trans>
        </Text>
      ) : differs ? (
        <Text size="sm" mt={4}>
          <Trans>
            By measured quality, downloads would try {bestName} first. With manual priority, {currentName} goes
            first now.
          </Trans>
        </Text>
      ) : order.mode === 'quality' ? (
        <Text size="sm" mt={4}>
          <Trans>Downloads now try {bestName} first.</Trans>
        </Text>
      ) : (
        <Text size="sm" mt={4}>
          <Trans>{bestName} is already tried first, so nothing needs to change.</Trans>
        </Text>
      )}
      {failed.map((s) => (
        <Text key={s.mappingId} size="xs" c="var(--ink-3)" mt={4}>
          {label(s.mappingId)}: {problemText(s, needsFlareSolverr(s.mappingId))}
        </Text>
      ))}
      {measured > 0 && differs && (
        <Group gap="xs" mt="sm">
          <Button size="xs" onClick={onUseQuality} loading={busy}>
            <Trans>Use best quality first</Trans>
          </Button>
          <Tooltip
            label={<Trans>Keep manual priority, but renumber it to follow the measured order.</Trans>}
            withArrow
            multiline
            w={240}
          >
            <Button size="xs" variant="default" onClick={onReorder} loading={busy}>
              <Trans>Reorder priority to match</Trans>
            </Button>
          </Tooltip>
          <Button size="xs" variant="subtle" color="gray" onClick={onDismiss}>
            <Trans>Keep as is</Trans>
          </Button>
        </Group>
      )}
    </Alert>
  )
}
