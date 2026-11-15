import { useState } from 'react'
import {
  ActionIcon,
  Button,
  Group,
  Input,
  Menu,
  MultiSelect,
  NumberInput,
  SegmentedControl,
  Stack,
  TagsInput,
  Text,
  TextInput,
} from '@mantine/core'
import { Note } from '../ui/Note'
import { IconPlus, IconX } from '../../lib/icons'
import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import type { PageConversionConditions, PageConversionFormat, ScopeBucket } from '../../api/pageConversion'
import { useTags } from '../../api/hooks'
import { SERIES_TYPES, SERIES_TYPE_LABELS } from '../../api/readingProfiles'
import { QUALITY_TIERS, QUALITY_TIER_LABELS, type QualityTierName } from '../../api/upgrades'
import { formatBytes } from '../../format'
import { useLabel } from '../../i18n-context'
import { PAGE_FORMAT_LABELS, RULE_PAGE_FORMATS } from './labels'
import { BYTES_PER_MB, hasConditions, type RuleDraft } from './ruleTemplates'

type ConditionKind = 'tiers' | 'sources' | 'formats' | 'seriesTypes' | 'tags' | 'bpp' | 'size'

const CONDITION_KINDS: readonly ConditionKind[] = ['tiers', 'sources', 'formats', 'seriesTypes', 'tags', 'bpp', 'size']

const CONDITION_LABELS: Record<ConditionKind, MessageDescriptor> = {
  tiers: msg`Kind of release`,
  sources: msg`Source`,
  formats: msg`Page format`,
  seriesTypes: msg`Series type`,
  tags: msg`Series tags`,
  bpp: msg`Bits per pixel`,
  size: msg`File size`,
}

const isSet = (kind: ConditionKind, c: PageConversionConditions): boolean => {
  switch (kind) {
    case 'tiers':
      return (c.tiers?.length ?? 0) > 0
    case 'sources':
      return (c.sources?.length ?? 0) > 0
    case 'formats':
      return (c.formats?.length ?? 0) > 0
    case 'seriesTypes':
      return (c.seriesTypes?.length ?? 0) > 0
    case 'tags':
      return (c.tags?.length ?? 0) > 0
    case 'bpp':
      return c.bppUnder !== undefined || c.bppOver !== undefined
    case 'size':
      return c.sizeOverBytes !== undefined || c.sizeUnderBytes !== undefined
  }
}

const CLEARED: Record<ConditionKind, Partial<PageConversionConditions>> = {
  tiers: { tiers: undefined },
  sources: { sources: undefined },
  formats: { formats: undefined },
  seriesTypes: { seriesTypes: undefined },
  tags: { tags: undefined },
  bpp: { bppUnder: undefined, bppOver: undefined },
  size: { sizeOverBytes: undefined, sizeUnderBytes: undefined },
}

const toMb = (bytes: number | undefined): number | undefined =>
  bytes === undefined ? undefined : Math.round((bytes / BYTES_PER_MB) * 100) / 100

const fromMb = (mb: number | undefined): number | undefined =>
  mb === undefined ? undefined : Math.round(mb * BYTES_PER_MB)

/**
 * Holds what the person is typing. "1." is not a number yet, and a controlled field that is handed
 * the last number back would erase the dot, so the text stays here and only a valid number goes up.
 */
function DecimalInput({
  label,
  ariaLabel,
  suffix,
  value,
  onValue,
}: {
  label: string
  ariaLabel: string
  suffix?: string
  value: number | undefined
  onValue: (value: number | undefined) => void
}) {
  const [raw, setRaw] = useState<number | string>(value ?? '')
  return (
    <NumberInput
      label={label}
      aria-label={ariaLabel}
      suffix={suffix}
      value={raw}
      onChange={(next) => {
        setRaw(next)
        onValue(typeof next === 'number' ? next : undefined)
      }}
      min={0.01}
      decimalScale={2}
      hideControls
    />
  )
}

