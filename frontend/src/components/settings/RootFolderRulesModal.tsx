import { SearchInput } from '../ui/SearchInput'
import { useMemo, useState } from 'react'
import { ActionIcon, Badge, Button, Group, Modal, Stack, Switch, Text, Tooltip } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { t as now } from '@lingui/core/macro'
import {
  IconArrowDown,
  IconArrowUp,
  IconFolder,
  IconFolderSearch,
  IconPencil,
  IconPlus,
  
  IconTrash,
} from '../../lib/icons'
import { notifications } from '../../lib/toast'
import { useRootFolders, useSeries } from '../../api/hooks'
import {
  ROOT_FOLDER_RULE_FIELDS,
  useDeleteRootFolderRule,
  useReorderRootFolderRules,
  useRootFolderRuleTest,
  useRootFolderRules,
  useSaveRootFolderRule,
  type RootFolderRule,
  type SaveRootFolderRule,
} from '../../api/rootFolderRules'
import { ConfirmDialog } from '../ui/ConfirmDialog'
import { DrillPanes } from '../ui/DrillPanes'
import { DrillBack, DrillChoices, DrillRow, useDrillPages } from '../ui/DrillPanel'
import { EmptyState } from '../ui/EmptyState'
import { ModalFooter, ModalHeading } from '../ui/ModalShell'
import { ConditionFieldPage, ConditionGroupRows } from './RuleConditions'
import { conditionParents, savedGroups, useGroupsSummary } from './conditionGroups'

type View = { kind: 'list' } | { kind: 'edit'; id: number | null; draft: SaveRootFolderRule } | { kind: 'check' }

/** Root folder rules: an ordered list, an editor for one rule, and a check of where a library series would go. */
export function RootFolderRulesModal({ opened, onClose }: { opened: boolean; onClose: () => void }) {
  const { t } = useLingui()
  const [view, setView] = useState<View>({ kind: 'list' })

  const close = () => {
    setView({ kind: 'list' })
    onClose()
  }

  const title =
    view.kind === 'list'
      ? t`Root folder rules`
      : view.kind === 'check'
        ? t`Check a series`
        : view.id == null
          ? t`New rule`
          : t`Edit rule`

  return (
    <Modal
      opened={opened}
      onClose={close}
      size="md"
      title={
        <ModalHeading
          title={title}
          description={
            view.kind === 'list' ? (
              <Trans>
                A series added without a chosen folder goes to the first rule it matches. With no match it goes to the
                first root folder, as before.
              </Trans>
            ) : undefined
          }
        />
      }
    >
      {view.kind === 'list' && (
        <RuleList
          onAdd={(rootFolderId) =>
            setView({ kind: 'edit', id: null, draft: { rootFolderId, enabled: true, groups: [[]] } })
          }
          onEdit={(rule) =>
            setView({
              kind: 'edit',
              id: rule.id,
              draft: { rootFolderId: rule.rootFolderId, enabled: rule.enabled, groups: rule.groups },
            })
          }
          onCheck={() => setView({ kind: 'check' })}
        />
      )}
      {view.kind === 'edit' && (
        <RuleEditor
          key={view.id ?? 'new'}
          id={view.id}
          initial={view.draft}
          onDone={() => setView({ kind: 'list' })}
        />
      )}
      {view.kind === 'check' && <SeriesCheck onDone={() => setView({ kind: 'list' })} />}
    </Modal>
  )
}

