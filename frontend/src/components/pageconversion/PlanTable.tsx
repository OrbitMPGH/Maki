import { useState } from 'react'
import { ActionIcon, Text, UnstyledButton } from '@mantine/core'
import { IconArrowDown, IconArrowUp, IconChevronDown, IconChevronUp, IconPhotoSearch, IconX } from '../../lib/icons'
import { Trans, useLingui } from '@lingui/react/macro'
import {
  type EstimateBucket,
  type PageConversionFormat,
  type PageConversionScopeRequest,
  type PageConversionSettings,
  type PageTarget,
  type RowSamplesRequest,
  type SampleKind,
  type ScopeBucket,
} from '../../api/pageConversion'
import { formatBytes } from '../../format'
import { FORMAT_LABELS, STOP_LABELS, useTargetLabel } from './labels'
import { useRangeText, type PlanEstimate } from './PlanEstimate'
import { CountLine, RuleEditor, useConditionsSummary } from './RuleEditor'
import { toRuleDtos, type RuleDraft } from './ruleTemplates'
import { TargetSheet } from './TargetSheet'

/**
 * The colour target as set, even when it matches the black-and-white one, so picking a format in
 * the sheet always shows as picked. Saving drops one that matches.
 */
const ownColour = (value: { colourFormat?: PageConversionFormat | null; colourStop?: PageTarget['stop'] | null }) =>
  value.colourStop ? { format: value.colourFormat ?? 'webp', stop: value.colourStop } : null

/** -1 is the plan's default, everything no rule matches. */
type RowIndex = number

interface Targets {
  grey: PageTarget
  colour: PageTarget | null
}

interface OpenSheet {
  row: RowIndex
  kind: SampleKind
  request: RowSamplesRequest
}

/**
 * The plan as a table: the rules in order, then everything else. Each converting row has a target
 * for black-and-white pages and one for colour pages; a target opens a sheet that shows it on pages
 * of that kind from the files the row converts.
 */
