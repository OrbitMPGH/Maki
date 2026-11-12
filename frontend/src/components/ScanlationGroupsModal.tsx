import { useEffect, useState, type ReactNode } from 'react'
import { ActionIcon, Autocomplete, Button, Group, Loader, Modal, Stack, Text } from '@mantine/core'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { IconBan, IconChevronDown, IconChevronUp, IconX } from '../lib/icons'
import { ModalFooter, ModalHeading, ModalNote } from './ui/ModalShell'
import { errorText } from '../api/errorText'
import {
  useGlobalScanlationGroups,
  useSaveGlobalScanlationGroups,
  useSaveSeriesScanlationGroups,
  useSeriesScanlationGroups,
  type ScanlationGroupLists,
  type SeenScanlationGroup,
} from '../api/scanlationGroups'

const same = (a: string, b: string) => a.trim().toLocaleLowerCase() === b.trim().toLocaleLowerCase()
const has = (list: string[], name: string) => list.some((n) => same(n, name))

/** A text box that offers the groups seen on the site and also takes a name typed by hand. */
function AddGroup({ label, placeholder, seen, taken, onAdd }: {
  label: string
  placeholder: string
  seen: SeenScanlationGroup[]
  taken: string[]
  onAdd: (name: string) => void
}) {
  const [value, setValue] = useState('')
  const counts = new Map(seen.map((g) => [g.name, g.chapters]))
  const add = (name: string) => {
    const trimmed = name.trim()
    if (trimmed && !has(taken, trimmed)) onAdd(trimmed)
    setValue('')
  }
  return (
    <Autocomplete
      aria-label={label}
      placeholder={placeholder}
      value={value}
      onChange={setValue}
      data={seen.filter((g) => !has(taken, g.name)).map((g) => g.name)}
      limit={30}
      maxDropdownHeight={220}
      comboboxProps={{ withinPortal: false }}
      onOptionSubmit={add}
      onKeyDown={(e) => {
        if (e.key === 'Enter' && value.trim()) {
          e.preventDefault()
          add(value)
        }
      }}
      renderOption={({ option }) => {
        const chapters = counts.get(option.value) ?? 0
        return (
          <Group justify="space-between" wrap="nowrap" w="100%">
            <span>{option.value}</span>
            <Text size="xs" c="var(--ink-3)"><Plural value={chapters} one="# chapter" other="# chapters" /></Text>
          </Group>
        )
      }}
    />
  )
}

function GroupRows({ names, ordered, emptyText, onChange, removeLabel }: {
  names: string[]
  ordered: boolean
  emptyText: ReactNode
  onChange: (names: string[]) => void
  removeLabel: (name: string) => string
}) {
  const { t } = useLingui()
  if (names.length === 0) {
    return <Text size="sm" c="var(--ink-3)">{emptyText}</Text>
  }
  const move = (index: number, by: number) => {
    const next = [...names]
    const [moved] = next.splice(index, 1)
    next.splice(index + by, 0, moved)
    onChange(next)
  }
  return (
    <ol className="group-list" data-ordered={ordered || undefined}>
      {names.map((name, i) => (
        <li key={name} className="group-list-row">
          {ordered && <span className="group-list-rank tnum">{i + 1}</span>}
          <span className="group-list-name">{name}</span>
          {ordered && (
            <>
              <ActionIcon
                variant="subtle" color="var(--neutral)" size="sm" disabled={i === 0}
                aria-label={t`Move ${name} up`} onClick={() => move(i, -1)}
              >
                <IconChevronUp size={16} />
              </ActionIcon>
              <ActionIcon
                variant="subtle" color="var(--neutral)" size="sm" disabled={i === names.length - 1}
                aria-label={t`Move ${name} down`} onClick={() => move(i, 1)}
              >
                <IconChevronDown size={16} />
              </ActionIcon>
            </>
          )}
          <ActionIcon
            variant="subtle" color="var(--neutral)" size="sm"
            aria-label={removeLabel(name)} onClick={() => onChange(names.filter((n) => n !== name))}
          >
            <IconX size={16} />
          </ActionIcon>
        </li>
      ))}
    </ol>
  )
}

