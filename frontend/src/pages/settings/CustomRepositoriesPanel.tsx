import { useEffect, useState } from 'react'
import { Alert, Anchor, Button, Card, Group, List, Modal, Stack, Switch, Table, Text, TextInput, Tooltip } from '@mantine/core'
import { IconAlertTriangle, IconRefresh, IconTrash } from '@tabler/icons-react'
import { Trans, useLingui } from '@lingui/react/macro'
import { errorText } from '../../api/errorText'
import { languageName } from '../../api/titles'
import { formatDateTime } from '../../format'
import { ConfirmDialog } from '../../components/ui/ConfirmDialog'
import {
  failedStepsOf, identityChangeOf, useAddRepository, useApplyCustomSourceUpdate, useCustomRepositories,
  useCustomSourceUpdatePreview, useInstallFromRepository, useRefreshRepositories, useRemoveRepository,
  useSetRepositoryAutoUpdate, type CustomRepository, type CustomRepositoryEntry, type IdentityReason,
} from '../../api/customSources'
import { MetaList } from '../../components/ui/MetaList'

function useReasonLabels(): Record<IdentityReason, string> {
  const { t } = useLingui()
  return {
    language: t`The language changes.`,
    host: t`The site's address changes.`,
    chapters: t`The sample series lists different chapter numbers or ids.`,
    imageHosts: t`Page images come from different hosts.`,
    unverified: t`Maki could not list the sample series under both versions to compare them.`,
  }
}

/** The identity check's findings and a button that goes ahead anyway, shared by install, update and revert. */
function IdentityConfirm({ error, onConfirm, loading }: { error: unknown; onConfirm: () => void; loading: boolean }) {
  const identity = identityChangeOf(error)
  const reasons = useReasonLabels()
  if (!identity) return null
  return (
    <Alert color="yellow" variant="light" icon={<IconAlertTriangle size={16} />} title={identity.error}>
      <List size="sm" spacing={2}>
        {identity.reasons.map((r) => <List.Item key={r}>{reasons[r] ?? r}</List.Item>)}
      </List>
      <Group justify="flex-end" mt="sm">
        <Button size="xs" color="var(--warn)" loading={loading} onClick={onConfirm}>
          <Trans>Go ahead anyway</Trans>
        </Button>
      </Group>
    </Alert>
  )
}

/**
 * What a waiting update changes, top-level field by field, and Apply. The identity check still
 * stops an update that could re-key linked series until it is confirmed.
 */