export function PlanTable({
  value,
  onChange,
  rules,
  onRulesChange,
  scopeRequest,
  ruleBucket,
  defaultBucket,
  countsStale,
  estimate,
  onSheetChange,
}: {
  value: PageConversionSettings & { format: PageConversionFormat }
  onChange: (changes: Partial<PageConversionSettings>) => void
  rules: RuleDraft[]
  onRulesChange: (rules: RuleDraft[]) => void
  scopeRequest: PageConversionScopeRequest
  ruleBucket: (rule: RuleDraft) => ScopeBucket | undefined
  defaultBucket: ScopeBucket | undefined
  countsStale: boolean
  estimate: PlanEstimate
  /** The sheet opened or closed, so the estimate can wait while targets are tuned. */
  onSheetChange: (open: boolean) => void
}) {
  const { t } = useLingui()
  const [sheet, setSheet] = useState<OpenSheet | null>(null)
  const [expanded, setExpanded] = useState<string | null>(null)

  const defaults: Targets = { grey: { format: value.format, stop: value.stop }, colour: ownColour(value) }
  const ruleTargets = (rule: RuleDraft): Targets => ({
    grey: { format: rule.format ?? value.format, stop: rule.stop ?? 'high' },
    colour: ownColour(rule),
  })

  const changeRule = (next: RuleDraft) => onRulesChange(rules.map((rule) => (rule.key === next.key ? next : rule)))
  const move = (index: number, delta: -1 | 1) => {
    const next = [...rules]
    ;[next[index], next[index + delta]] = [next[index + delta], next[index]]
    onRulesChange(next)
  }

  const setTarget = (row: RowIndex, kind: SampleKind, target: PageTarget | null) => {
    if (row < 0) {
      if (kind === 'greyscale' && target) onChange({ format: target.format, stop: target.stop })
      else onChange({ colourFormat: target?.format ?? null, colourStop: target?.stop ?? null })
      return
    }
    const rule = rules[row]
    if (kind === 'greyscale' && target) changeRule({ ...rule, format: target.format, stop: target.stop })
    else changeRule({ ...rule, colourFormat: target?.format ?? null, colourStop: target?.stop ?? null })
  }

  const open = (row: RowIndex, kind: SampleKind) => {
    // The live list, not the debounced one the counts use, so the row index names the same rule.
    setSheet({ row, kind, request: { plan: { ...scopeRequest, rules: toRuleDtos(rules) }, row, kind } })
    onSheetChange(true)
  }
  const close = () => {
    setSheet(null)
    onSheetChange(false)
  }

  // Buckets line up with rows by position, which only holds for an estimate of the plan on screen.
  const result = estimate.result
  const lined = !!result && !estimate.stale && result.rules.length === rules.length
  const estimateFor = (row: RowIndex): EstimateBucket | undefined =>
    !lined ? undefined : row < 0 ? result.default : result.rules[row]

  const rowName = (row: RowIndex) => {
    if (row < 0) return t`Everything else`
    const number = row + 1
    return rules[row]?.name.trim() || t`Rule ${number}`
  }
  const sheetTargets = sheet ? (sheet.row < 0 ? defaults : rules[sheet.row] ? ruleTargets(rules[sheet.row]) : null) : null
  const sheetKind = sheet?.kind === 'colour' ? t`Colour pages` : t`Black-and-white pages`
  const sheetRow = sheet ? rowName(sheet.row) : ''

  return (
    <div className="pc-plan" role="table" aria-label={t`Conversion plan`}>
      <div className="pc-plan-head" role="row">
        <span role="columnheader">
          <Trans>Files</Trans>
        </span>
        <span role="columnheader">
          <Trans>Black-and-white pages</Trans>
        </span>
        <span role="columnheader">
          <Trans>Colour pages</Trans>
        </span>
        <span role="columnheader">
          <Trans>Size</Trans>
        </span>
        <span aria-hidden />
      </div>

      {rules.map((rule, index) => (
        <RuleRow
          key={rule.key}
          index={index}
          total={rules.length}
          rule={rule}
          name={rowName(index)}
          targets={ruleTargets(rule)}
          defaultFormat={value.format}
          bucket={ruleBucket(rule)}
          countsStale={countsStale}
          estimate={estimateFor(index)}
          estimateStale={estimate.stale}
          expanded={expanded === rule.key}
          onToggle={() => setExpanded((key) => (key === rule.key ? null : rule.key))}
          onChange={changeRule}
          onMove={(delta) => move(index, delta)}
          onRemove={() => onRulesChange(rules.filter((r) => r.key !== rule.key))}
          onOpen={(kind) => open(index, kind)}
        />
      ))}

      <div className="pc-plan-row" role="row" data-default>
        <div className="pc-plan-name" role="cell">
          <Text size="sm" fw={600}>
            {rowName(-1)}
          </Text>
          <Text size="xs" c="var(--ink-3)">
            {rules.length === 0 ? <Trans>Every file</Trans> : <Trans>Files no rule above matches</Trans>}
          </Text>
        </div>
        <TargetCells
          rowLabel={rowName(-1)}
          targets={defaults}
          onOpen={(kind) => open(-1, kind)}
        />
        <SizeCell bucket={defaultBucket} countsStale={countsStale} estimate={estimateFor(-1)} estimateStale={estimate.stale} />
        <span role="cell" />
      </div>

      {sheet && sheetTargets && (
        <TargetSheet
          opened
          onClose={close}
          title={t`${sheetKind}, ${sheetRow}`}
          kind={sheet.kind}
          target={sheet.kind === 'colour' ? sheetTargets.colour : sheetTargets.grey}
          fallback={sheetTargets.grey}
          pngLossless={value.pngLossless}
          samplesRequest={sheet.request}
          onChange={(target) => setTarget(sheet.row, sheet.kind, target)}
        />
      )}
    </div>
  )
}