/**
 * The two lists, preferred groups in order and blocked groups, with the groups seen on the site
 * offered as suggestions. A name typed by hand is kept as it is.
 */
export function ScanlationGroupsEditor({ value, onChange, seen, inherited }: {
  value: ScanlationGroupLists
  onChange: (value: ScanlationGroupLists) => void
  seen: SeenScanlationGroup[]
  /** The global lists, shown on a series where its own lists say nothing. */
  inherited?: ScanlationGroupLists
}) {
  const { t } = useLingui()
  const taken = [...value.preferred, ...value.blocked]
  const inheritedPreferred = inherited?.preferred.join(', ') ?? ''
  const inheritedBlocked = inherited?.blocked.join(', ') ?? ''
  return (
    <Stack gap="lg">
      <Stack gap={6}>
        <Text size="sm" fw={600} c="var(--ink-hi)"><Trans>Preferred groups</Trans></Text>
        <Text size="xs" c="var(--ink-3)">
          <Trans>A chapter is taken from the first group on this list that uploaded it. With none of them, the earliest upload is taken.</Trans>
        </Text>
        <GroupRows
          names={value.preferred}
          ordered
          emptyText={
            inherited && inherited.preferred.length > 0
              ? <Trans>None for this series, so the global order applies: {inheritedPreferred}</Trans>
              : <Trans>No preferred groups.</Trans>
          }
          onChange={(preferred) => onChange({ ...value, preferred })}
          removeLabel={(name) => t`Remove ${name} from the preferred groups`}
        />
        <AddGroup
          label={t`Add a preferred group`}
          placeholder={t`Add a preferred group`}
          seen={seen}
          taken={taken}
          onAdd={(name) => onChange({ ...value, preferred: [...value.preferred, name] })}
        />
      </Stack>
      <Stack gap={6}>
        <Group gap={6}>
          <IconBan size={16} color="var(--danger)" aria-hidden />
          <Text size="sm" fw={600} c="var(--ink-hi)"><Trans>Blocked groups</Trans></Text>
        </Group>
        <Text size="xs" c="var(--ink-3)">
          <Trans>Never taken. A chapter only these groups uploaded comes from another source, or waits until one has it.</Trans>
        </Text>
        <GroupRows
          names={value.blocked}
          ordered={false}
          emptyText={<Trans>No blocked groups.</Trans>}
          onChange={(blocked) => onChange({ ...value, blocked })}
          removeLabel={(name) => t`Unblock ${name}`}
        />
        {inherited && inherited.blocked.length > 0 && (
          <Text size="xs" c="var(--ink-3)"><Trans>Also blocked everywhere: {inheritedBlocked}</Trans></Text>
        )}
        <AddGroup
          label={t`Block a group`}
          placeholder={t`Block a group`}
          seen={seen}
          taken={taken}
          onAdd={(name) => onChange({ ...value, blocked: [...value.blocked, name] })}
        />
      </Stack>
    </Stack>
  )
}

function useDraft(source: ScanlationGroupLists | undefined, opened: boolean) {
  const [draft, setDraft] = useState<ScanlationGroupLists>({ preferred: [], blocked: [] })
  const preferred = source?.preferred.join('\n')
  const blocked = source?.blocked.join('\n')
  useEffect(() => {
    if (opened && preferred !== undefined && blocked !== undefined) {
      setDraft({ preferred: preferred ? preferred.split('\n') : [], blocked: blocked ? blocked.split('\n') : [] })
    }
  }, [opened, preferred, blocked])
  const dirty = source !== undefined &&
    (draft.preferred.join('\n') !== preferred || draft.blocked.join('\n') !== blocked)
  return { draft, setDraft, dirty }
}

