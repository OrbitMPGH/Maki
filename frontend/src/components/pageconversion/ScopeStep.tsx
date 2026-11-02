import { Button, Group, Stack, Text } from '@mantine/core'
import { useDebouncedValue } from '@mantine/hooks'
import { IconPlus } from '@tabler/icons-react'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import {
  MAX_PAGE_CONVERSION_RULES,
  usePageConversionScope,
  type PageConversionScopeRequest,
  type PageConversionSettings,
  type ScopeBucket,
} from '../../api/pageConversion'
import { formatBytes } from '../../format'
import { STOP_LABELS } from './labels'
import { CountLine, RuleCard } from './RuleCard'
import { RULE_TEMPLATES, newRuleDraft, toRuleDtos, type RuleDraft } from './ruleTemplates'

const COUNT_DEBOUNCE_MS = 400

/** Step 3: which files get converted, which are left alone, and at what quality, with counts from the library. */
export function ScopeStep({
  value,
  rules,
  onRulesChange,
  rulesFailed = false,
}: {
  value: PageConversionSettings
  rules: RuleDraft[]
  onRulesChange: (rules: RuleDraft[]) => void
  rulesFailed?: boolean
}) {
  const { t } = useLingui()
  const [settledRules] = useDebouncedValue(rules, COUNT_DEBOUNCE_MS)
  const { format, stop, pngLossless } = value

  const request: PageConversionScopeRequest | null = format
    ? { format, stop, pngLossless, rules: toRuleDtos(settledRules) }
    : null
  const counts = usePageConversionScope(
    request,
    settledRules.map((rule) => rule.key),
  )
  const scope = counts.data
  const stale = settledRules !== rules || counts.isFetching

  const bucketFor = (rule: RuleDraft): ScopeBucket | undefined => {
    const at = scope?.ruleKeys.indexOf(rule.key) ?? -1
    return at >= 0 ? scope?.rules[at] : undefined
  }

  const change = (next: RuleDraft) => onRulesChange(rules.map((rule) => (rule.key === next.key ? next : rule)))

  const move = (index: number, delta: -1 | 1) => {
    const next = [...rules]
    ;[next[index], next[index + delta]] = [next[index + delta], next[index]]
    onRulesChange(next)
  }

  const atLimit = rules.length >= MAX_PAGE_CONVERSION_RULES

  if (!format) {
    return (
      <Text size="sm" c="var(--ink-3)">
        <Trans>Conversion is off, so there is nothing to choose. Continue to save that choice.</Trans>
      </Text>
    )
  }

  const stopLabel = t(STOP_LABELS[stop])
  const overridesSize = formatBytes(scope?.overrides.bytes)
  const compactSize = formatBytes(scope?.alreadyCompact.bytes)
  const earlierSize = formatBytes(scope?.convertedEarlier.bytes)

  return (
    <Stack gap="md">
      {rulesFailed && (
        <Text size="sm" c="var(--warn)" role="alert">
          <Trans>Saved rules could not be loaded; saving will replace them.</Trans>
        </Text>
      )}
      <Text size="sm" c="var(--ink-3)">
        <Trans>
          Rules are checked from the top, and the first one a file matches decides what happens to it. A series set to
          Never or to a fixed quality in its menu ignores the rules.
        </Trans>
      </Text>

      {rules.length > 0 ? (
        <Stack component="ul" gap="sm" p={0} m={0}>
          {rules.map((rule, index) => (
            <RuleCard
              key={rule.key}
              index={index}
              total={rules.length}
              rule={rule}
              bucket={bucketFor(rule)}
              stale={stale}
              onChange={change}
              onMove={(delta) => move(index, delta)}
              onRemove={() => onRulesChange(rules.filter((r) => r.key !== rule.key))}
            />
          ))}
        </Stack>
      ) : (
        <Text size="sm">
          <Trans>No rules yet. Every file is converted at {stopLabel}.</Trans>
        </Text>
      )}

      <Group gap="sm">
        <Button
          variant="default"
          size="xs"
          leftSection={<IconPlus size={14} />}
          disabled={atLimit}
          onClick={() => onRulesChange([...rules, newRuleDraft({ stop })])}
        >
          <Trans>Add rule</Trans>
        </Button>
      </Group>

      <Stack gap={6}>
        <Text size="sm" fw={500}>
          <Trans>Or start from one of these</Trans>
        </Text>
        <Group gap="xs" role="group" aria-label={t`Rule templates`}>
          {RULE_TEMPLATES.map((template) => (
            <Button
              key={template.id}
              variant="light"
              size="xs"
              disabled={atLimit}
              title={t(template.hint)}
              onClick={() => onRulesChange([...rules, newRuleDraft({ name: t(template.title), ...template.rule })])}
            >
              {t(template.title)}
            </Button>
          ))}
        </Group>
      </Stack>

      <Group
        justify="space-between"
        wrap="wrap"
        gap="sm"
        pt="sm"
        style={{ borderTop: '1px solid var(--border)' }}
      >
        <Text size="sm" fw={500}>
          <Trans>Everything else: {stopLabel}</Trans>
        </Text>
        <CountLine bucket={scope?.default} stale={stale} />
      </Group>

      {scope && (
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
                one={`# file is already WebP or AVIF and is left as it is (${compactSize}).`}
                other={`# files are already WebP or AVIF and are left as they are (${compactSize}).`}
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
      )}

      {counts.isError && (
        <Text size="sm" c="var(--danger-fg)">
          <Trans>The counts could not be loaded. The rules still save as you wrote them.</Trans>
        </Text>
      )}
    </Stack>
  )
}
