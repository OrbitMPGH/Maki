import { useMemo, useState } from 'react'
import { Button, Group, Menu, Modal, Skeleton, Stack, Switch, Text } from '@mantine/core'
import { useDebouncedValue } from '@mantine/hooks'
import { IconArrowLeft, IconChevronDown, IconPlus } from '../lib/icons'
import { Link, useNavigate } from 'react-router-dom'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import {
  MAX_PAGE_CONVERSION_RULES,
  usePageConversion,
  usePageConversionRules,
  usePageConversionScope,
  type PageConversionFormat,
  type PageConversionRule,
  type PageConversionScopeRequest,
  type PageConversionSettings,
  type PageConversionStatus,
  type ScopeBucket,
} from '../api/pageConversion'
import { formatBytes, formatReadingTime } from '../format'
import { PlanTable } from '../components/pageconversion/PlanTable'
import { PlanOptions } from '../components/pageconversion/PlanOptions'
import {
  EstimateDetails,
  Range,
  Sampling,
  estimateTotals,
  useRangeText,
  usePlanEstimate,
  type PlanEstimate,
} from '../components/pageconversion/PlanEstimate'
import { SaveActions, planChanged } from '../components/pageconversion/SaveActions'
import { RULE_TEMPLATES, newRuleDraft, toRuleDraft, toRuleDtos, type RuleDraft } from '../components/pageconversion/ruleTemplates'
import { PageHeader } from '../components/ui/PageHeader'
import { Panel } from '../components/ui/Panel'
import { SurfaceFrame } from '../components/ui/SurfaceFrame'
import { MODAL_SIZE } from '../components/ui/modalSizes'

const SETTINGS_URL = '/settings?tab=library&s=page-conversion'

const COUNT_DEBOUNCE_MS = 400

/**
 * Page conversion on one page: the rules and the default as a table of targets, each opening on
 * the admin's own pages of that kind, with live counts, an estimate, and the only buttons that write.
 * Choices live here until saved.
 */
export default function PageConversionWizardPage() {
  const navigate = useNavigate()
  const { data: status } = usePageConversion()
  const [edits, setEdits] = useState<Partial<PageConversionSettings>>({})
  const { data: savedRules, isError: rulesFailed } = usePageConversionRules()
  const [ruleEdits, setRuleEdits] = useState<RuleDraft[] | null>(null)
  const loadedRules = useMemo(() => (savedRules?.rules ?? []).map(toRuleDraft), [savedRules])
  const rulesReady = savedRules !== undefined || rulesFailed
  const rules = ruleEdits ?? loadedRules

  const draft: PageConversionSettings | null = status && rulesReady ? { ...status.settings, ...edits } : null

  return (
    <SurfaceFrame pageStyle="operational">
      <PageHeader
        compact
        title={<Trans>Page conversion</Trans>}
        description={
          <Trans>Choose how each kind of file and page is stored, and look at your own pages before anything is rewritten.</Trans>
        }
        actions={
          <Button component={Link} to={SETTINGS_URL} variant="default" leftSection={<IconArrowLeft size={16} />}>
            <Trans>Back to settings</Trans>
          </Button>
        }
      />
      {!draft || !status ? (
        <Panel p="md">
          <Stack gap="sm">
            <Skeleton height={28} width="40%" />
            <Skeleton height={220} />
          </Stack>
        </Panel>
      ) : (
        <Plan
          value={draft}
          onChange={(changes) => setEdits((previous) => ({ ...previous, ...changes }))}
          rules={rules}
          savedRules={savedRules?.rules ?? []}
          onRulesChange={setRuleEdits}
          rulesFailed={rulesFailed}
          status={status}
          onDone={() => navigate(SETTINGS_URL)}
        />
      )}
    </SurfaceFrame>
  )
}