export function CustomSourceUpdateDialog({ id, opened, onClose, onApplied }: {
  id: string | null
  opened: boolean
  onClose: () => void
  onApplied?: () => void
}) {
  const { t } = useLingui()
  const preview = useCustomSourceUpdatePreview(opened ? id : null)
  const apply = useApplyCustomSourceUpdate()
  const identity = identityChangeOf(apply.error)

  // Another source's refusal must never carry over: its "go ahead anyway" would confirm this one.
  useEffect(() => {
    apply.reset()
    // Reset only when the dialog opens on a source, not on every mutation identity change.
  }, [opened, id])

  function run(confirm: boolean) {
    if (!id) return
    apply.mutate({ id, confirm }, {
      onSuccess: () => {
        onApplied?.()
        onClose()
      },
    })
  }

  return (
    <Modal opened={opened} onClose={onClose} size={640} title={<Text fw={600}><Trans>Update {id}</Trans></Text>} closeButtonProps={{ 'aria-label': t`Close` }}>
      <Stack gap="sm">
        {preview.error && <Alert color="red" variant="light">{errorText(preview.error)}</Alert>}
        {preview.data && (
          <>
            <Text size="sm">
              <Trans>Version {preview.data.fromVersion} to {preview.data.toVersion}. These parts of the definition change:</Trans>
            </Text>
            <Table fz="xs" withRowBorders={false} striped>
              <Table.Tbody>
                {preview.data.changedFields.map((field) => (
                  <Table.Tr key={field}>
                    <Table.Td style={{ fontFamily: 'var(--mantine-font-family-monospace)', verticalAlign: 'top' }}>{field}</Table.Td>
                    <Table.Td>
                      <pre style={{ margin: 0, whiteSpace: 'pre-wrap', fontSize: 11, color: 'var(--ink-3)' }}>
                        {JSON.stringify(preview.data!.current[field] ?? null)}
                      </pre>
                      <pre style={{ margin: 0, whiteSpace: 'pre-wrap', fontSize: 11 }}>
                        {JSON.stringify(preview.data!.next[field] ?? null)}
                      </pre>
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </>
        )}
        {apply.error && !identity && <Alert color="red" variant="light">{errorText(apply.error)}</Alert>}
        <IdentityConfirm error={apply.error} onConfirm={() => run(true)} loading={apply.isPending} />
        <Group justify="flex-end" gap="xs">
          <Button variant="default" onClick={onClose}><Trans>Cancel</Trans></Button>
          <Button onClick={() => run(false)} loading={apply.isPending && !identity} disabled={!preview.data || identity !== null}>
            <Trans>Apply update</Trans>
          </Button>
        </Group>
      </Stack>
    </Modal>
  )
}

function EntryAction({ repository, entry, onUpdate }: {
  repository: CustomRepository
  entry: CustomRepositoryEntry
  onUpdate: (id: string) => void
}) {
  const install = useInstallFromRepository()
  const steps = failedStepsOf(install.error)
  const identity = identityChangeOf(install.error)
  const run = (confirm: boolean) => install.mutate({ url: repository.url, id: entry.id, confirm })

  let action
  switch (entry.state) {
    case 'available':
      action = <Button size="compact-xs" variant="default" loading={install.isPending} onClick={() => run(false)}><Trans>Install</Trans></Button>
      break
    case 'installed':
      action = <Text size="xs" c="var(--ink-3)"><Trans>Installed, version {entry.installedVersion}</Trans></Text>
      break
    case 'updateAvailable':
      action = (
        <Button size="compact-xs" variant="light" onClick={() => onUpdate(entry.id)}>
          <Trans>Update to version {entry.version}</Trans>
        </Button>
      )
      break
    case 'detached':
      action = (
        <Group gap={6} wrap="nowrap">
          <Text size="xs" c="var(--ink-3)"><Trans>Edited locally</Trans></Text>
          <Button size="compact-xs" variant="default" loading={install.isPending} onClick={() => run(false)}>
            <Trans>Replace with repository version</Trans>
          </Button>
        </Group>
      )
      break
    case 'idTaken':
      action = <Text size="xs" c="var(--warn)"><Trans>Another custom source already has this id</Trans></Text>
      break
    default:
      action = <Text size="xs" c="var(--ink-3)"><Trans>Needs a newer Maki</Trans></Text>
  }

  return (
    <Stack gap={4} align="flex-end">
      {action}
      {install.error && !identity && (
        <Text size="xs" c="var(--danger)" ta="right">
          {errorText(install.error)}
          {steps?.filter((s) => s.status === 'failed').map((s) => ` ${s.name}: ${s.error ?? ''}`).join(';')}
        </Text>
      )}
      <IdentityConfirm error={install.error} onConfirm={() => run(true)} loading={install.isPending} />
    </Stack>
  )
}

function RepositoryCard({ repository, onUpdate }: { repository: CustomRepository; onUpdate: (id: string) => void }) {
  const { t } = useLingui()
  const autoUpdate = useSetRepositoryAutoUpdate()
  const refresh = useRefreshRepositories()
  const remove = useRemoveRepository()
  const [removing, setRemoving] = useState(false)
  const installed = repository.definitions.filter((d) => d.installedVersion !== null)

  return (
    <Card withBorder padding="sm">
      <Stack gap="xs">
        <Group justify="space-between" wrap="nowrap" align="flex-start">
          <div style={{ minWidth: 0 }}>
            <Text fw={600}>{repository.name}</Text>
            <Text size="xs" c="var(--ink-3)" style={{ wordBreak: 'break-all', fontFamily: 'var(--mantine-font-family-monospace)' }}>
              {repository.url}
            </Text>
          </div>
          <Group gap={6} wrap="nowrap">
            <Button size="compact-xs" variant="default" leftSection={<IconRefresh size={12} />} loading={refresh.isPending}
              onClick={() => refresh.mutate(repository.url)}>
              <Trans>Check now</Trans>
            </Button>
            <Button size="compact-xs" variant="subtle" color="var(--danger)" leftSection={<IconTrash size={12} />} onClick={() => setRemoving(true)}>
              <Trans>Remove</Trans>
            </Button>
          </Group>
        </Group>
        <Tooltip
          label={t`Only a repository served over https can keep its sources up to date on its own.`}
          disabled={repository.secure}
          withArrow
          multiline
          w={260}
        >
          <div style={{ width: 'fit-content' }}>
            <Switch
              size="sm"
              label={t`Keep up to date`}
              description={t`Apply updates that pass their test and don't change chapter identity. Anything else waits for you.`}
              checked={repository.autoUpdate}
              disabled={!repository.secure}
              onChange={(e) => autoUpdate.mutate({ url: repository.url, autoUpdate: e.currentTarget.checked })}
            />
          </div>
        </Tooltip>
        <Text size="xs" c="var(--ink-3)">
          {repository.lastCheckedUtc
            ? <Trans>Last checked {formatDateTime(repository.lastCheckedUtc)}</Trans>
            : <Trans>Not checked yet</Trans>}
        </Text>
        {repository.lastError && <Text size="xs" c="var(--warn)">{repository.lastError}</Text>}
        {(autoUpdate.error || refresh.error || remove.error) && (
          <Text size="xs" c="var(--danger)">{errorText(autoUpdate.error ?? refresh.error ?? remove.error)}</Text>
        )}
        {repository.definitions.length === 0 ? (
          <Text size="sm" c="var(--ink-3)"><Trans>This repository lists no definitions.</Trans></Text>
        ) : (
          <Table fz="sm" verticalSpacing={6} withRowBorders>
            <Table.Tbody>
              {repository.definitions.map((entry) => (
                <Table.Tr key={entry.id}>
                  <Table.Td>
                    <Text size="sm" fw={600}>{entry.name}</Text>
                    <Text component="div" size="xs" c="var(--ink-3)">
                      <MetaList items={[languageName(entry.language) ?? entry.language, <Trans key="version">version {entry.version}</Trans>]} />
                    </Text>
                  </Table.Td>
                  <Table.Td ta="right">
                    <EntryAction repository={repository} entry={entry} onUpdate={onUpdate} />
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}
      </Stack>
      <ConfirmDialog
        opened={removing}
        onClose={() => setRemoving(false)}
        title={<Trans>Remove {repository.name}?</Trans>}
        confirmLabel={<Trans>Remove</Trans>}
        loading={remove.isPending}
        onConfirm={() => remove.mutate(repository.url, { onSuccess: () => setRemoving(false) })}
      >
        {installed.length === 0 ? (
          <Trans>Nothing was installed from it.</Trans>
        ) : (
          <Stack gap={6}>
            <Text size="sm">
              <Trans>These keep working as hand-made custom sources, and no longer get updates:</Trans>
            </Text>
            <List size="sm">{installed.map((d) => <List.Item key={d.id}>{d.name}</List.Item>)}</List>
          </Stack>
        )}
      </ConfirmDialog>
    </Card>
  )
}

/**
 * Repositories of custom source definitions, stacked over Manage sources like the wizard. Adding
 * one fetches its index; installing tests a definition against its own sample before saving it.
 */
export function CustomRepositoriesPanel({ opened, onClose }: { opened: boolean; onClose: () => void }) {
  const { t } = useLingui()
  const { data: repositories, error } = useCustomRepositories(opened)
  const add = useAddRepository()
  const [url, setUrl] = useState('')
  const [updating, setUpdating] = useState<string | null>(null)

  return (
    <Modal opened={opened} onClose={onClose} size={760} title={<Text fw={600}><Trans>Repositories</Trans></Text>} closeButtonProps={{ 'aria-label': t`Close` }}>
      <Stack gap="md">
        <Text size="sm" c="var(--ink-3)">
          <Trans>
            A repository is a list of custom source definitions someone publishes. Maki installs from it, and can
            keep what it installed up to date. Only add repositories you trust: a definition decides which site Maki reads.
          </Trans>
        </Text>
        <Group align="flex-end" gap="xs" wrap="nowrap">
          <TextInput
            style={{ flex: 1 }}
            label={t`Repository address`}
            placeholder="https://example.com/maki-sources/index.json"
            value={url}
            onChange={(e) => setUrl(e.currentTarget.value)}
          />
          <Button onClick={() => add.mutate(url.trim(), { onSuccess: () => setUrl('') })} loading={add.isPending} disabled={!url.trim()}>
            <Trans>Add</Trans>
          </Button>
        </Group>
        {add.error && <Alert color="red" variant="light">{errorText(add.error)}</Alert>}
        {error && <Alert color="red" variant="light">{errorText(error)}</Alert>}
        {repositories?.length === 0 && (
          <Text size="sm" c="var(--ink-3)"><Trans>No repositories yet.</Trans></Text>
        )}
        {repositories?.map((repository) => (
          <RepositoryCard key={repository.url} repository={repository} onUpdate={setUpdating} />
        ))}
        <Group justify="flex-end">
          <Anchor component="button" type="button" size="sm" onClick={onClose}><Trans>Done</Trans></Anchor>
        </Group>
      </Stack>
      <CustomSourceUpdateDialog id={updating} opened={updating !== null} onClose={() => setUpdating(null)} />
    </Modal>
  )
}
