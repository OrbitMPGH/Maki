import { Card, Divider, Group, SegmentedControl, Stack, Switch, Text, Title } from '@mantine/core'
import {
  INBOX_ADMIN_ONLY,
  INBOX_CATEGORIES,
  INBOX_TYPE_LABELS,
  useInboxPrefs,
  useSaveInboxPrefs,
  type InboxEventType,
} from '../../api/inbox'
import { SERIES_DEFAULT_OPTIONS } from '../ui/seriesNotifications'
import { useAuth } from '../../auth/AuthProvider'

/**
 * Per-event switches for the in-app notification inbox.
 * <p>
 * Not to be confused with the Notifications card on the Integrations tab, which manages the
 * instance-wide Discord and webhook connections. Those are admin-only and unaffected by anything
 * here — the two systems are deliberately separate so a chat channel isn't flooded with one
 * person's achievements.
 */
export function NotificationPrefsSection() {
  const { can } = useAuth()
  const isAdmin = can('Admin')

  const { data: prefs } = useInboxPrefs()
  const save = useSaveInboxPrefs()

  if (!prefs) {
    return null
  }

  const categories = INBOX_CATEGORIES.filter((c) => !c.adminOnly || isAdmin)

  function setType(type: InboxEventType, enabled: boolean) {
    if (!prefs) return
    save.mutate({ ...prefs, types: { ...prefs.types, [type]: enabled } })
  }

  return (
    <Card withBorder radius="md" padding="lg">
      <Title order={4}>Notifications</Title>
      <Text size="sm" c="dimmed" mt={4}>
        What lands in your bell. These are yours alone, they don't affect the Discord and webhook
        connections on the Integrations tab.
      </Text>

      <Switch
        mt="md"
        label="Show a popup when a notification arrives"
        description="Turn this off to only see them in the bell."
        checked={prefs.toasts}
        onChange={(e) => save.mutate({ ...prefs, toasts: e.currentTarget.checked })}
      />

      <Divider my="md" />

      <Text size="sm" fw={500}>
        Tell me about new chapters for
      </Text>
      <Text size="xs" c="dimmed" mb="xs">
        The starting point for every series. Any series can be set to something else from its own
        page, or for a whole selection at once from the Library's Select mode.
      </Text>
      <SegmentedControl
        fullWidth
        value={SERIES_DEFAULT_OPTIONS.some((o) => o.value === prefs.seriesDefault)
          ? prefs.seriesDefault
          : 'All'}
        onChange={(seriesDefault) => save.mutate({ ...prefs, seriesDefault })}
        data={SERIES_DEFAULT_OPTIONS}
      />

      {categories.map((category) => (
        <div key={category.label}>
          <Divider my="md" />
          <Text size="xs" fw={700} tt="uppercase" c="dimmed" mb="xs" style={{ letterSpacing: '0.08em' }}>
            {category.label}
          </Text>
          <Stack gap="xs">
            {category.types
              // Belt and braces: the category flag already hides the System block from a reader,
              // but a type could be marked admin-only inside a mixed category later.
              .filter((type) => isAdmin || !INBOX_ADMIN_ONLY.includes(type))
              .map((type) => (
                <Group key={type} justify="space-between" wrap="nowrap" gap="md">
                  <Text size="sm">{INBOX_TYPE_LABELS[type]}</Text>
                  <Switch
                    checked={prefs.types[type] ?? true}
                    onChange={(e) => setType(type, e.currentTarget.checked)}
                  />
                </Group>
              ))}
          </Stack>
        </div>
      ))}
    </Card>
  )
}
