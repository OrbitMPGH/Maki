import { useState, type CSSProperties } from 'react'
import { ActionIcon, Button, Group, Modal, Stack, Switch, Text, Tooltip } from '@mantine/core'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { plural, t as now } from '@lingui/core/macro'
import { IconPencil, IconPlus, IconTag, IconTrash, IconWand } from '../../lib/icons'
import { notifications } from '../../lib/toast'
import {
  AUTO_TAG_FIELDS,
  useApplyAutoTagRule,
  useAutoTagRulePreview,
  useAutoTagRules,
  useDeleteAutoTagRule,
  useSaveAutoTagRule,
  type AutoTagRule,
  type SaveAutoTagRule,
} from '../../api/autoTagRules'
import { useTags } from '../../api/hooks'
import { ConfirmDialog } from '../ui/ConfirmDialog'
import { DrillPanes } from '../ui/DrillPanes'
import { DrillBack, DrillChoices, DrillRow, useDrillPages } from '../ui/DrillPanel'
import { EmptyState } from '../ui/EmptyState'
import { ModalFooter, ModalHeading } from '../ui/ModalShell'
import { ConditionFieldPage, ConditionGroupRows } from './RuleConditions'
import { conditionParents, groupsOf, savedGroups, useGroupsSummary } from './conditionGroups'
import { MODAL_SIZE } from '../ui/modalSizes'

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
      size={MODAL_SIZE.narrow}
      title={
        <ModalHeading
          title={
            editing == null ? t`Auto-tag rules` : editing.id == null ? t`New rule` : t`Edit rule`
          }
          description={
            editing == null ? (
              <Trans>Tag a series when it is added and matches every condition of a rule, or of one of its alternatives.</Trans>
            ) : undefined
          }
        />
      }
    >
      {editing == null ? (
        <RuleList
          onAdd={() => setEditing({ id: null, draft: { tagId: 0, enabled: true, groups: [[]] } })}
          onEdit={(rule) =>
            setEditing({ id: rule.id, draft: { tagId: rule.tagId, enabled: rule.enabled, groups: groupsOf(rule) } })
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
  const { data: rules, isLoading } = useAutoTagRules()
  const { data: tags } = useTags()
  const summary = useGroupsSummary()
  const save = useSaveAutoTagRule()
  const remove = useDeleteAutoTagRule()
  const apply = useApplyAutoTagRule()
  const [applying, setApplying] = useState<AutoTagRule | null>(null)
  const [deleting, setDeleting] = useState<AutoTagRule | null>(null)
  const preview = useAutoTagRulePreview(applying?.id ?? null)
  const previewCount = preview.data?.count ?? 0

  const tagOf = (rule: AutoTagRule) => tags?.find((x) => x.id === rule.tagId)
  const applyingTag = applying ? (tagOf(applying)?.label ?? '') : ''

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
                    rule: { tagId: rule.tagId, enabled: e.currentTarget.checked, groups: groupsOf(rule) },
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
                  {summary(groupsOf(rule))}
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
  const { data: tags } = useTags()
  const save = useSaveAutoTagRule()
  const [draft, setDraft] = useState(initial)
  const pages = useDrillPages<string>('main', {
    main: 'main',
    tag: 'main',
    ...conditionParents('main', Math.max(draft.groups.length, 1), AUTO_TAG_FIELDS),
  })

  const tag = tags?.find((x) => x.id === draft.tagId)
  const groups = savedGroups(draft.groups)
  const ready = draft.tagId > 0 && groups.length > 0
  const setGroups = (next: SaveAutoTagRule['groups']) => setDraft((d) => ({ ...d, groups: next }))

  const submit = () =>
    save.mutate(
      { id, rule: { ...draft, groups } },
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
              <ConditionGroupRows
                groups={draft.groups}
                fields={AUTO_TAG_FIELDS}
                heading={<Trans>When a series is added and matches all of these</Trans>}
                pages={pages}
                onChange={setGroups}
              />
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
          {pages.page.includes(':') && (
            <ConditionFieldPage
              page={pages.page}
              groups={draft.groups}
              fields={AUTO_TAG_FIELDS}
              pages={pages}
              onChange={setGroups}
            />
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
