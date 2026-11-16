import { useMemo } from 'react'
import { useLingui } from '@lingui/react/macro'
import { IconBooks, IconFolder, IconPlus, IconShield, IconTag, IconTags, IconUser } from '../../lib/icons'
import {
  AUTO_TAG_ADDED_VIA,
  AUTO_TAG_ADDED_VIA_LABELS,
  AUTO_TAG_FIELD_LABELS,
  type AutoTagCondition,
  type AutoTagField,
} from '../../api/autoTagRules'
import { CONTENT_RATING_LABELS, useRootFolders, useSeries } from '../../api/hooks'
import { SERIES_TYPE_LABELS } from '../../api/readingProfiles'
import { useUsers } from '../../api/auth'
import { useAuth } from '../../auth/AuthProvider'
import { useLabel } from '../../i18n-context'
import { CONTENT_RATINGS } from '../ContentRatingCards'
import type { DrillChoice } from '../ui/DrillPanel'

/**
 * The condition editor auto-tag rules and root folder rules share. A rule holds groups of
 * conditions: it matches when every condition of any one group holds. Each group is the familiar
 * list of fields with one value each, and "Or" adds another group under it.
 */

export const FIELD_ICONS: Record<AutoTagField, typeof IconTag> = {
  genre: IconTags,
  type: IconBooks,
  contentRating: IconShield,
  addedVia: IconPlus,
  addedBy: IconUser,
  rootFolder: IconFolder,
}

/** Server limit on alternatives per rule (`ConditionGroups.MaxGroups`). */
export const MAX_GROUPS = 10

/** A drill page for one field of one group, e.g. `1:type`. */
export type ConditionPage = `${number}:${AutoTagField}`

export const conditionPage = (group: number, field: AutoTagField): ConditionPage => `${group}:${field}`

/** The parent map entries for every condition page, all opening from `main`. */
export function conditionParents<P extends string>(
  main: P,
  groupCount: number,
  fields: readonly AutoTagField[],
): Record<ConditionPage, P> {
  const parents = {} as Record<ConditionPage, P>
  for (let g = 0; g < groupCount; g++) for (const f of fields) parents[conditionPage(g, f)] = main
  return parents
}

/** The choices for each condition field, plus a lookup from a stored value to its label. */
export function useFieldChoices() {
  const { t, i18n } = useLingui()
  const label = useLabel()
  const { me } = useAuth()
  const isAdmin = me?.isAdmin ?? false
  const { data: users } = useUsers(isAdmin)
  const { data: folders } = useRootFolders()
  const { data: library } = useSeries()

  return useMemo(() => {
    const genres = [...new Set((library ?? []).flatMap((s) => s.genres ?? []))].sort((a, b) => a.localeCompare(b))
    const people = isAdmin ? (users ?? []) : me ? [me] : []
    const choices: Record<AutoTagField, DrillChoice[]> = {
      genre: genres.map((g) => ({ value: g, label: g })),
      type: Object.keys(SERIES_TYPE_LABELS).map((v) => ({ value: v, label: label(SERIES_TYPE_LABELS[v]) })),
      contentRating: CONTENT_RATINGS.map((v) => ({ value: v, label: label(CONTENT_RATING_LABELS[v]) })),
      addedVia: AUTO_TAG_ADDED_VIA.map((v) => ({ value: v, label: label(AUTO_TAG_ADDED_VIA_LABELS[v]) })),
      addedBy: people.map((u) => ({ value: String(u.id), label: u.displayName ?? u.userName })),
      rootFolder: (folders ?? []).map((f) => ({ value: String(f.id), label: f.path })),
    }
    const valueLabel = (field: AutoTagField, value: string): string => {
      const found = choices[field].find((c) => c.value === value)?.label
      if (typeof found === 'string') return found
      if (field === 'addedBy') return t`Unknown user`
      if (field === 'rootFolder') return t`Unknown folder`
      return value
    }
    return { choices, valueLabel }
  }, [library, users, folders, me, isAdmin, label, t, i18n.locale])
}

/** One line per rule: each group's conditions, the groups joined as alternatives in the reader's language. */
export function useGroupsSummary() {
  const { i18n } = useLingui()
  const label = useLabel()
  const { valueLabel } = useFieldChoices()
  return useMemo(() => {
    const or = new Intl.ListFormat(i18n.locale, { type: 'disjunction' })
    return (groups: AutoTagCondition[][]) =>
      or.format(
        groups.map((conditions) =>
          conditions.map((c) => `${label(AUTO_TAG_FIELD_LABELS[c.field])}: ${valueLabel(c.field, c.value)}`).join(' · '),
        ),
      )
  }, [i18n.locale, label, valueLabel])
}

/** The groups as they go to the server: empty groups dropped. */
export const savedGroups = (groups: AutoTagCondition[][]) => groups.filter((g) => g.length > 0)

/** The groups a rule's DTO carries, or its flat list for a server that predates groups. */
export const groupsOf = (rule: { groups?: AutoTagCondition[][]; conditions?: AutoTagCondition[] }) =>
  rule.groups ?? (rule.conditions?.length ? [rule.conditions] : [])