function Plan({
  value,
  onChange,
  rules,
  savedRules,
  onRulesChange,
  rulesFailed,
  status,
  onDone,
}: {
  value: PageConversionSettings
  onChange: (changes: Partial<PageConversionSettings>) => void
  rules: RuleDraft[]
  savedRules: PageConversionRule[]
  onRulesChange: (rules: RuleDraft[]) => void
  rulesFailed: boolean
  status: PageConversionStatus
  onDone: () => void
}) {
  const { t } = useLingui()
  const [lastFormat, setLastFormat] = useState<PageConversionFormat>(value.format ?? 'webp')
  const [sheetOpen, setSheetOpen] = useState(false)
  const [detailsOpen, setDetailsOpen] = useState(false)
  const estimate = usePlanEstimate(value, rules, sheetOpen)

  const [settledRules] = useDebouncedValue(rules, COUNT_DEBOUNCE_MS)
  const { format, stop, pngLossless, splitHeight, colourFormat, colourStop } = value
  const scopeRequest: PageConversionScopeRequest | null = format
    ? { format, stop, pngLossless, splitHeight, colourFormat, colourStop, rules: toRuleDtos(settledRules) }
    : null
  const counts = usePageConversionScope(
    scopeRequest,
    settledRules.map((rule) => rule.key),
  )
  const scope = counts.data
  const countsStale = settledRules !== rules || counts.isFetching
  const ruleBucket = (rule: RuleDraft): ScopeBucket | undefined => {
    const at = scope?.ruleKeys.indexOf(rule.key) ?? -1
    return at >= 0 ? scope?.rules[at] : undefined
  }

  const atLimit = rules.length >= MAX_PAGE_CONVERSION_RULES
  const addRule = (rule: RuleDraft) => onRulesChange([...rules, rule])
  const changed = planChanged(value, status.settings, rules, savedRules)

  return (
    <Stack gap="md">
      <Panel p="md">
        <Stack gap="md">
          <Group justify="space-between" align="flex-start" wrap="nowrap" gap="md">
            <Stack gap={2}>
              <Text fw={600}>
                <Trans>Convert pages</Trans>
              </Text>
              <Text size="sm" c="var(--ink-3)">
                {format ? (
                  <Trans>
                    Rules are checked from the top, and the first one a file matches decides what happens to it. A
                    series set to Never or to a fixed quality in its menu ignores the rules.
                  </Trans>
                ) : (
                  <Trans>Pages stay as they are downloaded. Files already converted stay converted.</Trans>
                )}
              </Text>
            </Stack>
            <Switch
              size="md"
              aria-label={t`Convert pages`}
              checked={!!format}
              onChange={(event) => {
                if (event.currentTarget.checked) onChange({ format: lastFormat })
                else {
                  if (format) setLastFormat(format)
                  onChange({ format: null })
                }
              }}
            />
          </Group>

          {rulesFailed && (
            <Text size="sm" c="var(--warn)" role="alert">
              <Trans>Saved rules could not be loaded; saving will replace them.</Trans>
            </Text>
          )}

          {format && scopeRequest && (
            <>
              <PlanTable
                value={{ ...value, format }}
                onChange={onChange}
                rules={rules}
                onRulesChange={onRulesChange}
                scopeRequest={scopeRequest}
                ruleBucket={ruleBucket}
                defaultBucket={scope?.default}
                countsStale={countsStale}
                estimate={estimate}
                onSheetChange={setSheetOpen}
              />

              <Group gap="sm">
                <Button
                  variant="default"
                  size="xs"
                  leftSection={<IconPlus size={16} />}
                  disabled={atLimit}
                  onClick={() => addRule(newRuleDraft({ stop }, value))}
                >
                  <Trans>Add rule</Trans>
                </Button>
                <Menu position="bottom-start" withinPortal>
                  <Menu.Target>
                    <Button variant="subtle" size="xs" rightSection={<IconChevronDown size={14} />} disabled={atLimit}>
                      <Trans>Start from a template</Trans>
                    </Button>
                  </Menu.Target>
                  <Menu.Dropdown>
                    {RULE_TEMPLATES.map((template) => (
                      <Menu.Item
                        key={template.id}
                        onClick={() => addRule(newRuleDraft({ name: t(template.title), ...template.rule }, value))}
                      >
                        <Text size="sm">{t(template.title)}</Text>
                        <Text size="xs" c="var(--ink-3)">
                          {t(template.hint)}
                        </Text>
                      </Menu.Item>
                    ))}
                  </Menu.Dropdown>
                </Menu>
              </Group>

              <ScopeNotes scope={scope} />
              {counts.isError && (
                <Text size="sm" c="var(--danger-fg)">
                  <Trans>The counts could not be loaded. The rules still save as you wrote them.</Trans>
                </Text>
              )}
            </>
          )}
        </Stack>
      </Panel>

      {format && (
        <Panel p="md">
          <PlanOptions value={value} onChange={onChange} />
        </Panel>
      )}

      <div className="pc-plan-footer">
        <FooterSummary on={!!format} estimate={estimate} />
        <Group gap="sm" wrap="nowrap">
          {format && (estimate.result || estimate.broken || estimate.startFailed) && (
            <Button variant="subtle" onClick={() => setDetailsOpen(true)}>
              <Trans>Details</Trans>
            </Button>
          )}
          <SaveActions value={value} rules={rules} changed={changed} status={status} onDone={onDone} />
        </Group>
      </div>

      <Modal
        opened={detailsOpen}
        onClose={() => setDetailsOpen(false)}
        title={<Trans>What the pass would do</Trans>}
        size={MODAL_SIZE.wide}
        centered
      >
        <EstimateDetails plan={estimate} />
      </Modal>
    </Stack>
  )
}

