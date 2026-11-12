import { useMemo, useState, type CSSProperties } from 'react'
import { ActionIcon, Button, Group, Modal, Stack, Switch, Text, Tooltip } from '@mantine/core'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { plural, t as now } from '@lingui/core/macro'
import {
  IconBooks,
  IconFolder,
  IconPencil,
  IconPlus,
  IconShield,
  IconTag,
  IconTags,
  IconTrash,
  IconUser,
  IconWand,
} from '../../lib/icons'
import { notifications } from '../../lib/toast'
import {
  AUTO_TAG_ADDED_VIA,
  AUTO_TAG_ADDED_VIA_LABELS,
  AUTO_TAG_FIELD_LABELS,
  AUTO_TAG_FIELDS,
  useApplyAutoTagRule,
  useAutoTagRulePreview,
  useAutoTagRules,
  useDeleteAutoTagRule,
  useSaveAutoTagRule,
  type AutoTagCondition,
  type AutoTagField,
  type AutoTagRule,
  type SaveAutoTagRule,
} from '../../api/autoTagRules'
import { CONTENT_RATING_LABELS, useRootFolders, useSeries, useTags } from '../../api/hooks'
import { SERIES_TYPE_LABELS } from '../../api/readingProfiles'
import { useUsers } from '../../api/auth'
import { useAuth } from '../../auth/AuthProvider'
import { useLabel } from '../../i18n-context'
import { CONTENT_RATINGS } from '../ContentRatingCards'
import { ConfirmDialog } from '../ui/ConfirmDialog'
import { DrillPanes } from '../ui/DrillPanes'
import { DrillBack, DrillChoices, DrillRow, useDrillPages, type DrillChoice } from '../ui/DrillPanel'
import { EmptyState } from '../ui/EmptyState'
import { ModalFooter, ModalHeading } from '../ui/ModalShell'

const FIELD_ICONS: Record<AutoTagField, typeof IconTag> = {
  genre: IconTags,
  type: IconBooks,
  contentRating: IconShield,
  addedVia: IconPlus,
  addedBy: IconUser,
  rootFolder: IconFolder,
}

type EditorPage = 'main' | 'tag' | AutoTagField

const EDITOR_PARENTS: Record<EditorPage, EditorPage> = {
  main: 'main',
  tag: 'main',
  genre: 'main',
  type: 'main',
  contentRating: 'main',
  addedVia: 'main',
  addedBy: 'main',
  rootFolder: 'main',
}

