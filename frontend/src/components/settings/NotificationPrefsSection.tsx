import { Checkbox, Divider, SegmentedControl, Switch, Text } from '@mantine/core'
import { IconBell, IconDeviceMobile } from '@tabler/icons-react'
import { SettingsSection } from '../../pages/settings/SettingsSection'
import {
  INBOX_ADMIN_ONLY,
  visibleInboxCategories,
  INBOX_TYPE_DESCRIPTIONS,
  INBOX_TYPE_LABELS,
  useInboxPrefs,
  useSaveInboxPrefs,
  type InboxEventType,
} from '../../api/inbox'
import { usePushPrefs, useSavePushPrefs } from '../../api/push'
import { useSeriesDefaultOptions } from '../ui/seriesNotifications'
import { useAuth } from '../../auth/AuthProvider'
import { useLabel } from '../../i18n-context'
import { Trans, useLingui } from '@lingui/react/macro'

/**
 * Per-event choices for the in-app notification inbox and for push, drawn once as one table: each
 * event has a bell column and a push column. Turning on push for this device is the Push card's job.
 * <p>
 * Not to be confused with the Discord & webhooks card on the Integrations tab, which manages the
 * instance-wide Discord and webhook connections. Those are admin-only and unaffected by anything
 * here. The two systems are deliberately separate so a chat channel isn't flooded with one
 * person's achievements.
 */
export function NotificationPrefsSection() {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const { can, singleUser } = useAuth()
  const isAdmin = can('Admin')
  const seriesDefaultOptions = useSeriesDefaultOptions()

  const { data: prefs } = useInboxPrefs()
  const save = useSaveInboxPrefs()
  const { data: pushPrefs } = usePushPrefs()
  const savePush = useSavePushPrefs()

  if (!prefs) {
    return null
  }

  const categories = visibleInboxCategories(isAdmin, singleUser)

  function setType(type: InboxEventType, enabled: boolean) {
    save.mutate({ types: { [type]: enabled } })
  }

  const pushed = savePush.isPending && savePush.variables ? savePush.variables : (pushPrefs?.types ?? [])
  function setPush(type: InboxEventType, on: boolean) {
    savePush.mutate(on ? [...pushed.filter((s) => s !== type), type] : pushed.filter((s) => s !== type))
  }

  return (
    <SettingsSection
      id="notification-prefs"
      title={<Trans>Notifications</Trans>}
    >
      <Switch
        label={t`Show a popup when a notification arrives`}
        checked={prefs.toasts}
        onChange={(e) => save.mutate({ toasts: e.currentTarget.checked })}
      />

      <Divider my="md" />

      <Text size="sm" fw={500}>
        <Trans>Which series notify me</Trans>
      </Text>
      <Text size="xs" c="var(--ink-3)" mb="xs">
        <Trans>Any series can be set differently from its own page.</Trans>
      </Text>
      <SegmentedControl
        aria-label={t`Which series notify me`}
        fullWidth
        value={seriesDefaultOptions.some((o) => o.value === prefs.seriesDefault)
          ? prefs.seriesDefault
          : 'All'}
        onChange={(seriesDefault) => save.mutate({ seriesDefault })}
        data={seriesDefaultOptions}
      />

      <Divider my="md" />

      <table className="notify-matrix">
        <thead>
          <tr>
            <th scope="col">
              <Trans>Event</Trans>
            </th>
            <th scope="col" className="notify-matrix-check">
              <span className="notify-matrix-head">
                <IconBell size={14} aria-hidden />
                <Trans>Bell</Trans>
              </span>
            </th>
            <th scope="col" className="notify-matrix-check">
              <span className="notify-matrix-head">
                <IconDeviceMobile size={14} aria-hidden />
                <Trans>Push</Trans>
              </span>
            </th>
          </tr>
        </thead>
        {categories.map((category) => (
          <tbody key={category.id}>
            <tr className="notify-matrix-category">
              <th scope="rowgroup" colSpan={3}>
                {renderLabel(category.label)}
              </th>
            </tr>
            {category.types
              // Belt and braces: the category flag already hides the System block from a reader,
              // but a type could be marked admin-only inside a mixed category later.
              .filter((type) => isAdmin || !INBOX_ADMIN_ONLY.includes(type))
              .map((type) => {
                const label = renderLabel(INBOX_TYPE_LABELS[type])
                const description = INBOX_TYPE_DESCRIPTIONS[type]
                const bellOn = prefs.types[type] ?? true
                return (
                  <tr key={type}>
                    <th scope="row">
                      <Text size="sm">{label}</Text>
                      {description && (
                        <Text size="xs" c="var(--ink-3)">
                          {renderLabel(description)}
                        </Text>
                      )}
                    </th>
                    <td className="notify-matrix-check">
                      <label className="notify-matrix-hit">
                        <Checkbox
                          aria-label={t`${label} in the bell`}
                          checked={bellOn}
                          onChange={(e) => setType(type, e.currentTarget.checked)}
                        />
                      </label>
                    </td>
                    <td className="notify-matrix-check">
                      <label className="notify-matrix-hit" data-disabled={!pushPrefs || !bellOn || undefined}>
                        <Checkbox
                          aria-label={t`${label} as a push notification`}
                          checked={bellOn && pushed.includes(type)}
                          disabled={!pushPrefs || !bellOn}
                          onChange={(e) => setPush(type, e.currentTarget.checked)}
                        />
                      </label>
                    </td>
                  </tr>
                )
              })}
          </tbody>
        ))}
      </table>
      <Text size="xs" c="var(--ink-3)" mt="sm">
        <Trans>Push needs Bell on, and push turned on for the device below.</Trans>
      </Text>
    </SettingsSection>
  )
}
