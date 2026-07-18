import { useState } from 'react'
import {
  ActionIcon,
  Badge,
  Button,
  Card,
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

const EVENT_FIELDS: { key: keyof NotificationRequest['events']; label: string; description: string }[] = [
  { key: 'chapterDownloaded', label: 'Chapter downloaded', description: 'A chapter finished downloading and imported.' },
  { key: 'downloadFailed', label: 'Download failed', description: 'A chapter download failed.' },
  { key: 'newChapterAvailable', label: 'New chapter available', description: 'A refresh queued new chapters for a monitored series.' },
  { key: 'importCompleted', label: 'Import completed', description: 'A library import folder finished.' },
  { key: 'healthIssue', label: 'Health issue', description: 'A new system health problem was detected.' },
]

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
  },
}

function toRequest(n: NotificationDto): NotificationRequest {
  return { name: n.name, type: n.type, enabled: n.enabled, config: n.config, events: n.events }
}

export function NotificationsSection() {
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
      toast.show({ message: 'Saved', color: 'green' })
      close()
    }
    const onError = (err: Error) => toast.show({ title: 'Save failed', message: err.message, color: 'red' })
    if (editing.id === null) create.mutate(editing.form, { onSuccess, onError })
    else update.mutate({ id: editing.id, value: editing.form }, { onSuccess, onError })
  }

  const runTest = () => {
    if (!editing) return
    test.mutate(editing.form, {
      onSuccess: (r) =>
        toast.show({
          message: r.success ? 'Test notification sent' : 'Test failed',
          color: r.success ? 'green' : 'red',
        }),
      onError: (err: Error) => toast.show({ title: 'Test failed', message: err.message, color: 'red' }),
    })
  }

  const form = editing?.form

  return (
    <Card withBorder radius="md" padding="md">
      <Group justify="space-between" mb="sm">
        <Title order={4}>Notifications</Title>
        <Button size="xs" leftSection={<IconBellPlus size={16} />} onClick={openNew}>
          Add connection
        </Button>
      </Group>
      <Text size="sm" c="dimmed" mb="md">
        Send alerts to Discord or a generic webhook when chapters download, downloads fail, new
        chapters appear, imports finish, or a health issue is detected. Each connection chooses which
        events it fires on.
      </Text>

      {connections && connections.length > 0 ? (
        <Table>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Name</Table.Th>
              <Table.Th>Type</Table.Th>
              <Table.Th>Status</Table.Th>
              <Table.Th />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {connections.map((n) => (
              <Table.Tr key={n.id}>
                <Table.Td>{n.name}</Table.Td>
                <Table.Td>
                  <Badge size="sm" variant="light">
                    {n.type}
                  </Badge>
                </Table.Td>
                <Table.Td>
                  <Badge size="sm" variant="light" color={n.enabled ? 'green' : 'gray'}>
                    {n.enabled ? 'Enabled' : 'Disabled'}
                  </Badge>
                </Table.Td>
                <Table.Td>
                  <Group gap="xs" justify="flex-end" wrap="nowrap">
                    <ActionIcon variant="subtle" onClick={() => openEdit(n)} aria-label="Edit connection">
                      <IconPencil size={16} />
                    </ActionIcon>
                    <ActionIcon
                      variant="subtle"
                      color="red"
                      onClick={() => remove.mutate(n.id)}
                      aria-label="Delete connection"
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
        <Text size="sm" c="dimmed">
          No notification connections yet.
        </Text>
      )}

      <Modal
        opened={editing !== null}
        onClose={close}
        title={editing?.id === null ? 'Add connection' : 'Edit connection'}
        centered
      >
        {form && (
          <Stack>
            <TextInput
              label="Name"
              placeholder="My Discord server"
              value={form.name}
              onChange={(e) => setForm({ name: e.currentTarget.value })}
            />
            <Select
              label="Type"
              data={['Discord', 'Webhook']}
              value={form.type}
              onChange={(v) => v && setForm({ type: v as NotificationType })}
              allowDeselect={false}
            />

            {form.type === 'Discord' ? (
              <TextInput
                label="Webhook URL"
                placeholder="https://discord.com/api/webhooks/..."
                value={form.config.webhookUrl ?? ''}
                onChange={(e) => setConfig({ webhookUrl: e.currentTarget.value || null })}
              />
            ) : (
              <>
                <TextInput
                  label="URL"
                  placeholder="https://example.com/hook"
                  value={form.config.url ?? ''}
                  onChange={(e) => setConfig({ url: e.currentTarget.value || null })}
                />
                <PasswordInput
                  label="Bearer token (optional)"
                  value={form.config.bearerToken ?? ''}
                  onChange={(e) => setConfig({ bearerToken: e.currentTarget.value || null })}
                />
              </>
            )}

            <Switch
              label="Enabled"
              checked={form.enabled}
              onChange={(e) => setForm({ enabled: e.currentTarget.checked })}
            />

            <Text size="sm" fw={600} mt="xs">
              Events
            </Text>
            {EVENT_FIELDS.map((f) => (
              <Switch
                key={f.key}
                label={f.label}
                description={f.description}
                checked={form.events[f.key]}
                onChange={(e) => setEvent(f.key, e.currentTarget.checked)}
              />
            ))}

            <Group justify="space-between" mt="sm">
              <Button variant="default" loading={test.isPending} onClick={runTest}>
                Test
              </Button>
              <Group>
                <Button variant="subtle" onClick={close}>
                  Cancel
                </Button>
                <Button loading={create.isPending || update.isPending} onClick={save}>
                  Save
                </Button>
              </Group>
            </Group>
          </Stack>
        )}
      </Modal>
    </Card>
  )
}