function RuleRow({
  index,
  total,
  rule,
  name,
  targets,
  defaultFormat,
  bucket,
  countsStale,
  estimate,
  estimateStale,
  expanded,
  onToggle,
  onChange,
  onMove,
  onRemove,
  onOpen,
}: {
  index: number
  total: number
  rule: RuleDraft
  name: string
  targets: Targets
  defaultFormat: PageConversionFormat
  bucket: ScopeBucket | undefined
  countsStale: boolean
  estimate: EstimateBucket | undefined
  estimateStale: boolean
  expanded: boolean
  onToggle: () => void
  onChange: (rule: RuleDraft) => void
  onMove: (delta: -1 | 1) => void
  onRemove: () => void
  onOpen: (kind: SampleKind) => void
}) {
  const { t } = useLingui()
  const summarise = useConditionsSummary()
  const number = index + 1
  const summary = summarise(rule.conditions)

  return (
    <div className="pc-plan-row" role="row" data-expanded={expanded || undefined}>
      <div className="pc-plan-name" role="cell">
        <UnstyledButton className="pc-plan-name-button" onClick={onToggle} aria-expanded={expanded}>
          <Text size="sm" fw={600}>
            {number}. {name}
          </Text>
          <Text size="xs" c="var(--ink-3)" lineClamp={2}>
            {summary ?? <Trans>Every file that reaches it</Trans>}
          </Text>
        </UnstyledButton>
      </div>
      {rule.action === 'skip' ? (
        <div className="pc-plan-skip" role="cell">
          <Text size="sm" c="var(--ink-3)">
            <Trans>Skipped, left as they are</Trans>
          </Text>
        </div>
      ) : (
        <TargetCells rowLabel={name} targets={targets} onOpen={onOpen} />
      )}
      <SizeCell
        bucket={bucket}
        countsStale={countsStale}
        estimate={rule.action === 'skip' ? undefined : estimate}
        estimateStale={estimateStale}
      />
      <div className="pc-plan-actions" role="cell">
        <ActionIcon
          variant="subtle"
          color="var(--neutral)"
          aria-label={expanded ? t`Close rule ${number}` : t`Edit rule ${number}`}
          onClick={onToggle}
        >
          {expanded ? <IconChevronUp size={16} /> : <IconChevronDown size={16} />}
        </ActionIcon>
        <ActionIcon
          variant="subtle"
          color="var(--neutral)"
          aria-label={t`Move rule ${number} up`}
          disabled={index === 0}
          onClick={() => onMove(-1)}
        >
          <IconArrowUp size={16} />
        </ActionIcon>
        <ActionIcon
          variant="subtle"
          color="var(--neutral)"
          aria-label={t`Move rule ${number} down`}
          disabled={index === total - 1}
          onClick={() => onMove(1)}
        >
          <IconArrowDown size={16} />
        </ActionIcon>
        <ActionIcon variant="subtle" color="var(--danger)" aria-label={t`Remove rule ${number}`} onClick={onRemove}>
          <IconX size={16} />
        </ActionIcon>
      </div>
      {expanded && (
        <div className="pc-plan-editor" role="cell">
          <RuleEditor index={index} rule={rule} defaultFormat={defaultFormat} onChange={onChange} />
        </div>
      )}
    </div>
  )
}

function TargetCells({
  rowLabel,
  targets,
  onOpen,
}: {
  rowLabel: string
  targets: Targets
  onOpen: (kind: SampleKind) => void
}) {
  const { t } = useLingui()
  return (
    <>
      <div className="pc-plan-target" role="cell" data-label={t`Black-and-white pages`}>
        <TargetChip
          target={targets.grey}
          ariaLabel={t`Black-and-white pages, ${rowLabel}`}
          onClick={() => onOpen('greyscale')}
        />
      </div>
      <div className="pc-plan-target" role="cell" data-label={t`Colour pages`}>
        <TargetChip target={targets.colour} ariaLabel={t`Colour pages, ${rowLabel}`} onClick={() => onOpen('colour')} />
      </div>
    </>
  )
}

/** A target as a button: "AVIF High", or "Same as black-and-white" for colour pages that follow it. */
function TargetChip({
  target,
  ariaLabel,
  onClick,
}: {
  target: PageTarget | null
  ariaLabel: string
  onClick: () => void
}) {
  const { t } = useLingui()
  const targetLabel = useTargetLabel()
  const shown = target ? targetLabel(target) : t`Same as black-and-white`
  return (
    <UnstyledButton
      className="pc-target-chip"
      data-same={!target || undefined}
      onClick={onClick}
      aria-label={t`${ariaLabel}: ${shown}`}
    >
      {target ? (
        <span className="pc-target-chip-text">
          {target.stop !== 'lossless' && <span className="pc-target-chip-format">{t(FORMAT_LABELS[target.format])}</span>}
          <span>{t(STOP_LABELS[target.stop])}</span>
        </span>
      ) : (
        <span className="pc-target-chip-text">
          <Trans>Same as black-and-white</Trans>
        </span>
      )}
      <IconPhotoSearch size={14} aria-hidden className="pc-target-chip-icon" />
    </UnstyledButton>
  )
}

function SizeCell({
  bucket,
  countsStale,
  estimate,
  estimateStale,
}: {
  bucket: ScopeBucket | undefined
  countsStale: boolean
  estimate: EstimateBucket | undefined
  estimateStale: boolean
}) {
  const range = useRangeText()
  const sampled = estimate && estimate.sampledFiles > 0 && estimate.files > 0
  const expected = sampled ? range(estimate.expectedLow, estimate.expectedHigh, formatBytes) : ''
  return (
    <div className="pc-plan-size" role="cell">
      <CountLine bucket={bucket} stale={countsStale} />
      {sampled && (
        <Text size="xs" c="var(--ink-2)" style={{ opacity: estimateStale ? 0.55 : 1 }}>
          <Trans>{expected} after</Trans>
        </Text>
      )}
    </div>
  )
}
