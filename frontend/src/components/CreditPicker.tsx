import { useState } from 'react'
import { useLingui } from '@lingui/react/macro'
import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { MultiSelect } from '@mantine/core'
import { useDebouncedValue } from '@mantine/hooks'
import { useCreditSuggestions, type CatalogueCredit } from '../api/hooks'
import { useLabel } from '../i18n-context'

/** Descriptors, rendered with `useLabel()`. */
export const CREDIT_ROLE_LABELS: Record<string, MessageDescriptor> = {
  author: msg`Story`,
  artist: msg`Art`,
  studio: msg`Studio`,
}

const SEP = '\u0001'
const valueOf = (c: CatalogueCredit) => `${c.role ?? ''}${SEP}${c.name}`
function creditOf(value: string): CatalogueCredit {
  const [role, name] = value.split(SEP)
  return role ? { name, role: role as CatalogueCredit['role'] } : { name }
}

/**
 * Creators and studios for a catalogue filter, searched against the credit index. A pick from the
 * search covers every role the name holds; a role-specific credit (one followed from a creator page
 * opened as "Story" or "Studio") keeps its role and says so in its label.
 */
export function CreditPicker({
  value,
  onChange,
}: {
  value: CatalogueCredit[]
  onChange: (value: CatalogueCredit[]) => void
}) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const [search, setSearch] = useState('')
  const [debounced] = useDebouncedValue(search, 250)
  const { data: suggestions } = useCreditSuggestions(debounced)

  const labelOf = (c: CatalogueCredit) =>
    c.role ? `${c.name} (${renderLabel(CREDIT_ROLE_LABELS[c.role] ?? c.role)})` : c.name

  // The picked credits stay in the options, or the select could not render their chips once the
  // search moves on.
  const options = new Map<string, string>()
  for (const c of value) options.set(valueOf(c), labelOf(c))
  for (const s of suggestions ?? []) {
    const key = valueOf({ name: s.name })
    if (!options.has(key)) options.set(key, s.name)
  }
  const data = [...options].map(([v, label]) => ({ value: v, label }))

  return (
    <MultiSelect
      label={t`Creators and studios`}
      description={t`Titles credited to any of them.`}
      placeholder={value.length ? undefined : t`Search by name`}
      data={data}
      value={value.map(valueOf)}
      onChange={(values) => onChange(values.map(creditOf))}
      searchable
      searchValue={search}
      onSearchChange={setSearch}
      // The server already matched the name, romanization and all; a second pass on the label
      // would drop "Junji Itou" for "ito".
      filter={({ options }) => options}
      nothingFoundMessage={debounced.trim().length >= 2 ? t`No matches` : t`Type to search…`}
      hidePickedOptions
      clearable
      maxDropdownHeight={260}
    />
  )
}
