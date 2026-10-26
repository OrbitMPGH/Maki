import { useState } from 'react'
import { Button, Group, Skeleton, Stack } from '@mantine/core'
import { IconArrowLeft, IconArrowRight } from '@tabler/icons-react'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import { Trans, useLingui } from '@lingui/react/macro'
import { usePageConversion, type PageConversionSettings } from '../api/pageConversion'
import { StepStrip, type Step } from '../components/StepStrip'
import { CompareStep } from '../components/pageconversion/CompareStep'
import { emptyCompareChoice, type CompareChoice } from '../components/pageconversion/compareChoice'
import { FormatStep } from '../components/pageconversion/FormatStep'
import { StartStep } from '../components/pageconversion/StartStep'
import { FORMAT_LABELS, STOP_LABELS } from '../components/pageconversion/labels'
import { PageHeader } from '../components/ui/PageHeader'
import { Panel } from '../components/ui/Panel'
import { SurfaceFrame } from '../components/ui/SurfaceFrame'

const SETTINGS_URL = '/settings?tab=library&s=page-conversion'

type StepId = 'format' | 'compare' | 'start'

/** The wizard's steps in order. A later phase adds "What to convert" and "Review" here and nowhere else. */
const STEP_IDS: readonly StepId[] = ['format', 'compare', 'start']

/**
 * Sets up page conversion: the format, then one of the admin's own pages before and after at each
 * quality stop, then the only step that writes anything. Choices live in this component until Start.
 */
export default function PageConversionWizardPage() {
  const { t } = useLingui()
  const [params, setParams] = useSearchParams()
  const navigate = useNavigate()
  const { data: status } = usePageConversion()
  const [edits, setEdits] = useState<Partial<PageConversionSettings>>({})
  const [compare, setCompare] = useState<CompareChoice>(emptyCompareChoice)

  const requested = Number(params.get('step'))
  const index = Number.isInteger(requested) ? Math.min(Math.max(requested, 1), STEP_IDS.length) - 1 : 0
  const current = STEP_IDS[index]
  const goTo = (next: number) => setParams({ step: String(next + 1) })

  const draft: PageConversionSettings | null = status
    ? { ...status.settings, format: status.settings.format ?? 'webp', ...edits }
    : null

  const hints: Record<StepId, string> = {
    format: draft?.format ? t(FORMAT_LABELS[draft.format]) : t`Off`,
    compare: draft?.format ? t(STOP_LABELS[draft.stop]) : t`Nothing to compare`,
    start: t`Save and begin`,
  }
  const labels: Record<StepId, string> = {
    format: t`Format`,
    compare: t`Compare`,
    start: t`Start`,
  }
  const steps: Step[] = STEP_IDS.map((id, i) => ({
    label: labels[id],
    hint: hints[id],
    state: i < index ? 'done' : i === index ? 'current' : 'todo',
  }))

  const change = (changes: Partial<PageConversionSettings>) => setEdits((previous) => ({ ...previous, ...changes }))

  return (
    <SurfaceFrame pageStyle="operational">
      <PageHeader
        compact
        title={<Trans>Set up page conversion</Trans>}
        description={<Trans>Compare your own pages at each quality before anything is rewritten.</Trans>}
        actions={
          <Button component={Link} to={SETTINGS_URL} variant="default" size="xs" leftSection={<IconArrowLeft size={14} />}>
            <Trans>Back to settings</Trans>
          </Button>
        }
      />
      <Panel p="sm">
        <StepStrip steps={steps} label={t`Page conversion steps`} />
        {!draft || !status ? (
          <Stack gap="sm">
            <Skeleton height={28} width="40%" />
            <Skeleton height={220} />
          </Stack>
        ) : (
          <Stack gap="lg" p="xs">
            {current === 'format' && <FormatStep value={draft} onChange={change} />}
            {current === 'compare' && (
              <CompareStep value={draft} onChange={change} choice={compare} onChoiceChange={setCompare} />
            )}
            {current === 'start' && (
              <StartStep
                value={draft}
                status={status}
                onDone={() => navigate(SETTINGS_URL)}
              />
            )}
            <Group justify="space-between">
              {index > 0 ? (
                <Button variant="default" leftSection={<IconArrowLeft size={14} />} onClick={() => goTo(index - 1)}>
                  <Trans>Back</Trans>
                </Button>
              ) : (
                <span />
              )}
              {index < STEP_IDS.length - 1 && (
                <Button rightSection={<IconArrowRight size={14} />} onClick={() => goTo(index + 1)}>
                  <Trans>Next</Trans>
                </Button>
              )}
            </Group>
          </Stack>
        )}
      </Panel>
    </SurfaceFrame>
  )
}
