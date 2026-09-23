import { useMemo, useState } from 'react'
import {
  ActionIcon,
  Badge,
  Button,
  Group,
  Modal,
  PasswordInput,
  Select,
  Stack,
  Switch,
  Table,
  Text,
  TextInput,
  Title,
} from '@mantine/core'
import { IconBellPlus, IconPencil, IconTrash } from '@tabler/icons-react'
import { notifications as toast } from '@mantine/notifications'
import {
  useCreateNotification,
  useDeleteNotification,
  useNotifications,
  useTestNotification,
  useUpdateNotification,
} from '../api/hooks'
import type { NotificationDto, NotificationRequest, NotificationType } from '../api/types'
import { Trans, useLingui } from '@lingui/react/macro'
import { msg, t as now } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { Panel } from './ui/Panel'
import { useLabel } from '../i18n-context'

const EVENT_FIELDS: { key: keyof NotificationRequest['events']; label: MessageDescriptor; description: MessageDescriptor }[] = [
  { key: 'chapterDownloaded', label: msg`Chapter downloaded`, description: msg`A chapter finished downloading and imported.` },
  { key: 'downloadFailed', label: msg`Download failed`, description: msg`A chapter download failed.` },
  { key: 'newChapterAvailable', label: msg`New chapter available`, description: msg`A refresh found new chapters for a series.` },
  { key: 'importCompleted', label: msg`Import completed`, description: msg`A library import folder finished.` },
  { key: 'healthIssue', label: msg`Health issue`, description: msg`A new system health problem was detected.` },
  { key: 'updateAvailable', label: msg`Update available`, description: msg`A newer Maki release was published.` },
]

/**
 * "Discord" is a product name and is never translated; "Webhook" is a generic connector kind and
 * is. Kept as descriptors rather than plain strings because the module evaluates once and would
 * freeze whatever language was active then.
 */
const NOTIFICATION_TYPE_LABELS: Record<NotificationType, MessageDescriptor | string> = {
  Discord: 'Discord',
  Webhook: msg`Webhook`,
}

function useNotificationTypeOptions() {
  const renderLabel = useLabel()
  const { i18n } = useLingui()
  return useMemo(
    () =>
      (['Discord', 'Webhook'] as NotificationType[]).map((value) => ({
        value,
        label: renderLabel(NOTIFICATION_TYPE_LABELS[value]),
      })),
    [renderLabel, i18n.locale],
  )
}

const EMPTY: NotificationRequest = {
  name: '',
  type: 'Discord',
  enabled: true,
  config: { webhookUrl: null, url: null, bearerToken: null },
  events: {
    chapterDownloaded: true,
    downloadFailed: true,
    newChapterAvailable: false,
    importCompleted: false,
    healthIssue: false,
    updateAvailable: false,
  },
}

function toRequest(n: NotificationDto): NotificationRequest {
  return { name: n.name, type: n.type, enabled: n.enabled, config: n.config, events: n.events }
}