function Shell({ opened, onClose, description, children, footer }: {
  opened: boolean
  onClose: () => void
  description: ReactNode
  children: ReactNode
  footer: ReactNode
}) {
  const { t } = useLingui()
  return (
    <Modal
      opened={opened}
      onClose={onClose}
      size={520}
      closeButtonProps={{ 'aria-label': t`Close` }}
      title={<ModalHeading title={<Trans>Scanlation groups</Trans>} description={description} />}
    >
      <Stack gap="md">
        {children}
        {footer}
      </Stack>
    </Modal>
  )
}

/** The global lists, from a source's row in Manage sources. */
export function GlobalScanlationGroupsModal({ opened, onClose, onSaved }: {
  opened: boolean
  onClose: () => void
  onSaved?: () => void
}) {
  const { data, isPending, error } = useGlobalScanlationGroups(opened)
  const save = useSaveGlobalScanlationGroups()
  const { draft, setDraft, dirty } = useDraft(data, opened)
  return (
    <Shell
      opened={opened}
      onClose={onClose}
      description={<Trans>Which group's upload Maki takes when a source has several. Applies to every series.</Trans>}
      footer={
        <ModalFooter status={<Trans>Takes effect at each series' next chapter refresh.</Trans>}>
          <Button variant="default" onClick={onClose}><Trans>Cancel</Trans></Button>
          <Button
            disabled={!dirty}
            loading={save.isPending}
            onClick={() => save.mutate(draft, { onSuccess: () => { onSaved?.(); onClose() } })}
          >
            <Trans>Save</Trans>
          </Button>
        </ModalFooter>
      }
    >
      {isPending && <Group justify="center" p="md"><Loader size="sm" /></Group>}
      {error && <ModalNote tone="danger">{errorText(error)}</ModalNote>}
      {save.error && <ModalNote tone="danger">{errorText(save.error)}</ModalNote>}
      {data && <ScanlationGroupsEditor value={draft} onChange={setDraft} seen={data.seen} />}
    </Shell>
  )
}

/** One series' own lists, from the series page's actions menu. */
export function SeriesScanlationGroupsModal({ seriesId, opened, onClose, onSaved }: {
  seriesId: number
  opened: boolean
  onClose: () => void
  /** After a save, so the caller can refresh the chapter list and have the new pick linked. */
  onSaved?: () => void
}) {
  const { data, isPending, error } = useSeriesScanlationGroups(seriesId, opened)
  const save = useSaveSeriesScanlationGroups(seriesId)
  const { draft, setDraft, dirty } = useDraft(data, opened)
  const unreachable = data?.unreachable.join(', ') ?? ''
  const sources = data?.sources.join(', ') ?? ''
  return (
    <Shell
      opened={opened}
      onClose={onClose}
      description={<Trans>For this series only. Its preferred list replaces the global one; blocked groups add to it.</Trans>}
      footer={
        <ModalFooter status={sources ? <Trans>Applies to {sources}.</Trans> : undefined}>
          <Button variant="default" onClick={onClose}><Trans>Cancel</Trans></Button>
          <Button
            disabled={!dirty}
            loading={save.isPending}
            onClick={() => save.mutate(draft, { onSuccess: () => { onSaved?.(); onClose() } })}
          >
            <Trans>Save</Trans>
          </Button>
        </ModalFooter>
      }
    >
      {isPending && <Group justify="center" p="md"><Loader size="sm" /></Group>}
      {error && <ModalNote tone="danger">{errorText(error)}</ModalNote>}
      {save.error && <ModalNote tone="danger">{errorText(save.error)}</ModalNote>}
      {data && data.unreachable.length > 0 && (
        <ModalNote tone="warn">
          <Trans>Could not read the groups on {unreachable} just now. You can still type a name.</Trans>
        </ModalNote>
      )}
      {data && <ScanlationGroupsEditor value={draft} onChange={setDraft} seen={data.seen} inherited={data.global} />}
    </Shell>
  )
}