/** The choices for each condition field, plus a lookup from a stored value to its label. */
function useFieldChoices() {
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

/** Library-wide auto-tag rules: a list, and an editor for one rule in the drill panel grammar. */
export function AutoTagRulesModal({ opened, onClose }: { opened: boolean; onClose: () => void }) {
  const { t } = useLingui()
  const [editing, setEditing] = useState<{ id: number | null; draft: SaveAutoTagRule } | null>(null)

  const close = () => {
    setEditing(null)
    onClose()
  }

  return (
    <Modal
      opened={opened}
      onClose={close}
      size="md"
      title={
        <ModalHeading
          title={
            editing == null ? t`Auto-tag rules` : editing.id == null ? t`New rule` : t`Edit rule`
          }
          description={
            editing == null ? (
              <Trans>Tag a series when it is added and matches every condition of a rule.</Trans>
            ) : undefined
          }
        />
      }
    >
      {editing == null ? (
        <RuleList
          onAdd={() => setEditing({ id: null, draft: { tagId: 0, enabled: true, conditions: [] } })}
          onEdit={(rule) =>
            setEditing({ id: rule.id, draft: { tagId: rule.tagId, enabled: rule.enabled, conditions: rule.conditions } })
          }
        />
      ) : (
        <RuleEditor
          key={editing.id ?? 'new'}
          id={editing.id}
          initial={editing.draft}
          onDone={() => setEditing(null)}
        />
      )}
    </Modal>
  )
}

function RuleList({ onAdd, onEdit }: { onAdd: () => void; onEdit: (rule: AutoTagRule) => void }) {
  const { t } = useLingui()
  const label = useLabel()
  const { data: rules, isLoading } = useAutoTagRules()
  const { data: tags } = useTags()
  const { valueLabel } = useFieldChoices()
  const save = useSaveAutoTagRule()
  const remove = useDeleteAutoTagRule()
  const apply = useApplyAutoTagRule()
  const [applying, setApplying] = useState<AutoTagRule | null>(null)
  const [deleting, setDeleting] = useState<AutoTagRule | null>(null)
  const preview = useAutoTagRulePreview(applying?.id ?? null)
  const previewCount = preview.data?.count ?? 0

  const tagOf = (rule: AutoTagRule) => tags?.find((x) => x.id === rule.tagId)
  const applyingTag = applying ? (tagOf(applying)?.label ?? '') : ''

  const summary = (conditions: AutoTagCondition[]) =>
    conditions.map((c) => `${label(AUTO_TAG_FIELD_LABELS[c.field])}: ${valueLabel(c.field, c.value)}`).join(' · ')

  return (
    <Stack gap="sm">
      {!isLoading && (rules ?? []).length === 0 ? (
        <EmptyState
          compact
          title={t`No rules yet`}
          description={t`A rule tags new series that match it, for example every manhwa from an import list.`}
        />
      ) : (
        (rules ?? []).map((rule) => {
          const tag = tagOf(rule)
          return (
            <Group key={rule.id} gap="sm" wrap="nowrap" align="flex-start">
              <Switch
                mt={2}
                checked={rule.enabled}
                aria-label={t`Rule is on`}
                onChange={(e) =>
                  save.mutate({
                    id: rule.id,
                    rule: { tagId: rule.tagId, enabled: e.currentTarget.checked, conditions: rule.conditions },
                  })
                }
              />
              <Stack gap={2} style={{ flex: 1, minWidth: 0 }}>
                <Group gap={6} wrap="nowrap">
                  {tag && (
                    <i
                      className="tag-dot"
                      style={{ '--bucket': `var(--mantine-color-${tag.color}-6)` } as CSSProperties}
                    />
                  )}
                  <Text size="sm" fw={600} truncate>
                    {tag?.label ?? t`Deleted tag`}
                  </Text>
                </Group>
                <Text size="xs" c="var(--ink-3)">
                  {summary(rule.conditions)}
                </Text>
              </Stack>
              <Group gap={4} wrap="nowrap">
                <Tooltip label={t`Apply to existing series`} withArrow>
                  <ActionIcon
                    variant="subtle"
                    aria-label={t`Apply to existing series`}
                    onClick={() => setApplying(rule)}
                  >
                    <IconWand size={16} />
                  </ActionIcon>
                </Tooltip>
                <ActionIcon variant="subtle" aria-label={t`Edit rule`} onClick={() => onEdit(rule)}>
                  <IconPencil size={16} />
                </ActionIcon>
                <ActionIcon
                  variant="subtle"
                  color="var(--danger)"
                  aria-label={t`Delete rule`}
                  onClick={() => setDeleting(rule)}
                >
                  <IconTrash size={16} />
                </ActionIcon>
              </Group>
            </Group>
          )
        })
      )}

      <ModalFooter>
        <Button leftSection={<IconPlus size={16} />} onClick={onAdd}>
          <Trans>Add rule</Trans>
        </Button>
      </ModalFooter>

      <ConfirmDialog
        opened={applying != null}
        onClose={() => setApplying(null)}
        title={<Trans>Apply to existing series</Trans>}
        description={
          preview.isLoading ? (
            <Trans>Counting the series this rule matches…</Trans>
          ) : (
            <Plural
              value={previewCount}
              _0={`No series in the library match this rule without already carrying ${applyingTag}.`}
              one={`# series matches this rule and will be tagged ${applyingTag}.`}
              other={`# series match this rule and will be tagged ${applyingTag}.`}
            />
          )
        }
        confirmLabel={<Trans>Tag them</Trans>}
        confirmDisabled={preview.isLoading || previewCount === 0}
        loading={apply.isPending}
        onConfirm={() => {
          if (!applying) return
          apply.mutate(applying.id, {
            onSuccess: ({ tagged }) => {
              setApplying(null)
              notifications.show({
                message: plural(tagged, { one: 'Tagged # series', other: 'Tagged # series' }),
                color: 'var(--ok)',
              })
            },
            onError: () => notifications.show({ message: now`Couldn't apply the rule`, color: 'var(--danger)' }),
          })
        }}
      />
      <ConfirmDialog
        opened={deleting != null}
        onClose={() => setDeleting(null)}
        title={<Trans>Delete rule</Trans>}
        description={<Trans>Series it already tagged keep the tag.</Trans>}
        confirmLabel={<Trans>Delete</Trans>}
        loading={remove.isPending}
        onConfirm={() => {
          if (!deleting) return
          remove.mutate(deleting.id, { onSuccess: () => setDeleting(null) })
        }}
      />
    </Stack>
  )
}

function RuleEditor({ id, initial, onDone }: { id: number | null; initial: SaveAutoTagRule; onDone: () => void }) {
  const { t } = useLingui()
  const label = useLabel()
  const { data: tags } = useTags()
  const { choices, valueLabel } = useFieldChoices()
  const save = useSaveAutoTagRule()
  const pages = useDrillPages<EditorPage>('main', EDITOR_PARENTS)
  const [draft, setDraft] = useState(initial)

  const valueOf = (field: AutoTagField) => draft.conditions.find((c) => c.field === field)?.value ?? ''
  const setField = (field: AutoTagField, value: string) =>
    setDraft((d) => ({
      ...d,
      conditions: [...d.conditions.filter((c) => c.field !== field), ...(value ? [{ field, value }] : [])],
    }))

  const tag = tags?.find((x) => x.id === draft.tagId)
  const ready = draft.tagId > 0 && draft.conditions.length > 0

  const fieldOptions = (field: AutoTagField): DrillChoice[] => {
    const current = valueOf(field)
    const options = choices[field]
    const known = current === '' || options.some((o) => o.value === current)
    return [
      { value: '', label: t`Any` },
      ...options,
      ...(known ? [] : [{ value: current, label: valueLabel(field, current) }]),
    ]
  }

  const submit = () =>
    save.mutate(
      { id, rule: draft },
      {
        onSuccess: onDone,
        onError: () => notifications.show({ message: now`Couldn't save the rule`, color: 'var(--danger)' }),
      },
    )

  return (
    <Stack gap="sm">
      <div className="auto-tag-rule-pages">
        <DrillPanes page={pages.page} depth={pages.depth} bleed={6}>
          {pages.page === 'main' && (
            <>
              <DrillRow
                ref={pages.rowRef('tag')}
                icon={IconTag}
                label={<Trans>Tag to add</Trans>}
                value={tag?.label ?? t`Choose`}
                onClick={() => pages.open('tag')}
              />
              <Text size="xs" c="var(--ink-3)" px={10} pt={10} pb={4}>
                <Trans>When a series is added and matches all of these</Trans>
              </Text>
              {AUTO_TAG_FIELDS.map((field) => {
                const value = valueOf(field)
                return (
                  <DrillRow
                    key={field}
                    ref={pages.rowRef(field)}
                    icon={FIELD_ICONS[field]}
                    label={label(AUTO_TAG_FIELD_LABELS[field])}
                    value={value ? valueLabel(field, value) : t`Any`}
                    onClick={() => pages.open(field)}
                  />
                )
              })}
            </>
          )}
          {pages.page === 'tag' && (
            <>
              <DrillBack ref={pages.backRef} title={<Trans>Tag to add</Trans>} onBack={pages.back} />
              <div className="drill-scroll">
                {(tags ?? []).length === 0 ? (
                  <Text size="sm" c="var(--ink-3)" px={10} py={8}>
                    <Trans>Create a tag first, from Manage tags in the library.</Trans>
                  </Text>
                ) : (
                  <DrillChoices
                    label={t`Tag to add`}
                    value={String(draft.tagId)}
                    options={(tags ?? []).map((x) => ({ value: String(x.id), label: x.label }))}
                    onPick={(value) => {
                      setDraft((d) => ({ ...d, tagId: Number(value) }))
                      pages.back()
                    }}
                  />
                )}
              </div>
            </>
          )}
          {AUTO_TAG_FIELDS.map(
            (field) =>
              pages.page === field && (
                <div key={field}>
                  <DrillBack ref={pages.backRef} title={label(AUTO_TAG_FIELD_LABELS[field])} onBack={pages.back} />
                  <div className="drill-scroll">
                    <DrillChoices
                      label={label(AUTO_TAG_FIELD_LABELS[field])}
                      value={valueOf(field)}
                      options={fieldOptions(field)}
                      onPick={(value) => {
                        setField(field, value)
                        pages.back()
                      }}
                    />
                  </div>
                </div>
              ),
          )}
        </DrillPanes>
      </div>
      <ModalFooter
        status={
          !ready && (
            <Text size="xs" c="var(--ink-3)">
              {draft.tagId > 0 ? <Trans>Pick at least one condition.</Trans> : <Trans>Pick a tag to add.</Trans>}
            </Text>
          )
        }
      >
        <Button variant="default" onClick={onDone}>
          <Trans>Cancel</Trans>
        </Button>
        <Button onClick={submit} disabled={!ready} loading={save.isPending}>
          <Trans>Save rule</Trans>
        </Button>
      </ModalFooter>
    </Stack>
  )
}