/** "About 76 GB to 50 GB to 56 GB", how long, or how far the estimate has got. */
function FooterSummary({ on, estimate }: { on: boolean; estimate: PlanEstimate }) {
  const range = useRangeText()
  if (!on) {
    return (
      <Text size="sm" c="var(--ink-3)">
        <Trans>Conversion is off. Files converted earlier stay as they are.</Trans>
      </Text>
    )
  }

  const totals = estimate.result ? estimateTotals(estimate.result) : null
  if (!totals) {
    if (estimate.running) {
      return (
        <div className="pc-plan-footer-sampling">
          <Sampling estimate={estimate.estimate} starting={estimate.starting} />
        </div>
      )
    }
    return (
      <Text size="sm" c="var(--ink-3)">
        {estimate.broken || estimate.startFailed ? (
          <Trans>The estimate did not finish. Open the details to try again.</Trans>
        ) : (
          <Trans>Sizes appear here once Maki has sampled your files.</Trans>
        )}
      </Text>
    )
  }

  const now = formatBytes(totals.bytes)
  const expected = totals.sampled ? range(totals.expectedLow, totals.expectedHigh, formatBytes) : null
  return (
    <Stack gap={2} style={{ opacity: estimate.stale ? 0.6 : 1 }} aria-live="polite">
      <Text fw={600}>
        {totals.files === 0 ? (
          <Trans>Nothing to convert</Trans>
        ) : expected ? (
          <Plural
            value={totals.files}
            one={`# file, ${now} now, ${expected} after`}
            other={`# files, ${now} now, ${expected} after`}
          />
        ) : (
          <Plural value={totals.files} one={`# file, ${now}`} other={`# files, ${now}`} />
        )}
      </Text>
      <Text size="xs" c="var(--ink-3)">
        {estimate.running ? (
          <Trans>Updating for your latest changes</Trans>
        ) : estimate.stale ? (
          <Trans>From before your latest changes</Trans>
        ) : totals.secondsHigh > 0 ? (
          <Trans>
            Takes about <Range low={totals.secondsLow} high={totals.secondsHigh} format={formatReadingTime} /> on this
            server
          </Trans>
        ) : null}
      </Text>
    </Stack>
  )
}

function ScopeNotes({ scope }: { scope: ReturnType<typeof usePageConversionScope>['data'] }) {
  if (!scope) return null
  const overridesSize = formatBytes(scope.overrides.bytes)
  const compactSize = formatBytes(scope.alreadyCompact.bytes)
  const earlierSize = formatBytes(scope.convertedEarlier.bytes)
  return (
    <Stack gap={4} aria-live="polite">
      {scope.overrides.files > 0 && (
        <Text size="sm" c="var(--ink-3)">
          <Plural
            value={scope.overrides.files}
            one={`# file is in a series with its own setting, which wins over the rules (${overridesSize}).`}
            other={`# files are in series with their own setting, which wins over the rules (${overridesSize}).`}
          />
        </Text>
      )}
      {scope.alreadyCompact.files > 0 && (
        <Text size="sm" c="var(--ink-3)">
          <Plural
            value={scope.alreadyCompact.files}
            one={`# file is already WebP, AVIF or JPEG XL and is left as it is (${compactSize}).`}
            other={`# files are already WebP, AVIF or JPEG XL and are left as they are (${compactSize}).`}
          />
        </Text>
      )}
      {scope.convertedEarlier.files > 0 && (
        <Text size="sm" c="var(--ink-3)">
          <Plural
            value={scope.convertedEarlier.files}
            one={`# file was converted earlier and stays as it is (${earlierSize}).`}
            other={`# files were converted earlier and stay as they are (${earlierSize}).`}
          />
        </Text>
      )}
    </Stack>
  )
}
