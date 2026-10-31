import { useMemo, useState } from 'react'
import { Button, Group, Skeleton, Stack } from '@mantine/core'
import { IconArrowLeft, IconArrowRight } from '@tabler/icons-react'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import { plural } from '@lingui/core/macro'
import { Trans, useLingui } from '@lingui/react/macro'
import { usePageConversion, usePageConversionRules, type PageConversionSettings } from '../api/pageConversion'
import { StepStrip, type Step } from '../components/StepStrip'
import { CompareStep } from '../components/pageconversion/CompareStep'
import { emptyCompareChoice, type CompareChoice } from '../components/pageconversion/compareChoice'
import { FormatStep } from '../components/pageconversion/FormatStep'
import { toRuleDraft, type RuleDraft } from '../components/pageconversion/ruleTemplates'
import { ReviewStep } from '../components/pageconversion/ReviewStep'
import { ScopeStep } from '../components/pageconversion/ScopeStep'
import { StartStep } from '../components/pageconversion/StartStep'
import { FORMAT_LABELS, STOP_LABELS } from '../components/pageconversion/labels'
import { PageHeader } from '../components/ui/PageHeader'
import { Panel } from '../components/ui/Panel'
import { SurfaceFrame } from '../components/ui/SurfaceFrame'

const SETTINGS_URL = '/settings?tab=library&s=page-conversion'

type StepId = 'format' | 'compare' | 'scope' | 'review' | 'start'

const STEP_IDS: readonly StepId[] = ['format', 'compare', 'scope', 'review', 'start']

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
  const { data: savedRules, isError: rulesFailed } = usePageConversionRules()
  const [ruleEdits, setRuleEdits] = useState<RuleDraft[] | null>(null)
  const loadedRules = useMemo(() => (savedRules?.rules ?? []).map(toRuleDraft), [savedRules])
  const rulesReady = savedRules !== undefined || rulesFailed
  const rules = ruleEdits ?? loadedRules

  const requested = Number(params.get('step'))
  const index = Number.isInteger(requested) ? Math.min(Math.max(requested, 1), STEP_IDS.length) - 1 : 0
  const current = STEP_IDS[index]
  const goTo = (next: number) => setParams({ step: String(next + 1) })

  const draft: PageConversionSettings | null = status && rulesReady
    ? { ...status.settings, format: status.settings.format ?? 'webp', ...edits }
    : null

  const hints: Record<StepId, string> = {
    format: draft?.format ? t(FORMAT_LABELS[draft.format]) : t`Off`,
    compare: draft?.format ? t(STOP_LABELS[draft.stop]) : t`Nothing to compare`,
    scope: !draft?.format
      ? t`Nothing to choose`
      : rules.length === 0
        ? t`No rules`
        : plural(rules.length, { one: '# rule', other: '# rules' }),
    review: draft?.format ? t`Sizes and time` : t`Nothing to estimate`,
    start: t`Save and begin`,
  }
  const labels: Record<StepId, string> = {
    format: t`Format`,
    compare: t`Compare`,
    scope: t`What to convert`,
    review: t`Review`,
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
            {current === 'scope' && <ScopeStep value={draft} rules={rules} onRulesChange={setRuleEdits} rulesFailed={rulesFailed} />}
            {current === 'review' && <ReviewStep value={draft} rules={rules} />}
            {current === 'start' && (
              <StartStep
                value={draft}
                rules={rules}
                savedRules={savedRules?.rules ?? []}
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
