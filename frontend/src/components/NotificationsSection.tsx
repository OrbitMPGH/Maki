import { useMemo, useState, type ComponentType } from 'react'
import {
  ActionIcon,
  Badge,
  Button,
  Group,
  Modal,
  NumberInput,
  PasswordInput,
  Select,
  Stack,
  Switch,
  Table,
  Text,
  TextInput,
  Title,
} from '@mantine/core'
import {
  IconBell,
  IconBellPlus,
  IconBellRinging,
  IconBrandDiscord,
  IconBrandSlack,
  IconBrandTelegram,
  IconBroadcast,
  IconDeviceMobileMessage,
  IconPencil,
  IconServer,
  IconTrash,
  IconWebhook,
} from '@tabler/icons-react'
import { notifications as toast } from '@mantine/notifications'
import {
  useCreateNotification,
  useDeleteNotification,
  useNotificationProviders,
  useNotifications,
  useTestNotification,
  useUpdateNotification,
} from '../api/hooks'
import type {
  NotificationDto,
  NotificationFieldDescriptor,
  NotificationProviderDescriptor,
  NotificationRequest,
  NotificationType,
} from '../api/types'
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
 * Product names are never translated; "Webhook" is a generic connector kind and is. Descriptors
 * rather than plain strings because the module evaluates once and would freeze the language.
 */
const TYPE_LABELS: Record<NotificationType, MessageDescriptor | string> = {
  Discord: 'Discord',
  Webhook: msg`Webhook`,
  Telegram: 'Telegram',
  Notifiarr: 'Notifiarr',
  Ntfy: 'ntfy',
  Gotify: 'Gotify',
  Pushover: 'Pushover',
  Apprise: 'Apprise',
  SlackWebhook: 'Slack / Mattermost',
}

const TYPE_STYLE: Record<NotificationType, { icon: ComponentType<{ size?: number }>; color: string }> = {
  Discord: { icon: IconBrandDiscord, color: 'var(--info)' },
  Webhook: { icon: IconWebhook, color: 'var(--neutral)' },
  Telegram: { icon: IconBrandTelegram, color: '#229ED9' },
  Notifiarr: { icon: IconBroadcast, color: '#9b59b6' },
  Ntfy: { icon: IconBellRinging, color: '#317f6f' },
  Gotify: { icon: IconServer, color: '#4b8dd9' },
  Pushover: { icon: IconDeviceMobileMessage, color: '#249DF1' },
  Apprise: { icon: IconBell, color: '#f39c12' },
  SlackWebhook: { icon: IconBrandSlack, color: '#E01E5A' },
}

const FALLBACK_STYLE = { icon: IconBell, color: 'var(--neutral)' }

// The server may know a type this build does not, so both lookups fall back rather than trust the union.
const typeStyle = (type: NotificationType) => TYPE_STYLE[type] ?? FALLBACK_STYLE
const typeLabel = (type: NotificationType): MessageDescriptor | string => TYPE_LABELS[type] ?? type

interface FieldCopy {
  label: MessageDescriptor
  description?: MessageDescriptor
}

/** Looked up as `${type}.${key}` first, then by `key` alone, so shared keys carry one label. */
const FIELD_COPY: Record<string, FieldCopy> = {
  webhookUrl: { label: msg`Webhook URL` },
  url: { label: msg`URL` },
  bearerToken: { label: msg`Bearer token` },
  botToken: { label: msg`Bot token`, description: msg`The token @BotFather gave you.` },
  chatId: { label: msg`Chat ID`, description: msg`User, group or channel ID. Public channels can use @name.` },
  threadId: { label: msg`Topic ID`, description: msg`Posts into one topic of a forum group.` },
  silent: { label: msg`Send silently`, description: msg`Delivers without a notification sound.` },
  apiKey: { label: msg`API key` },
  channelId: { label: msg`Discord channel ID`, description: msg`Notifiarr posts here through its Discord bot.` },
  serverUrl: { label: msg`Server URL` },
  topic: { label: msg`Topic` },
  token: { label: msg`Access token` },
  priority: { label: msg`Priority` },
  appToken: { label: msg`Application token` },
  userKey: { label: msg`User key` },
  device: { label: msg`Device`, description: msg`Leave empty to send to every device.` },
  configKey: { label: msg`Config key`, description: msg`Uses a configuration saved on the Apprise server.` },
  urls: { label: msg`Notification URLs`, description: msg`Comma separated Apprise URLs, used when there is no config key.` },
  tag: { label: msg`Tag`, description: msg`Only notify services with this tag.` },
  channel: { label: msg`Channel`, description: msg`Overrides the default channel of the webhook.` },
  'Webhook.url': { label: msg`URL`, description: msg`Receives a JSON POST for each event.` },
  'SlackWebhook.webhookUrl': { label: msg`Webhook URL`, description: msg`An incoming webhook from Slack or Mattermost.` },
  'Ntfy.token': { label: msg`Access token`, description: msg`Only needed for protected topics.` },
  'Ntfy.priority': { label: msg`Priority`, description: msg`1 (lowest) to 5 (highest).` },
  'Gotify.appToken': { label: msg`Application token`, description: msg`Created under Apps in Gotify.` },
  'Gotify.priority': { label: msg`Priority`, description: msg`Higher numbers are more intrusive.` },
  'Pushover.appToken': { label: msg`Application token`, description: msg`The API token of your Pushover application.` },
  'Pushover.userKey': { label: msg`User key`, description: msg`Your user or group key.` },
  'Pushover.priority': { label: msg`Priority`, description: msg`-2 (lowest) to 2 (emergency).` },
}

