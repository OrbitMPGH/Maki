import { Divider, Group, SegmentedControl, Stack, Switch, Text, Title } from '@mantine/core'
import { Panel } from '../ui/Panel'
import {
  INBOX_ADMIN_ONLY,
  INBOX_CATEGORIES,
  INBOX_TYPE_LABELS,
  useInboxPrefs,
  useSaveInboxPrefs,
  type InboxEventType,
} from '../../api/inbox'
import { useSeriesDefaultOptions } from '../ui/seriesNotifications'
import { useAuth } from '../../auth/AuthProvider'
import { useLabel } from '../../i18n-context'
import { Trans, useLingui } from '@lingui/react/macro'

/**
 * Per-event switches for the in-app notification inbox.
 * <p>
 * Not to be confused with the Discord & webhooks card on the Integrations tab, which manages the
 * instance-wide Discord and webhook connections. Those are admin-only and unaffected by anything
 * here — the two systems are deliberately separate so a chat channel isn't flooded with one
 * person's achievements.
 */
export function NotificationPrefsSection() {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const { can } = useAuth()
  const isAdmin = can('Admin')
  const seriesDefaultOptions = useSeriesDefaultOptions()

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
    <Panel>
      <Title order={4}>
        <Trans>Notifications</Trans>
      </Title>
      <Text size="sm" c="var(--ink-3)" mt={4}>
        <Trans>What lands in your bell. These settings only affect you.</Trans>
      </Text>

      <Switch
        mt="md"
        label={t`Show a popup when a notification arrives`}
        description={t`Turn this off to only see them in the bell.`}
        checked={prefs.toasts}
        onChange={(e) => save.mutate({ ...prefs, toasts: e.currentTarget.checked })}
      />

      <Divider my="md" />

      <Text size="sm" fw={500}>
        <Trans>Tell me about new chapters for</Trans>
      </Text>
      <Text size="xs" c="var(--ink-3)" mb="xs">
        <Trans>
          The starting point for every series. Any series can be set to something else from its own
          page, or for a whole selection at once from the Library's Select mode.
        </Trans>
      </Text>
      <SegmentedControl
        fullWidth
        value={seriesDefaultOptions.some((o) => o.value === prefs.seriesDefault)
          ? prefs.seriesDefault
          : 'All'}
        onChange={(seriesDefault) => save.mutate({ ...prefs, seriesDefault })}
        data={seriesDefaultOptions}
      />

      {categories.map((category) => (
        <div key={category.id}>
          <Divider my="md" />
          <Text size="xs" fw={700} tt="uppercase" c="var(--ink-3)" mb="xs" style={{ letterSpacing: '0.08em' }}>
            {renderLabel(category.label)}
          </Text>
          <Stack gap="xs">
            {category.types
              // Belt and braces: the category flag already hides the System block from a reader,
              // but a type could be marked admin-only inside a mixed category later.
              .filter((type) => isAdmin || !INBOX_ADMIN_ONLY.includes(type))
              .map((type) => (
                <Group key={type} justify="space-between" wrap="nowrap" gap="md">
                  <Text size="sm">{renderLabel(INBOX_TYPE_LABELS[type])}</Text>
                  <Switch
                    checked={prefs.types[type] ?? true}
                    onChange={(e) => setType(type, e.currentTarget.checked)}
                  />
                </Group>
              ))}
          </Stack>
        </div>
      ))}
    </Panel>
  )
}