/** "Kind of release: Volume; Source: torrent:*", or null for a rule with no conditions. */
export function useConditionsSummary() {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const range = (over: number | undefined, under: number | undefined, suffix: string): string => {
    const low = over === undefined ? '' : `${over}${suffix}`
    const high = under === undefined ? '' : `${under}${suffix}`
    if (low && high) return t`over ${low}, under ${high}`
    if (low) return t`over ${low}`
    return t`under ${high}`
  }
  const valuesOf = (kind: ConditionKind, c: PageConversionConditions): string => {
    switch (kind) {
      case 'tiers':
        return (c.tiers ?? []).map((tier) => renderLabel(QUALITY_TIER_LABELS[tier])).join(', ')
      case 'sources':
        return (c.sources ?? []).join(', ')
      case 'formats':
        return (c.formats ?? [])
          .map((f) => {
            const known = PAGE_FORMAT_LABELS[f as keyof typeof PAGE_FORMAT_LABELS]
            return known ? renderLabel(known) : f
          })
          .join(', ')
      case 'seriesTypes':
        return (c.seriesTypes ?? [])
          .map((type) => (SERIES_TYPE_LABELS[type] ? renderLabel(SERIES_TYPE_LABELS[type]) : type))
          .join(', ')
      case 'tags':
        return (c.tags ?? []).join(', ')
      case 'bpp':
        return range(c.bppOver, c.bppUnder, '')
      case 'size':
        return range(toMb(c.sizeOverBytes), toMb(c.sizeUnderBytes), t` MB`)
    }
  }
  return (c: PageConversionConditions): string | null => {
    const parts = CONDITION_KINDS.filter((kind) => isSet(kind, c)).map((kind) => {
      const label = renderLabel(CONDITION_LABELS[kind])
      const values = valuesOf(kind, c)
      return t`${label}: ${values}`
    })
    return parts.length === 0 ? null : parts.join('; ')
  }
}