/** Looked up by `${type}`, same approach as `TYPE_LABELS`, falling back to a generic placeholder. */
const NAME_PLACEHOLDERS: Partial<Record<NotificationType, MessageDescriptor>> = {
  Discord: msg`My Discord server`,
  Telegram: msg`My phone`,
  Ntfy: msg`Home server`,
  SlackWebhook: msg`Team channel`,
}
const GENERIC_NAME_PLACEHOLDER = msg`My connection`
const namePlaceholder = (type: NotificationType): MessageDescriptor => NAME_PLACEHOLDERS[type] ?? GENERIC_NAME_PLACEHOLDER

function useTypeOptions(providers: NotificationProviderDescriptor[] | undefined, current: NotificationType | undefined) {
  const renderLabel = useLabel()
  const { i18n } = useLingui()
  return useMemo(() => {
    const types = (providers ?? []).map((p) => p.type)
    if (current && !types.includes(current)) types.push(current)
    return types.map((value) => ({ value, label: renderLabel(typeLabel(value)) }))
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [providers, current, renderLabel, i18n.locale])
}

const EMPTY_EVENTS: NotificationRequest['events'] = {
  chapterDownloaded: true,
  downloadFailed: true,
  newChapterAvailable: false,
  importCompleted: false,
  healthIssue: false,
  updateAvailable: false,
}

function toRequest(n: NotificationDto): NotificationRequest {
  return { name: n.name, type: n.type, enabled: n.enabled, config: { ...n.config }, events: n.events }
}

const isBlank = (v: string | undefined) => !v || v.trim() === ''

function ConfigField({
  type,
  field,
  value,
  onChange,
}: {
  type: NotificationType
  field: NotificationFieldDescriptor
  value: string | undefined
  onChange: (value: string) => void
}) {
  const renderLabel = useLabel()
  const { t } = useLingui()
  const copy = FIELD_COPY[`${type}.${field.key}`] ?? FIELD_COPY[field.key]
  const label = copy ? renderLabel(copy.label) : field.key
  const description = copy?.description ? renderLabel(copy.description) : undefined
  const common = {
    label,
    description,
    required: field.required,
    placeholder: field.placeholder ?? undefined,
  }

  switch (field.kind) {
    case 'Secret':
      return <PasswordInput {...common} value={value ?? ''} onChange={(e) => onChange(e.currentTarget.value)} />
    case 'Number': {
      const { min, max } = field
      const rangeDescription =
        min != null && max != null
          ? t`${min} to ${max}`
          : min != null
            ? t`${min} or more`
            : max != null
              ? t`Up to ${max}`
              : undefined
      return (
        <NumberInput
          {...common}
          description={common.description ?? rangeDescription}
          min={min ?? undefined}
          max={max ?? undefined}
          allowDecimal={false}
          value={value ?? ''}
          onChange={(v) => onChange(v === '' ? '' : String(v))}
        />
      )
    }
    case 'Boolean':
      return (
        <Switch
          label={label}
          description={description}
          checked={value === 'true'}
          onChange={(e) => onChange(e.currentTarget.checked ? 'true' : 'false')}
        />
      )
    default:
      return <TextInput {...common} value={value ?? ''} onChange={(e) => onChange(e.currentTarget.value)} />
  }
}

function TypeIcon({ type }: { type: NotificationType }) {
  const { icon: Icon, color } = typeStyle(type)
  return (
    <span style={{ color, display: 'inline-flex' }}>
      <Icon size={16} />
    </span>
  )
}

function TypeBadge({ type }: { type: NotificationType }) {
  const renderLabel = useLabel()
  const { icon: Icon, color } = typeStyle(type)
  return (
    <Badge size="sm" variant="light" color={color} leftSection={<Icon size={12} />}>
      {renderLabel(typeLabel(type))}
    </Badge>
  )
}

export function NotificationsSection() {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const { data: providers } = useNotificationProviders()
  const { data: connections } = useNotifications()
  const create = useCreateNotification()
  const update = useUpdateNotification()
  const remove = useDeleteNotification()
  const test = useTestNotification()

  const [editing, setEditing] = useState<{ id: number | null; form: NotificationRequest } | null>(null)
  const typeOptions = useTypeOptions(providers, editing?.form.type)
  const descriptor = providers?.find((p) => p.type === editing?.form.type)

  const openNew = () => {
    if (!providers || providers.length === 0) return
    setEditing({
      id: null,
      form: { name: '', type: providers[0].type, enabled: true, config: {}, events: { ...EMPTY_EVENTS } },
    })
  }
  const openEdit = (n: NotificationDto) => setEditing({ id: n.id, form: toRequest(n) })
  const close = () => setEditing(null)

  const setForm = (patch: Partial<NotificationRequest>) =>
    setEditing((e) => (e ? { ...e, form: { ...e.form, ...patch } } : e))
  const setConfig = (key: string, value: string) =>
    setEditing((e) => {
      if (!e) return e
      const config = { ...e.form.config }
      if (value === '') delete config[key]
      else config[key] = value
      return { ...e, form: { ...e.form, config } }
    })
  const setEvent = (key: keyof NotificationRequest['events'], value: boolean) =>
    setEditing((e) => (e ? { ...e, form: { ...e.form, events: { ...e.form.events, [key]: value } } } : e))

  const save = () => {
    if (!editing) return
    const onSuccess = () => {
      toast.show({ message: now`Saved`, color: 'var(--ok)' })
      close()
    }
    const onError = (err: Error) => toast.show({ title: now`Save failed`, message: err.message, color: 'var(--danger)' })
    if (editing.id === null) create.mutate(editing.form, { onSuccess, onError })
    else update.mutate({ id: editing.id, value: editing.form }, { onSuccess, onError })
  }

  const runTest = () => {
    if (!editing) return
    test.mutate(editing.form, {
      onSuccess: (r) =>
        toast.show({
          message: r.success ? now`Test notification sent` : now`Test failed`,
          color: r.success ? 'var(--ok)' : 'var(--danger)',
        }),
      onError: (err: Error) => toast.show({ title: now`Test failed`, message: err.message, color: 'var(--danger)' }),
    })
  }

  const form = editing?.form
  const missingRequired =
    !form || isBlank(form.name) || (descriptor?.fields ?? []).some((f) => f.required && isBlank(form.config[f.key]))

  return (
    <Panel>
      <Group justify="space-between" mb="sm">
        <Title order={4}><Trans>Outbound notifications</Trans></Title>
        <Button
          size="xs"
          leftSection={<IconBellPlus size={16} />}
          onClick={openNew}
          disabled={!providers || providers.length === 0}
        >
          <Trans>Add connection</Trans>
        </Button>
      </Group>
      <Text size="sm" c="var(--ink-3)" mb="md">
        <Trans>Send events to chat apps, push services or a webhook. Each connection picks its own events.</Trans>
      </Text>

      {connections && connections.length > 0 ? (
        <Table className="panel-table ops-table">
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
                  <TypeBadge type={n.type} />
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
              placeholder={renderLabel(namePlaceholder(form.type))}
              required
              value={form.name}
              onChange={(e) => setForm({ name: e.currentTarget.value })}
            />
            <Select
              label={t`Type`}
              data={typeOptions}
              value={form.type}
              leftSection={<TypeIcon type={form.type} />}
              onChange={(v) => v && v !== form.type && setForm({ type: v as NotificationType, config: {} })}
              allowDeselect={false}
            />

            {descriptor?.fields.map((field) => (
              <ConfigField
                key={field.key}
                type={form.type}
                field={field}
                value={form.config[field.key]}
                onChange={(value) => setConfig(field.key, value)}
              />
            ))}

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
              <Button variant="default" loading={test.isPending} disabled={missingRequired} onClick={runTest}>
                <Trans>Test</Trans>
              </Button>
              <Group>
                <Button variant="subtle" onClick={close}>
                  <Trans>Cancel</Trans>
                </Button>
                <Button loading={create.isPending || update.isPending} disabled={missingRequired} onClick={save}>
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