function RuleList({
  onAdd,
  onEdit,
  onCheck,
}: {
  onAdd: (rootFolderId: number) => void
  onEdit: (rule: RootFolderRule) => void
  onCheck: () => void
}) {
  const { t } = useLingui()
  const { data: rules, isLoading } = useRootFolderRules()
  const { data: folders } = useRootFolders()
  const summary = useGroupsSummary()
  const save = useSaveRootFolderRule()
  const remove = useDeleteRootFolderRule()
  const reorder = useReorderRootFolderRules()
  const [deleting, setDeleting] = useState<RootFolderRule | null>(null)
  const list = rules ?? []

  const pathOf = (id: number) => folders?.find((f) => f.id === id)?.path ?? t`Unknown folder`
  const move = (index: number, by: -1 | 1) => {
    const ids = list.map((r) => r.id)
    const [moved] = ids.splice(index, 1)
    ids.splice(index + by, 0, moved)
    reorder.mutate(ids)
  }

  return (
    <Stack gap="sm">
      {!isLoading && list.length === 0 ? (
        <EmptyState
          compact
          title={t`No rules yet`}
          description={t`For example, send every manhwa and manhua to a webtoon folder.`}
        />
      ) : (
        list.map((rule, index) => {
          const position = index + 1
          return (
            <Group key={rule.id} gap="sm" wrap="nowrap" align="flex-start">
              <Switch
                mt={2}
                checked={rule.enabled}
                aria-label={t`Rule is on`}
                onChange={(e) =>
                  save.mutate({
                    id: rule.id,
                    rule: { rootFolderId: rule.rootFolderId, enabled: e.currentTarget.checked, groups: rule.groups },
                  })
                }
              />
              <Stack gap={2} style={{ flex: 1, minWidth: 0 }}>
                <Group gap={6} wrap="nowrap">
                  <Badge variant="light" size="sm" circle aria-label={t`Rule ${position}`}>
                    {position}
                  </Badge>
                  <IconFolder size={14} aria-hidden />
                  <Text size="sm" fw={600} ff="monospace" truncate>
                    {pathOf(rule.rootFolderId)}
                  </Text>
                </Group>
                <Text size="xs" c="var(--ink-3)">
                  {summary(rule.groups)}
                </Text>
              </Stack>
              <Group gap={2} wrap="nowrap">
                <ActionIcon
                  variant="subtle"
                  aria-label={t`Move up`}
                  disabled={index === 0 || reorder.isPending}
                  onClick={() => move(index, -1)}
                >
                  <IconArrowUp size={16} />
                </ActionIcon>
                <ActionIcon
                  variant="subtle"
                  aria-label={t`Move down`}
                  disabled={index === list.length - 1 || reorder.isPending}
                  onClick={() => move(index, 1)}
                >
                  <IconArrowDown size={16} />
                </ActionIcon>
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
        {list.length > 0 && (
          <Button variant="default" leftSection={<IconFolderSearch size={16} />} onClick={onCheck}>
            <Trans>Check a series</Trans>
          </Button>
        )}
        <Tooltip label={t`Add a root folder first`} disabled={(folders ?? []).length > 0} withArrow>
          <Button
            leftSection={<IconPlus size={16} />}
            disabled={(folders ?? []).length === 0}
            onClick={() => folders?.[0] && onAdd(folders[0].id)}
          >
            <Trans>Add rule</Trans>
          </Button>
        </Tooltip>
      </ModalFooter>

      <ConfirmDialog
        opened={deleting != null}
        onClose={() => setDeleting(null)}
        title={<Trans>Delete rule</Trans>}
        description={<Trans>Series it already placed stay where they are.</Trans>}
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

function RuleEditor({
  id,
  initial,
  onDone,
}: {
  id: number | null
  initial: SaveRootFolderRule
  onDone: () => void
}) {
  const { t } = useLingui()
  const { data: folders } = useRootFolders()
  const save = useSaveRootFolderRule()
  const [draft, setDraft] = useState(initial)
  const pages = useDrillPages<string>('main', {
    main: 'main',
    folder: 'main',
    ...conditionParents('main', Math.max(draft.groups.length, 1), ROOT_FOLDER_RULE_FIELDS),
  })

  const folder = folders?.find((f) => f.id === draft.rootFolderId)
  const groups = savedGroups(draft.groups)
  const ready = folder != null && groups.length > 0
  const setGroups = (next: SaveRootFolderRule['groups']) => setDraft((d) => ({ ...d, groups: next }))

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
                ref={pages.rowRef('folder')}
                icon={IconFolder}
                label={<Trans>Put it in</Trans>}
                value={folder?.path ?? t`Choose`}
                onClick={() => pages.open('folder')}
              />
              <ConditionGroupRows
                groups={draft.groups}
                fields={ROOT_FOLDER_RULE_FIELDS}
                heading={<Trans>When a series is added and matches all of these</Trans>}
                pages={pages}
                onChange={setGroups}
              />
            </>
          )}
          {pages.page === 'folder' && (
            <>
              <DrillBack ref={pages.backRef} title={<Trans>Put it in</Trans>} onBack={pages.back} />
              <div className="drill-scroll">
                <DrillChoices
                  label={t`Put it in`}
                  value={String(draft.rootFolderId)}
                  options={(folders ?? []).map((f) => ({ value: String(f.id), label: f.path }))}
                  onPick={(value) => {
                    setDraft((d) => ({ ...d, rootFolderId: Number(value) }))
                    pages.back()
                  }}
                />
              </div>
            </>
          )}
          {pages.page.includes(':') && (
            <ConditionFieldPage
              page={pages.page}
              groups={draft.groups}
              fields={ROOT_FOLDER_RULE_FIELDS}
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
              {folder ? <Trans>Pick at least one condition.</Trans> : <Trans>Pick a root folder.</Trans>}
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

/** Pick a library series and see which rule would send it where, were it added again. */
function SeriesCheck({ onDone }: { onDone: () => void }) {
  const { t } = useLingui()
  const { data: library } = useSeries()
  const { data: folders } = useRootFolders()
  const { data: rules } = useRootFolderRules()
  const [query, setQuery] = useState('')
  const [seriesId, setSeriesId] = useState<number | null>(null)
  const result = useRootFolderRuleTest(seriesId)

  const matches = useMemo(() => {
    const q = query.trim().toLocaleLowerCase()
    const all = library ?? []
    return (q ? all.filter((s) => s.displayTitle.toLocaleLowerCase().includes(q)) : all).slice(0, 30)
  }, [library, query])

  const picked = library?.find((s) => s.id === seriesId)
  const position = result.data?.ruleId != null ? (rules ?? []).findIndex((r) => r.id === result.data?.ruleId) + 1 : 0
  const path = folders?.find((f) => f.id === result.data?.rootFolderId)?.path ?? ''
  const fallback = folders?.[0]?.path ?? ''
  const title = picked?.displayTitle ?? ''

  return (
    <Stack gap="sm">
      <SearchInput
        placeholder={t`Search the library`}
        aria-label={t`Search the library`}
        value={query}
        onClear={() => setQuery('')}
        onChange={(e) => setQuery(e.currentTarget.value)}
      />
      <div className="auto-tag-rule-pages drill-scroll" style={{ maxHeight: 280 }}>
        <DrillChoices
          label={t`Series`}
          value={seriesId == null ? '' : String(seriesId)}
          options={matches.map((s) => ({ value: String(s.id), label: s.displayTitle }))}
          onPick={(value) => setSeriesId(Number(value))}
        />
      </div>
      {picked && (
        <Text size="sm" aria-live="polite">
          {result.isLoading ? (
            <Trans>Checking the rules…</Trans>
          ) : result.data?.ruleId != null ? (
            <Trans>
              Rule {position} would put {title} in <Text span ff="monospace">{path}</Text>.
            </Trans>
          ) : (
            <Trans>
              No rule matches {title}, so it would go to <Text span ff="monospace">{fallback}</Text>.
            </Trans>
          )}
        </Text>
      )}
      <ModalFooter>
        <Button variant="default" onClick={onDone}>
          <Trans>Back to rules</Trans>
        </Button>
      </ModalFooter>
    </Stack>
  )
}