export function NotificationsSection() {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const typeOptions = useNotificationTypeOptions()
  const { data: connections } = useNotifications()
  const create = useCreateNotification()
  const update = useUpdateNotification()
  const remove = useDeleteNotification()
  const test = useTestNotification()

  const [editing, setEditing] = useState<{ id: number | null; form: NotificationRequest } | null>(null)

  const openNew = () => setEditing({ id: null, form: { ...EMPTY, config: { ...EMPTY.config }, events: { ...EMPTY.events } } })
  const openEdit = (n: NotificationDto) => setEditing({ id: n.id, form: toRequest(n) })
  const close = () => setEditing(null)

  const setForm = (patch: Partial<NotificationRequest>) =>
    setEditing((e) => (e ? { ...e, form: { ...e.form, ...patch } } : e))
  const setConfig = (patch: Partial<NotificationRequest['config']>) =>
    setEditing((e) => (e ? { ...e, form: { ...e.form, config: { ...e.form.config, ...patch } } } : e))
  const setEvent = (key: keyof NotificationRequest['events'], value: boolean) =>
    setEditing((e) => (e ? { ...e, form: { ...e.form, events: { ...e.form.events, [key]: value } } } : e))

  const save = () => {
    if (!editing) return
    const onSuccess = () => {
      toast.show({ message: now`Saved`, color: 'green' })
      close()
    }
    const onError = (err: Error) => toast.show({ title: now`Save failed`, message: err.message, color: 'red' })
    if (editing.id === null) create.mutate(editing.form, { onSuccess, onError })
    else update.mutate({ id: editing.id, value: editing.form }, { onSuccess, onError })
  }

  const runTest = () => {
    if (!editing) return
    test.mutate(editing.form, {
      onSuccess: (r) =>
        toast.show({
          message: r.success ? now`Test notification sent` : now`Test failed`,
          color: r.success ? 'green' : 'red',
        }),
      onError: (err: Error) => toast.show({ title: now`Test failed`, message: err.message, color: 'red' }),
    })
  }

  const form = editing?.form

  return (
    <Panel>
      <Group justify="space-between" mb="sm">
        <Title order={4}><Trans>Notifications</Trans></Title>
        <Button size="xs" leftSection={<IconBellPlus size={16} />} onClick={openNew}>
          <Trans>Add connection</Trans>
        </Button>
      </Group>
      <Text size="sm" c="var(--ink-3)" mb="md">
        <Trans>Send events to Discord or a webhook. Each connection picks its own events.</Trans>
      </Text>

      {connections && connections.length > 0 ? (
        <Table>
          <Table.Thead>
            <Table.Tr>
              <Table.Th><Trans>Name</Trans></Table.Th>
              <Table.Th><Trans>Type</Trans></Table.Th>
              <Table.Th><Trans>Status</Trans></Table.Th>
              <Table.Th />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {connections.map((n) => (
              <Table.Tr key={n.id}>
                <Table.Td>{n.name}</Table.Td>
                <Table.Td>
                  <Badge size="sm" variant="light">
                    {renderLabel(NOTIFICATION_TYPE_LABELS[n.type])}
                  </Badge>
                </Table.Td>
                <Table.Td>
                  <Badge size="sm" variant="light" color={n.enabled ? 'var(--ok)' : 'var(--neutral)'}>
                    {n.enabled ? <Trans>Enabled</Trans> : <Trans>Disabled</Trans>}
                  </Badge>
                </Table.Td>
                <Table.Td>
                  <Group gap="xs" justify="flex-end" wrap="nowrap">
                    <ActionIcon variant="subtle" onClick={() => openEdit(n)} aria-label={t`Edit connection`}>
                      <IconPencil size={16} />
                    </ActionIcon>
                    <ActionIcon
                      variant="subtle"
                      color="var(--danger)"
                      onClick={() => remove.mutate(n.id)}
                      aria-label={t`Delete connection`}
                    >
                      <IconTrash size={16} />
                    </ActionIcon>
                  </Group>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      ) : (
        <Text size="sm" c="var(--ink-3)">
          <Trans>No notification connections yet.</Trans>
        </Text>
      )}

      <Modal
        opened={editing !== null}
        onClose={close}
        title={editing?.id === null ? t`Add connection` : t`Edit connection`}
        centered
      >
        {form && (
          <Stack>
            <TextInput
              label={t`Name`}
              placeholder={t`My Discord server`}
              value={form.name}
              onChange={(e) => setForm({ name: e.currentTarget.value })}
            />
            <Select
              label={t`Type`}
              data={typeOptions}
              value={form.type}
              onChange={(v) => v && setForm({ type: v as NotificationType })}
              allowDeselect={false}
            />

            {form.type === 'Discord' ? (
              <TextInput
                label={t`Webhook URL`}
                placeholder="https://discord.com/api/webhooks/..."
                value={form.config.webhookUrl ?? ''}
                onChange={(e) => setConfig({ webhookUrl: e.currentTarget.value || null })}
              />
            ) : (
              <>
                <TextInput
                  label={t`URL`}
                  placeholder="https://example.com/hook"
                  value={form.config.url ?? ''}
                  onChange={(e) => setConfig({ url: e.currentTarget.value || null })}
                />
                <PasswordInput
                  label={t`Bearer token (optional)`}
                  value={form.config.bearerToken ?? ''}
                  onChange={(e) => setConfig({ bearerToken: e.currentTarget.value || null })}
                />
              </>
            )}

            <Switch
              label={t`Enabled`}
              checked={form.enabled}
              onChange={(e) => setForm({ enabled: e.currentTarget.checked })}
            />

            <Text size="sm" fw={600} mt="xs">
              <Trans>Events</Trans>
            </Text>
            {EVENT_FIELDS.map((f) => (
              <Switch
                key={f.key}
                label={renderLabel(f.label)}
                description={renderLabel(f.description)}
                checked={form.events[f.key]}
                onChange={(e) => setEvent(f.key, e.currentTarget.checked)}
              />
            ))}

            <Group justify="space-between" mt="sm">
              <Button variant="default" loading={test.isPending} onClick={runTest}>
                <Trans>Test</Trans>
              </Button>
              <Group>
                <Button variant="subtle" onClick={close}>
                  <Trans>Cancel</Trans>
                </Button>
                <Button loading={create.isPending || update.isPending} onClick={save}>
                  <Trans>Save</Trans>
                </Button>
              </Group>
            </Group>
          </Stack>
        )}
      </Modal>
    </Panel>
  )
}