/** One rule's name, conditions and action. Its targets are set from the plan table. */
export function RuleEditor({
  index,
  rule,
  defaultFormat,
  onChange,
}: {
  /** Zero-based. */
  index: number
  rule: RuleDraft
  /** The plan's format, which a rule saved without one converts in. */
  defaultFormat: PageConversionFormat
  onChange: (rule: RuleDraft) => void
}) {
  const { t } = useLingui()
  const mbSuffix = t` MB`
  const renderLabel = useLabel()
  const { data: tags } = useTags()
  const [opened, setOpened] = useState<ConditionKind[]>([])
  const number = index + 1
  const c = rule.conditions

  const visible = CONDITION_KINDS.filter((kind) => isSet(kind, c) || opened.includes(kind))
  const addable = CONDITION_KINDS.filter((kind) => !visible.includes(kind))

  const setConditions = (patch: Partial<PageConversionConditions>) =>
    onChange({ ...rule, conditions: { ...c, ...patch } })

  const removeCondition = (kind: ConditionKind) => {
    setOpened((previous) => previous.filter((k) => k !== kind))
    setConditions(CLEARED[kind])
  }

  const tagLabels = [...new Set([...(tags ?? []).map((tag) => tag.label), ...(c.tags ?? [])])]

  const editor = (kind: ConditionKind) => {
    switch (kind) {
      case 'tiers':
        return (
          <MultiSelect
            label={renderLabel(CONDITION_LABELS.tiers)}
            data={[...QUALITY_TIERS].reverse().map((tier) => ({ value: tier, label: renderLabel(QUALITY_TIER_LABELS[tier]) }))}
            value={c.tiers ?? []}
            onChange={(next) => setConditions({ tiers: next as QualityTierName[] })}
            placeholder={t`Any kind`}
            style={{ flex: 1 }}
          />
        )
      case 'sources':
        return (
          <TagsInput
            label={renderLabel(CONDITION_LABELS.sources)}
            description={t`Names such as import or mangafire. End with * to match a prefix, like torrent:*.`}
            value={c.sources ?? []}
            onChange={(next) => setConditions({ sources: next })}
            splitChars={[',', ' ']}
            placeholder="torrent:*"
            style={{ flex: 1 }}
          />
        )
      case 'formats':
        return (
          <MultiSelect
            label={renderLabel(CONDITION_LABELS.formats)}
            data={RULE_PAGE_FORMATS.map((format) => ({ value: format, label: renderLabel(PAGE_FORMAT_LABELS[format]) }))}
            value={c.formats ?? []}
            onChange={(next) => setConditions({ formats: next })}
            placeholder={t`Any format`}
            style={{ flex: 1 }}
          />
        )
      case 'seriesTypes':
        return (
          <MultiSelect
            label={renderLabel(CONDITION_LABELS.seriesTypes)}
            data={SERIES_TYPES.map((type) => ({ value: type, label: renderLabel(SERIES_TYPE_LABELS[type]) }))}
            value={c.seriesTypes ?? []}
            onChange={(next) => setConditions({ seriesTypes: next })}
            placeholder={t`Any type`}
            style={{ flex: 1 }}
          />
        )
      case 'tags':
        return (
          <MultiSelect
            label={renderLabel(CONDITION_LABELS.tags)}
            description={t`Matches a series with any of these tags.`}
            data={tagLabels}
            value={c.tags ?? []}
            onChange={(next) => setConditions({ tags: next })}
            searchable
            nothingFoundMessage={t`No tags`}
            placeholder={t`Any tags`}
            style={{ flex: 1 }}
          />
        )
      case 'bpp':
        return (
          <Input.Wrapper
            label={renderLabel(CONDITION_LABELS.bpp)}
            description={t`How hard the pages are compressed. A low number means a small, heavily compressed file.`}
            style={{ flex: 1 }}
          >
            <Group gap="sm" mt={4} grow>
              <DecimalInput
                ariaLabel={t`Bits per pixel, under`}
                label={t`Under`}
                value={c.bppUnder}
                onValue={(next) => setConditions({ bppUnder: next })}
              />
              <DecimalInput
                ariaLabel={t`Bits per pixel, over`}
                label={t`Over`}
                value={c.bppOver}
                onValue={(next) => setConditions({ bppOver: next })}
              />
            </Group>
          </Input.Wrapper>
        )
      case 'size':
        return (
          <Input.Wrapper label={renderLabel(CONDITION_LABELS.size)} style={{ flex: 1 }}>
            <Group gap="sm" mt={4} grow>
              <DecimalInput
                ariaLabel={t`File size in MB, over`}
                label={t`Over`}
                suffix={mbSuffix}
                value={toMb(c.sizeOverBytes)}
                onValue={(next) => setConditions({ sizeOverBytes: fromMb(next) })}
              />
              <DecimalInput
                ariaLabel={t`File size in MB, under`}
                label={t`Under`}
                suffix={mbSuffix}
                value={toMb(c.sizeUnderBytes)}
                onValue={(next) => setConditions({ sizeUnderBytes: fromMb(next) })}
              />
            </Group>
          </Input.Wrapper>
        )
    }
  }

  return (
    <Stack gap="sm" className="pc-rule-editor">
      <TextInput
        label={t`Name`}
        aria-label={t`Name of rule ${number}`}
        placeholder={t`Optional`}
        value={rule.name}
        onChange={(event) => onChange({ ...rule, name: event.currentTarget.value })}
        maxLength={80}
        size="xs"
        maw={420}
      />

      {visible.length > 0 && (
        <Stack gap="sm">
          {visible.map((kind) => {
            const conditionLabel = renderLabel(CONDITION_LABELS[kind])
            return (
              <Group key={kind} gap="xs" wrap="nowrap" align="flex-end">
                {editor(kind)}
                <ActionIcon
                  variant="subtle"
                  color="var(--neutral)"
                  mb={2}
                  aria-label={t`Remove condition: ${conditionLabel}`}
                  onClick={() => removeCondition(kind)}
                >
                  <IconX size={16} />
                </ActionIcon>
              </Group>
            )
          })}
        </Stack>
      )}

      <Group gap="sm">
        <Menu position="bottom-start" withinPortal>
          <Menu.Target>
            <Button
              variant="default"
              size="xs"
              leftSection={<IconPlus size={16} />}
              disabled={addable.length === 0}
              aria-label={t`Add condition to rule ${number}`}
            >
              <Trans>Add condition</Trans>
            </Button>
          </Menu.Target>
          <Menu.Dropdown>
            {addable.map((kind) => (
              <Menu.Item key={kind} onClick={() => setOpened((previous) => [...previous, kind])}>
                {renderLabel(CONDITION_LABELS[kind])}
              </Menu.Item>
            ))}
          </Menu.Dropdown>
        </Menu>
        {!hasConditions(c) && (
          <Note tone="warn">
            <Trans>No conditions, so this rule matches every file that reaches it.</Trans>
          </Note>
        )}
      </Group>

      <Group gap="sm" align="center">
        <Text size="sm" fw={500}>
          <Trans>Matching files are</Trans>
        </Text>
        <SegmentedControl
          size="xs"
          aria-label={t`Action for rule ${number}`}
          value={rule.action}
          data={[
            { value: 'convert', label: t`Converted` },
            { value: 'skip', label: t`Skipped` },
          ]}
          onChange={(next) => {
            if (next === 'skip') onChange({ ...rule, action: 'skip', stop: null })
            else onChange({ ...rule, action: 'convert', stop: rule.stop ?? 'high', format: rule.format ?? defaultFormat })
          }}
        />
      </Group>
    </Stack>
  )
}

/** "365 files, 73.8 GB", dimmed while a newer count is on its way. */
export function CountLine({ bucket, stale }: { bucket: ScopeBucket | undefined; stale: boolean }) {
  if (!bucket) {
    return (
      <Text size="sm" c="var(--ink-3)">
        <Trans>Counting files</Trans>
      </Text>
    )
  }
  const size = formatBytes(bucket.bytes)
  return (
    <Text size="sm" c="var(--ink-3)" aria-live="polite" style={{ opacity: stale ? 0.55 : 1 }}>
      {bucket.files === 0 ? (
        <Trans>No files match</Trans>
      ) : (
        <Plural value={bucket.files} one={`# file, ${size}`} other={`# files, ${size}`} />
      )}
    </Text>
  )
}
