import { type ReactNode, type RefObject } from 'react'
import { ActionIcon, Button, Group, Text, Tooltip } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { IconPlus, IconTrash } from '../../lib/icons'
import { AUTO_TAG_FIELD_LABELS, type AutoTagCondition, type AutoTagField } from '../../api/autoTagRules'
import { useLabel } from '../../i18n-context'
import { DrillBack, DrillChoices, DrillRow, type DrillChoice } from '../ui/DrillPanel'
import { FIELD_ICONS, MAX_GROUPS, conditionPage, useFieldChoices } from './conditionGroups'

/** The editor pieces auto-tag rules and root folder rules share; the logic lives in `conditionGroups.ts`. */

interface PagesHandle {
  open: (to: string) => void
  back: () => void
  rowRef: (p: string) => (node: HTMLButtonElement | null) => void
  backRef: RefObject<HTMLButtonElement | null>
}

/** The first page's share: each group's field rows, a way to remove a later group, and Or. */
export function ConditionGroupRows({
  groups,
  fields,
  heading,
  pages,
  onChange,
}: {
  groups: AutoTagCondition[][]
  fields: readonly AutoTagField[]
  /** Over the first group, e.g. "When a series is added and matches all of these". */
  heading: ReactNode
  pages: PagesHandle
  onChange: (groups: AutoTagCondition[][]) => void
}) {
  const { t } = useLingui()
  const label = useLabel()
  const { valueLabel } = useFieldChoices()
  const shown = groups.length > 0 ? groups : [[]]
  const canAddGroup = shown.length < MAX_GROUPS && shown[shown.length - 1].length > 0

  return (
    <>
      {shown.map((conditions, g) => {
        const number = g + 1
        return (
        <div key={g} role="group" aria-label={g === 0 ? t`Conditions` : t`Alternative ${number}`}>
          <Group justify="space-between" wrap="nowrap" px={10} pt={10} pb={4} gap="xs">
            <Text size="xs" c="var(--ink-3)">
              {g === 0 ? heading : <Trans>Or all of these</Trans>}
            </Text>
            {g > 0 && (
              <Tooltip label={t`Remove these conditions`} withArrow>
                <ActionIcon
                  variant="subtle"
                  size="sm"
                  color="var(--danger)"
                  aria-label={t`Remove these conditions`}
                  onClick={() => onChange(shown.filter((_, i) => i !== g))}
                >
                  <IconTrash size={14} />
                </ActionIcon>
              </Tooltip>
            )}
          </Group>
          {fields.map((field) => {
            const value = conditions.find((c) => c.field === field)?.value ?? ''
            const page = conditionPage(g, field)
            return (
              <DrillRow
                key={field}
                ref={pages.rowRef(page)}
                icon={FIELD_ICONS[field]}
                label={label(AUTO_TAG_FIELD_LABELS[field])}
                value={value ? valueLabel(field, value) : t`Any`}
                onClick={() => pages.open(page)}
              />
            )
          })}
        </div>
        )
      })}
      {canAddGroup && (
        <Group px={10} pt={6}>
          <Button
            variant="subtle"
            size="xs"
            leftSection={<IconPlus size={14} />}
            onClick={() => onChange([...shown, []])}
          >
            <Trans>Or match other conditions</Trans>
          </Button>
        </Group>
      )}
    </>
  )
}

/** The sub-page for one field of one group, or nothing when `page` is not a condition page. */
export function ConditionFieldPage({
  page,
  groups,
  fields,
  pages,
  onChange,
}: {
  page: string
  groups: AutoTagCondition[][]
  fields: readonly AutoTagField[]
  pages: PagesHandle
  onChange: (groups: AutoTagCondition[][]) => void
}) {
  const { t } = useLingui()
  const label = useLabel()
  const { choices, valueLabel } = useFieldChoices()
  const [index, field] = page.split(':') as [string, AutoTagField]
  const g = Number(index)
  if (!fields.includes(field) || Number.isNaN(g)) return null

  const shown = groups.length > 0 ? groups : [[]]
  const conditions = shown[g] ?? []
  const current = conditions.find((c) => c.field === field)?.value ?? ''
  const known = current === '' || choices[field].some((o) => o.value === current)
  const options: DrillChoice[] = [
    { value: '', label: t`Any` },
    ...choices[field],
    ...(known ? [] : [{ value: current, label: valueLabel(field, current) }]),
  ]
  const title = label(AUTO_TAG_FIELD_LABELS[field])

  const pick = (value: string) => {
    const next = shown.map((group, i) =>
      i !== g ? group : [...group.filter((c) => c.field !== field), ...(value ? [{ field, value }] : [])],
    )
    onChange(next)
    pages.back()
  }

  return (
    <div>
      <DrillBack ref={pages.backRef} title={title} onBack={pages.back} />
      <div className="drill-scroll">
        <DrillChoices label={title} value={current} options={options} onPick={pick} />
      </div>
    </div>
  )
}
